// The two ways the app talks to the board, behind one interface:
//  - AscomSession: through the ASCOM LocalServer (late-bound COM, only the AstroUniBoard:* Action
//    contract), so it works while N.I.N.A. is connected to the same driver.
//  - DirectSession: its own BoardController on the COM port, for bench work without a driver. The
//    port is then taken: N.I.N.A. cannot connect at the same time.
// Every call may block for a few seconds; the form calls them off the UI thread.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using AstroUniBoard.Core;

namespace AstroUniBoard.DebugApp
{
    internal enum BoardAction { HaltAll, ClearError, SaveSettings, ImuHome, ImuCalibrate, Reboot, EnterDfu }

    internal sealed class LogEntry
    {
        public long Seq;
        public DateTime Time;
        public string Text;
    }

    internal interface IBoardSession : IDisposable
    {
        /// <summary>One line for the title and the status bar.</summary>
        string Description { get; }
        bool IsDirect { get; }
        /// <summary>Connects (throws with a readable message when that is not possible).</summary>
        void Open();
        BoardView Snapshot();
        /// <summary>Sends ":NN...#"; returns the reply without '#' ("OK", "ERR,7", "S,1,...") or "ERROR: message".</summary>
        string Raw(string command);
        /// <summary>"OK" or "ERROR: message".</summary>
        string Do(BoardAction action);
        /// <summary>Log entries with Seq greater than <paramref name="afterSeq"/>.</summary>
        IList<LogEntry> ReadLog(long afterSeq);
        /// <summary>After a firmware update or a reboot: connect again (to the same board).</summary>
        void Reconnect();
    }

    internal static class Replies
    {
        public const string ErrorPrefix = "ERROR: ";
        public static bool IsOk(string r) { return r == "OK"; }
        public static bool IsFailure(string r) { return r == null || r.StartsWith(ErrorPrefix, StringComparison.Ordinal) || r.StartsWith("ERR,", StringComparison.Ordinal); }

        /// <summary>"ERR,7" → "ERR,7 — bad parameter ..."; anything else unchanged.</summary>
        public static string Explain(string r)
        {
            if (r == null) return "(no reply)";
            int code;
            if (r.StartsWith("ERR,", StringComparison.Ordinal) && int.TryParse(r.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out code))
                return r + " — " + BoardErrors.Describe(code);
            return r;
        }

        public static string FromCommandResult(CommandResult c) { return c.Success ? "OK" : ErrorPrefix + c.Message; }

        /// <summary>
        /// The explanation for a rotator move (:71 / :72) the board refused while its angle is
        /// unverified (#75), or null for any other command or reply.
        /// </summary>
        public static string RotatorRefusalHint(string command, string reply, StatusView status)
        {
            if (command == null || status == null || !(command.StartsWith(":71", StringComparison.Ordinal) || command.StartsWith(":72", StringComparison.Ordinal))) return null;
            int code;
            if (reply == null || !reply.StartsWith("ERR,", StringComparison.Ordinal) ||
                !int.TryParse(reply.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out code)) return null;
            return BoardErrors.IsRotatorUnverifiedRefusal(code, (StatusFlags)status.Flags) ? BoardErrors.RotatorUnverifiedHint : null;
        }
    }

    internal static class SessionExtensions
    {
        /// <summary>
        /// Halts both axes and waits until the board reports idle (before :22# / :31#, #76).
        /// Null when the board is idle, else why not. Blocking: call it off the UI thread.
        /// </summary>
        public static string HaltAndWaitIdle(this IBoardSession s, int timeoutMs = 10000)
        {
            string r = s.Do(BoardAction.HaltAll);
            if (!Replies.IsOk(r)) return "halting failed: " + r;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                BoardView v = s.Snapshot();
                if (!v.IsConnected) return "the board is not connected";
                if (v.Status != null && !v.Status.Busy) return null;
                if (sw.ElapsedMilliseconds >= timeoutMs) return "the board was still busy " + timeoutMs / 1000 + " s after halting";
                System.Threading.Thread.Sleep(100);
            }
        }
    }

    // ---------------------------------------------------------------------------------- direct

    internal sealed class DirectSession : IBoardSession
    {
        private readonly BoardController controller;
        private readonly object logLock = new object();
        private readonly List<LogEntry> log = new List<LogEntry>();
        private long seq;
        private const int MaxLog = 5000;

        /// <param name="port">COM port, or null for automatic USB detection (Core ignores other 1209:0001 devices such as an OpenWheel).</param>
        public DirectSession(string port, Func<string, ILink> linkFactory = null, IUsbSerialEnumerator enumerator = null, BoardOptions options = null)
        {
            var opt = options ?? new BoardOptions();
            opt.ManualPort = string.IsNullOrEmpty(port) ? null : port;
            controller = new BoardController(opt, linkFactory, enumerator);
            controller.Log += AddLog;
            Description = "Direct " + (port ?? "(automatic)");
        }

        public string Description { get; private set; }
        public bool IsDirect { get { return true; } }
        internal BoardController Controller { get { return controller; } }

        public void Open()
        {
            AddLog("connecting directly to " + (controller.Options.ManualPort ?? "the first AstroUniBoard on USB"));
            controller.Connect();
            if (!controller.WaitConnected(8000))
            {
                string why = controller.Snapshot.Problem ?? "no answer";
                controller.Disconnect();
                throw new InvalidOperationException("could not connect: " + why);
            }
            // Core publishes Connected before it has read the axis settings; give it a moment so the
            // first snapshot is complete
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000 && !SettingsRead(controller.Snapshot)) System.Threading.Thread.Sleep(20);
            BoardSnapshot s = controller.Snapshot;
            Description = "Direct " + s.PortName + " — " + s.Identity.Model + " " + s.Identity.Firmware + ", serial " + s.Identity.Serial;
        }

        private static bool SettingsRead(BoardSnapshot s)
        {
            if (!s.IsConnected || s.Identity == null) return false;
            return (!s.Identity.Has(BoardCaps.Focuser) || s.Focuser != null) && (!s.Identity.Has(BoardCaps.Rotator) || s.Rotator != null);
        }

        public BoardView Snapshot() { return BoardView.FromDictionary(SnapshotJson.Build(controller.Snapshot)); }

        public string Raw(string command)
        {
            // halts jump the controller's queue
            if (command == Commands.HaltAll) return Replies.FromCommandResult(controller.HaltAll());
            if (command == Commands.FocuserHalt) return Replies.FromCommandResult(controller.FocuserHalt());
            if (command == Commands.RotatorHalt) return Replies.FromCommandResult(controller.RotatorHalt());
            Reply reply;
            CommandResult r = controller.Raw(command, out reply);
            if (!r.Success) return Replies.ErrorPrefix + r.Message;
            return reply.ToString();
        }

        public string Do(BoardAction action)
        {
            CommandResult r;
            switch (action)
            {
                case BoardAction.HaltAll: r = controller.HaltAll(); break;
                case BoardAction.ClearError: r = controller.ClearError(); break;
                case BoardAction.SaveSettings: r = controller.SaveSettings(); break;
                case BoardAction.ImuHome: r = controller.RotatorImuHome(); break;
                case BoardAction.ImuCalibrate: r = controller.ImuCalibrate(); break;
                case BoardAction.Reboot: r = controller.Reboot(); break;
                case BoardAction.EnterDfu: r = controller.EnterDfu(); break;
                default: return Replies.ErrorPrefix + "unknown action " + action;
            }
            return Replies.FromCommandResult(r);
        }

        public IList<LogEntry> ReadLog(long afterSeq)
        {
            lock (logLock)
            {
                var list = new List<LogEntry>();
                foreach (LogEntry e in log) if (e.Seq > afterSeq) list.Add(e);
                return list;
            }
        }

        public void Reconnect()
        {
            controller.Connect();
            controller.WaitConnected(8000);
        }

        internal void AddLog(string text)
        {
            lock (logLock)
            {
                log.Add(new LogEntry { Seq = ++seq, Time = DateTime.UtcNow, Text = text });
                if (log.Count > MaxLog) log.RemoveRange(0, log.Count - MaxLog);
            }
        }

        public void Dispose()
        {
            controller.Disconnect();
            controller.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------- ASCOM

    /// <summary>The driver's Action entry point; the COM object in real use, a loopback or fake in tests.</summary>
    internal interface IDriverChannel : IDisposable
    {
        string Name { get; }
        void Connect();
        void Disconnect();
        string Action(string name, string parameter);
    }

    internal sealed class AscomSession : IBoardSession
    {
        public const string FocuserProgId = "ASCOM.AstroUniBoard.Focuser";
        public const string ActionRaw = "AstroUniBoard:Raw";
        public const string ActionSnapshot = "AstroUniBoard:Snapshot";
        public const string ActionLog = "AstroUniBoard:Log";

        private readonly Func<IDriverChannel> factory;
        private IDriverChannel channel;

        public AscomSession(Func<IDriverChannel> factory = null)
        {
            this.factory = factory ?? (() => new ComDriverChannel(FocuserProgId));
            Description = "ASCOM driver " + FocuserProgId;
        }

        public string Description { get; private set; }
        public bool IsDirect { get { return false; } }

        public void Open()
        {
            channel = factory();
            channel.Connect();
            BoardView v = Snapshot();
            Description = "ASCOM " + channel.Name + (v.Identity != null ? " — " + v.Identity.Model + " " + v.Identity.Firmware + " on " + v.Port : " — " + v.Connection);
        }

        private IDriverChannel Channel
        {
            get
            {
                IDriverChannel c = channel;
                if (c == null) throw new InvalidOperationException("not connected to the ASCOM driver");
                return c;
            }
        }

        public BoardView Snapshot()
        {
            string json = Channel.Action(ActionSnapshot, "");
            if (json != null && json.StartsWith(Replies.ErrorPrefix, StringComparison.Ordinal)) throw new InvalidOperationException(json.Substring(Replies.ErrorPrefix.Length));
            return BoardView.FromJson(json);
        }

        public string Raw(string command)
        {
            try { return Channel.Action(ActionRaw, command); }
            catch (Exception ex) { return Replies.ErrorPrefix + ex.Message; }
        }

        public string Do(BoardAction action)
        {
            try { return Channel.Action("AstroUniBoard:" + action, ""); }
            catch (Exception ex) { return Replies.ErrorPrefix + ex.Message; }
        }

        public IList<LogEntry> ReadLog(long afterSeq)
        {
            string json = Channel.Action(ActionLog, afterSeq > 0 ? afterSeq.ToString(CultureInfo.InvariantCulture) : "");
            var list = new List<LogEntry>();
            if (string.IsNullOrEmpty(json) || json.StartsWith(Replies.ErrorPrefix, StringComparison.Ordinal)) return list;
            foreach (IDictionary<string, object> d in J.Array(new JavaScriptSerializer().DeserializeObject(json)))
            {
                long s = Convert.ToInt64(J.Get(d, "seq") ?? 0L, CultureInfo.InvariantCulture);
                if (s <= afterSeq) continue;
                // "...Z" / "+01:00" are honoured; a time without an offset (the LocalServer's) is local
                DateTime t;
                if (!DateTime.TryParse(J.Str(d, "time"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal, out t)) t = DateTime.UtcNow;
                list.Add(new LogEntry { Seq = s, Time = t, Text = J.Str(d, "text") });
            }
            return list;
        }

        public void Reconnect()
        {
            IDriverChannel c = Channel;
            try { c.Disconnect(); } catch { }
            c.Connect();
        }

        public void Dispose()
        {
            IDriverChannel c = channel;
            channel = null;
            if (c == null) return;
            try { c.Disconnect(); } catch { }
            c.Dispose();
        }
    }

    /// <summary>Late-bound COM to the installed driver: no ASCOM assemblies needed by the app.</summary>
    internal sealed class ComDriverChannel : IDriverChannel
    {
        private readonly string progId;
        private object driver;

        public ComDriverChannel(string progId) { this.progId = progId; }
        public string Name { get { return progId; } }

        public void Connect()
        {
            if (driver == null)
            {
                Type t = Type.GetTypeFromProgID(progId, false);
                if (t == null) throw new InvalidOperationException("the ASCOM driver " + progId + " is not installed — use the direct connection, or install the driver");
                driver = Activator.CreateInstance(t);
            }
            Invoke("Connected", BindingFlags.SetProperty, true);
            if (!(bool)Invoke("Connected", BindingFlags.GetProperty))
                throw new InvalidOperationException("the driver did not connect to the board");
            object actions = Invoke("SupportedActions", BindingFlags.GetProperty);
            bool found = false;
            foreach (object a in (System.Collections.IEnumerable)actions)
                if (string.Equals(a as string, AscomSession.ActionSnapshot, StringComparison.OrdinalIgnoreCase)) found = true;
            if (!found) throw new InvalidOperationException("the installed driver does not offer " + AscomSession.ActionSnapshot + " (too old?)");
        }

        public void Disconnect()
        {
            if (driver != null) Invoke("Connected", BindingFlags.SetProperty, false);
        }

        public string Action(string name, string parameter)
        {
            if (driver == null) throw new InvalidOperationException("not connected");
            return (string)Invoke("Action", BindingFlags.InvokeMethod, name, parameter ?? "");
        }

        private object Invoke(string member, BindingFlags how, params object[] args)
        {
            try { return driver.GetType().InvokeMember(member, how, null, driver, args, CultureInfo.InvariantCulture); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw new InvalidOperationException(ex.InnerException.Message, ex.InnerException);
            }
        }

        public void Dispose()
        {
            object d = driver;
            driver = null;
            if (d != null && Marshal.IsComObject(d)) Marshal.ReleaseComObject(d);
        }
    }
}
