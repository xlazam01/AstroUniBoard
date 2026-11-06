// The worker that owns the serial port (copied and adapted from OpenWheel.Core.WheelController).
// Every public member is thread-safe and bounded in time: readers get a cached snapshot, command
// callers wait at most a given time, and a device that stays silent, answers garbage or disappears
// turns into a clear "not connected" state instead of a hang. One instance serves the focuser, the
// rotator and the environment sensor (the ASCOM LocalServer shares it between its COM objects).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace AstroUniBoard.Core
{
    public enum ConnectionState
    {
        Disconnected,
        /// <summary>Looking for the board / opening the port for the first time.</summary>
        Connecting,
        Connected,
        /// <summary>Connection lost; retrying in the background.</summary>
        Reconnecting
    }

    public sealed class BoardOptions
    {
        /// <summary>PROTOCOL.md §2: hosts wait ~1 s for an answer (2 s for :23#).</summary>
        public int CommandTimeoutMs = 1000;
        public int SaveTimeoutMs = 2000;
        public int PollIdleMs = 1000;
        public int PollBusyMs = 200;
        public int HousekeepingMs = 5000;
        /// <summary>The firmware refreshes the SHT31 every 2 s.</summary>
        public int EnvironmentMs = 5000;
        /// <summary>Consecutive timeouts/garbage replies before the connection is declared lost.</summary>
        public int MaxConsecutiveFailures = 3;
        /// <summary>A focuser or rotator move that is still running after this is halted by the driver.</summary>
        public int MoveTimeoutMs = 120000;
        /// <summary>IMU homing / zero calibration (about 35 s at default speed) is halted after this.</summary>
        public int ImuTimeoutMs = 180000;
        public int ReconnectMinMs = 1000;
        public int ReconnectMaxMs = 15000;
        public bool AutoReconnect = true;
        /// <summary>Preferred board by USB serial number (stable across COM renumbering). Optional.</summary>
        public string PreferredUsbSerial;
        /// <summary>If set, skip USB detection and use this COM port (it must still pass identify).</summary>
        public string ManualPort;
    }

    /// <summary>Immutable view of the board, safe to read from any thread at any time.</summary>
    public sealed class BoardSnapshot
    {
        public ConnectionState Connection { get; internal set; }
        public string PortName { get; internal set; }
        public BoardIdentity Identity { get; internal set; }
        /// <summary>Last good status, null until the first poll succeeded.</summary>
        public BoardStatus Status { get; internal set; }
        public DateTime StatusTimeUtc { get; internal set; }
        public Housekeeping Housekeeping { get; internal set; }
        /// <summary>Last environment reading (:90#), null when no SHT31 has a valid reading.</summary>
        public EnvironmentReading Environment { get; internal set; }
        public FocuserSettings Focuser { get; internal set; }
        public RotatorSettings Rotator { get; internal set; }
        /// <summary>Human readable reason for the last problem (lost connection, device fault, watchdog), or null.</summary>
        public string Problem { get; internal set; }

        public bool IsConnected { get { return Connection == ConnectionState.Connected; } }
        public bool HasStatus { get { return IsConnected && Status != null; } }
        public bool IsFocuserMoving { get { return HasStatus && Status.FocuserMoving; } }
        public bool IsRotatorMoving { get { return HasStatus && Status.RotatorMoving; } }
        public bool IsBusy { get { return HasStatus && Status.Busy; } }
        /// <summary>Sticky device fault code (0 = none).</summary>
        public int DeviceError { get { return Status == null ? 0 : Status.Error; } }
        /// <summary>Rotator mechanical angle 0..360 degrees, NaN without status.</summary>
        public double RotatorMechanicalDegrees { get { return HasStatus ? Status.RotatorAngle / 100.0 : double.NaN; } }
    }

    public sealed class CommandResult
    {
        public bool Success { get; private set; }
        /// <summary>Firmware error code when the board rejected the command, else 0.</summary>
        public int ErrorCode { get; private set; }
        public string Message { get; private set; }

        public static readonly CommandResult Ok = new CommandResult { Success = true, Message = "ok" };
        public static CommandResult Fail(string message, int errorCode = 0)
        {
            return new CommandResult { Success = false, Message = message, ErrorCode = errorCode };
        }
        public override string ToString() { return Success ? "ok" : Message; }
    }

    /// <summary>Focuser set commands (PROTOCOL.md 4.1), value = command number.</summary>
    public enum FocuserSetting
    {
        MaxPosition = 55,
        Speed = 59,
        Acceleration = 61,
        Backlash = 63,
        Reverse = 65,
        StepSizeCentiMicrons = 67
    }

    /// <summary>Rotator set commands (PROTOCOL.md 4.2), value = command number.</summary>
    public enum RotatorSetting
    {
        Speed = 79,
        Acceleration = 81,
        Reverse = 83,
        StepsPerKiloDegree = 85,
        BootHome = 87,
        HomeToleranceCdeg = 89
    }

    public sealed class BoardController : IDisposable
    {
        private sealed class Work
        {
            public Func<CommandResult> Run;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public CommandResult Result;
            public int State; // 0 pending, 1 running, 2 done, 3 abandoned
        }

        private readonly BoardOptions opt;
        private readonly Func<string, ILink> linkFactory;
        private readonly IUsbSerialEnumerator enumerator;

        private readonly object queueLock = new object();
        private readonly LinkedList<Work> queue = new LinkedList<Work>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private Thread thread;
        private volatile bool stop;
        private volatile bool wantConnected;
        private volatile BoardSnapshot snapshot = new BoardSnapshot { Connection = ConnectionState.Disconnected };

        // worker-thread-only state
        private ILink link;
        private Transport transport;
        private int consecutiveFailures;
        private int backoffMs;
        private long nextConnectAttemptMs;
        private long nextPollMs;
        private long busySinceMs = -1;
        private bool watchdogTripped;
        private string problem;
        private string stickyProblem;      // e.g. watchdog halt; kept until the next accepted command
        private Housekeeping hk;
        private long hkNextMs;
        private EnvironmentReading env;
        private long envNextMs;
        private FocuserSettings focuser;
        private RotatorSettings rotator;
        private volatile string boundSerial; // the board this controller talks to; never another one after a reboot

        private static readonly long start = Stopwatch.GetTimestamp();
        private static long NowMs() { return (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency; }

        public event Action<BoardSnapshot> SnapshotChanged;
        public event Action<string> Log;

        public BoardController(BoardOptions options, Func<string, ILink> linkFactory = null, IUsbSerialEnumerator enumerator = null)
        {
            this.opt = options ?? new BoardOptions();
            this.linkFactory = linkFactory ?? (name => new SerialLink(name));
            this.enumerator = enumerator ?? new RegistryUsbEnumerator();
            backoffMs = opt.ReconnectMinMs;
        }

        public BoardSnapshot Snapshot { get { return snapshot; } }
        public BoardOptions Options { get { return opt; } }

        // ------------------------------------------------------------------ lifecycle

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(WorkerMain) { IsBackground = true, Name = "AstroUniBoard worker" };
            thread.Start();
        }

        /// <summary>Asks the worker to find and open the board. Returns immediately; watch <see cref="Snapshot"/>.</summary>
        public void Connect()
        {
            Start();
            if (!snapshot.IsConnected) boundSerial = string.IsNullOrEmpty(opt.PreferredUsbSerial) ? null : opt.PreferredUsbSerial;
            wantConnected = true;
            nextConnectAttemptMs = 0;
            backoffMs = opt.ReconnectMinMs;
            wake.Set();
        }

        public void Disconnect()
        {
            wantConnected = false;
            wake.Set();
        }

        /// <summary>Blocks up to <paramref name="timeoutMs"/> until connected. Returns IsConnected.</summary>
        public bool WaitConnected(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (snapshot.IsConnected) return true;
                Thread.Sleep(10);
            }
            return snapshot.IsConnected;
        }

        public void Dispose()
        {
            stop = true;
            wantConnected = false;
            wake.Set();
            Thread t = thread;
            if (t != null) t.Join(2000);
        }

        // ------------------------------------------------------------------ focuser

        /// <summary>Absolute move to 0..max. Out of range is rejected without sending (the ASCOM layer clamps).</summary>
        public CommandResult FocuserMoveTo(int steps, int waitMs = 3000)
        {
            return Submit(() => DoFocuserMove(steps), waitMs, false);
        }

        public CommandResult FocuserMoveBy(int steps, int waitMs = 3000)
        {
            return Submit(() =>
            {
                BoardStatus st = snapshot.Status;
                if (st == null) return CommandResult.Fail("board status unknown");
                long target = (long)st.FocuserPosition + steps;
                if (target < int.MinValue || target > int.MaxValue) return CommandResult.Fail("target out of range", (int)BoardError.BadParameter);
                return DoFocuserMove((int)target);
            }, waitMs, false);
        }

        public CommandResult FocuserHalt(int waitMs = 3000) { return Submit(() => SendAction(Commands.FocuserHalt), waitMs, true); }

        /// <summary>Sets the position counter without motion (:56); clears "position unverified".</summary>
        public CommandResult FocuserSync(int steps, int waitMs = 3000) { return Submit(() => SendAction(Commands.FocuserSync(steps)), waitMs, false); }

        public CommandResult SetFocuserSetting(FocuserSetting which, int value, int waitMs = 3000)
        {
            return Submit(() =>
            {
                CommandResult r = SendAction(Commands.Set((int)which, value));
                if (snapshot.Connection == ConnectionState.Connected) RefreshSettings(true, false);
                return r;
            }, waitMs, false);
        }

        // ------------------------------------------------------------------ rotator

        /// <summary>Move to a mechanical angle 0..35999 cdeg; the firmware picks the path inside the cable wrap.</summary>
        public CommandResult RotatorMoveTo(int cdeg, int waitMs = 3000)
        {
            int t = ((cdeg % 36000) + 36000) % 36000;
            return Submit(() => DoRotatorMove(t), waitMs, false);
        }

        public CommandResult RotatorMoveBy(int cdeg, int waitMs = 3000) { return Submit(() => SendAction(Commands.RotatorMoveBy(cdeg)), waitMs, false); }

        public CommandResult RotatorHalt(int waitMs = 3000) { return Submit(() => SendAction(Commands.RotatorHalt), waitMs, true); }

        /// <summary>Sets the signed mechanical angle without motion (:76); 0 = "this is home".</summary>
        public CommandResult RotatorSync(int signedCdeg, int waitMs = 3000) { return Submit(() => SendAction(Commands.RotatorSync(signedCdeg)), waitMs, false); }

        public CommandResult RotatorImuHome(int waitMs = 3000) { return Submit(() => SendAction(Commands.RotatorImuHome), waitMs, false); }

        /// <summary>IMU zero calibration (:95): moves the rotator +90 and +180 degrees and back (about 35 s).</summary>
        public CommandResult ImuCalibrate(int waitMs = 3000) { return Submit(() => SendAction(Commands.ImuCalibrate), waitMs, false); }

        public CommandResult SetRotatorSetting(RotatorSetting which, int value, int waitMs = 3000)
        {
            return Submit(() =>
            {
                CommandResult r = SendAction(Commands.Set((int)which, value));
                if (snapshot.Connection == ConnectionState.Connected) RefreshSettings(false, true);
                return r;
            }, waitMs, false);
        }

        public CommandResult SetRotatorWrap(int minCdeg, int maxCdeg, int waitMs = 3000)
        {
            return Submit(() =>
            {
                CommandResult r = SendAction(Commands.RotatorSetWrap(minCdeg, maxCdeg));
                if (snapshot.Connection == ConnectionState.Connected) RefreshSettings(false, true);
                return r;
            }, waitMs, false);
        }

        // ------------------------------------------------------------------ common

        /// <summary>Halts both axes; jumps the queue.</summary>
        public CommandResult HaltAll(int waitMs = 3000) { return Submit(() => SendAction(Commands.HaltAll), waitMs, true); }

        public CommandResult ClearError(int waitMs = 3000) { return Submit(() => SendAction(Commands.ClearError), waitMs, false); }

        /// <summary>Writes all running settings to the EEPROM (:23#).</summary>
        public CommandResult SaveSettings(int waitMs = 5000) { return Submit(() => SendAction(Commands.SaveSettings, opt.SaveTimeoutMs), waitMs, false); }

        /// <summary>Running settings back to defaults (:24#); SaveSettings keeps them.</summary>
        public CommandResult ResetSettings(int waitMs = 5000)
        {
            return Submit(() =>
            {
                CommandResult r = SendAction(Commands.ResetSettings);
                if (snapshot.Connection == ConnectionState.Connected) RefreshSettings(true, true);
                return r;
            }, waitMs, false);
        }

        public CommandResult ReadStallGuard(Axis axis, out StallGuardInfo info, int waitMs = 3000)
        {
            StallGuardInfo got = null;
            CommandResult r = Submit(() => QueryParsed(Commands.GetStallGuard(axis), v => StallGuardInfo.TryParse(v, out got)), waitMs, false);
            info = r.Success ? got : null;
            return r;
        }

        public CommandResult SetStallThreshold(Axis axis, int threshold, int waitMs = 3000)
        {
            if (threshold < 0 || threshold > 255) return CommandResult.Fail("threshold must be 0..255", (int)BoardError.BadParameter);
            return Submit(() => SendAction(Commands.SetStallThreshold(axis, threshold)), waitMs, false);
        }

        public CommandResult ReadDriverConfig(Axis axis, out DriverConfig cfg, int waitMs = 3000)
        {
            DriverConfig got = null;
            CommandResult r = Submit(() => QueryParsed(Commands.GetDriverConfig(axis), v => DriverConfig.TryParse(v, out got)), waitMs, false);
            cfg = r.Success ? got : null;
            return r;
        }

        /// <summary>Sets the TMC2209 config of one axis; a microstep change rescales step values, so settings are re-read.</summary>
        public CommandResult SetDriverConfig(Axis axis, DriverConfig cfg, int waitMs = 3000)
        {
            if (cfg == null) return CommandResult.Fail("no driver config", (int)BoardError.BadParameter);
            return Submit(() =>
            {
                CommandResult r = SendAction(Commands.SetDriverConfig(axis, cfg));
                if (snapshot.Connection == ConnectionState.Connected) RefreshSettings(true, true);
                if (r.Success && snapshot.Connection == ConnectionState.Connected) Poll();
                return r;
            }, waitMs, false);
        }

        public CommandResult ReadImu(out ImuReading imu, int waitMs = 3000)
        {
            ImuReading got = null;
            CommandResult r = Submit(() => QueryParsed(Commands.Imu, v => ImuReading.TryParse(v, out got)), waitMs, false);
            imu = r.Success ? got : null;
            return r;
        }

        public CommandResult ReadResetInfo(out ResetInfo info, int waitMs = 3000)
        {
            ResetInfo got = null;
            CommandResult r = Submit(() => QueryParsed(Commands.ResetInfo, v => ResetInfo.TryParse(v, out got)), waitMs, false);
            info = r.Success ? got : null;
            return r;
        }

        public CommandResult SetEnvironmentSource(int source, int waitMs = 3000)
        {
            if (source < 0 || source > 2) return CommandResult.Fail("source must be 0 auto, 1 on-board, 2 external", (int)BoardError.BadParameter);
            return Submit(() =>
            {
                CommandResult r = SendAction(Commands.SetEnvironmentSource(source));
                envNextMs = 0;
                return r;
            }, waitMs, false);
        }

        /// <summary>
        /// Sends any command (":NN...#") and returns the raw reply; for the debug console and the ASCOM
        /// CommandString. :22# and :31# reset the board: the controller then reconnects to the same serial.
        /// </summary>
        public CommandResult Raw(string command, out Reply reply, int waitMs = 3000)
        {
            Reply got = null;
            if (command == null || command.Length < 4 || command[0] != ':' || command[command.Length - 1] != '#' || command.Length - 2 > Commands.MaxCommandBody)
            {
                reply = null;
                return CommandResult.Fail("not a valid command (\":NN...#\", at most " + Commands.MaxCommandBody + " characters)", (int)BoardError.BadFormat);
            }
            CommandResult r = Submit(() =>
            {
                got = Send(command);
                if (snapshot.Connection != ConnectionState.Connected) return CommandResult.Fail("connection lost");
                if (got.IsTransportFailure) return CommandResult.Fail("no valid answer from the board");
                if (command == Commands.Reboot || command == Commands.EnterDfu) ExpectReset();
                else if (got.IsOk && ChangesSettings(command)) RefreshSettings(true, true);
                return CommandResult.Ok;
            }, waitMs, false);
            reply = got;
            return r;
        }

        /// <summary>Reboots the board (:22#); the controller reconnects to the same board.</summary>
        public CommandResult Reboot(int waitMs = 3000)
        {
            return Submit(() =>
            {
                CommandResult r = SendAction(Commands.Reboot);
                if (r.Success) ExpectReset();
                return r;
            }, waitMs, false);
        }

        /// <summary>Puts the board into the USB DFU bootloader (:31#) and disconnects.</summary>
        public CommandResult EnterDfu(int waitMs = 3000)
        {
            return Submit(() =>
            {
                CommandResult r = SendAction(Commands.EnterDfu);
                if (r.Success)
                {
                    wantConnected = false;
                    CloseLink();
                    problem = "board is in the DFU bootloader";
                    Publish(ConnectionState.Disconnected, null, null, problem);
                }
                return r;
            }, waitMs, false);
        }

        private CommandResult Submit(Func<CommandResult> run, int waitMs, bool urgent)
        {
            if (!snapshot.IsConnected) return CommandResult.Fail("board is not connected");
            var w = new Work { Run = run };
            lock (queueLock)
            {
                if (urgent) queue.AddFirst(w); else queue.AddLast(w);
            }
            wake.Set();

            if (w.Done.Wait(waitMs)) return w.Result;

            // Not finished in time: if it has not started, cancel it so it can never run late.
            if (Interlocked.CompareExchange(ref w.State, 3, 0) == 0)
                return CommandResult.Fail("board did not respond in time");

            // Running right now; the worker is bounded by the command timeouts, wait for it.
            if (w.Done.Wait(opt.CommandTimeoutMs * 6 + 500)) return w.Result;
            return CommandResult.Fail("board did not respond in time");
        }

        // ------------------------------------------------------------------ worker

        private void WorkerMain()
        {
            while (!stop)
            {
                try { Step(); }
                catch (Exception ex)
                {
                    LogMsg("internal error: " + ex);
                    try { LoseConnection("internal error: " + ex.Message); } catch { }
                    Thread.Sleep(200);
                }
            }
            CloseLink();
            FailAllQueued("driver stopped");
            Publish(ConnectionState.Disconnected, null, null, problem);
        }

        private void Step()
        {
            BoardSnapshot s = snapshot;

            if (!wantConnected)
            {
                if (s.Connection != ConnectionState.Disconnected)
                {
                    CloseLink();
                    FailAllQueued("disconnected");
                    problem = null;
                    Publish(ConnectionState.Disconnected, null, null, null);
                }
                wake.WaitOne(200);
                return;
            }

            if (s.Connection == ConnectionState.Connected)
            {
                ServiceConnected();
                return;
            }

            if (s.Connection == ConnectionState.Disconnected)
                Publish(ConnectionState.Connecting, null, null, null);

            long now = NowMs();
            if (now < nextConnectAttemptMs)
            {
                wake.WaitOne((int)Math.Min(nextConnectAttemptMs - now, 100));
                return;
            }
            TryConnect();
        }

        private void TryConnect()
        {
            var candidates = new List<KeyValuePair<string, string>>(); // port, usb serial
            string want = boundSerial;
            if (!string.IsNullOrEmpty(opt.ManualPort))
            {
                candidates.Add(new KeyValuePair<string, string>(opt.ManualPort, null));
            }
            else
            {
                foreach (UsbSerialDevice d in DeviceFinder.FindCandidates(enumerator, want))
                    candidates.Add(new KeyValuePair<string, string>(d.PortName, d.UsbSerial));
            }

            string lastFailure = candidates.Count == 0 ? "no AstroUniBoard found on USB" : null;
            foreach (var c in candidates)
            {
                if (stop || !wantConnected) return;
                ILink l = null;
                try
                {
                    l = linkFactory(c.Key);
                    string failure;
                    BoardIdentity id = DeviceFinder.Identify(l, opt.CommandTimeoutMs, out failure);
                    if (id == null)
                    {
                        lastFailure = c.Key + ": " + failure;
                        LogMsg("rejected " + lastFailure);
                        SafeClose(l);
                        continue;
                    }
                    if (!string.IsNullOrEmpty(want) && !string.Equals(id.Serial, want, StringComparison.OrdinalIgnoreCase))
                    {
                        lastFailure = c.Key + ": another board (serial " + id.Serial + ", want " + want + ")";
                        LogMsg("skipped " + lastFailure);
                        SafeClose(l);
                        continue;
                    }

                    var t = new Transport(l);
                    Reply r = t.Execute(Commands.Status, opt.CommandTimeoutMs);
                    BoardStatus st;
                    if (r.Kind != ReplyKind.Value || !BoardStatus.TryParse(r.Value, out st))
                    {
                        lastFailure = c.Key + ": no valid status after identify (" + r + ")";
                        SafeClose(l);
                        continue;
                    }

                    link = l;
                    transport = t;
                    boundSerial = id.Serial;
                    consecutiveFailures = 0;
                    backoffMs = opt.ReconnectMinMs;
                    busySinceMs = -1;
                    watchdogTripped = false;
                    problem = null;
                    stickyProblem = null;
                    nextPollMs = NowMs() + (st.Busy ? opt.PollBusyMs : opt.PollIdleMs);
                    hk = null; env = null; focuser = null; rotator = null;
                    // publish identity first so Send() can re-identify while the rest is read
                    Publish(ConnectionState.Connected, id, st, ProblemFor(st));
                    RefreshSettings(true, true);
                    if (transport == null) return;
                    ReadHousekeeping();
                    if (transport == null) return;
                    ReadEnvironment();
                    if (transport == null) return;
                    LogMsg("connected " + l.Name + " " + id + " " + st);
                    Publish(ConnectionState.Connected, id, st, ProblemFor(st));
                    return;
                }
                catch (Exception ex)
                {
                    lastFailure = c.Key + ": " + ex.Message;
                    if (l != null) SafeClose(l);
                }
            }

            problem = lastFailure;
            ScheduleReconnect();
            Publish(snapshot.Connection == ConnectionState.Reconnecting ? ConnectionState.Reconnecting : ConnectionState.Connecting,
                    snapshot.Connection == ConnectionState.Reconnecting ? snapshot.Identity : null, null, problem);
        }

        private void ScheduleReconnect()
        {
            nextConnectAttemptMs = NowMs() + backoffMs;
            backoffMs = Math.Min(backoffMs * 2, opt.ReconnectMaxMs);
        }

        private void ServiceConnected()
        {
            Work w = null;
            lock (queueLock)
            {
                while (queue.First != null)
                {
                    Work cand = queue.First.Value;
                    queue.RemoveFirst();
                    if (Interlocked.CompareExchange(ref cand.State, 1, 0) == 0) { w = cand; break; }
                }
            }
            if (w != null)
            {
                CommandResult res;
                try { res = w.Run(); }
                catch (Exception ex) { res = CommandResult.Fail("internal error: " + ex.Message); }
                // Refresh the status BEFORE the caller is released: a client that reads IsMoving right
                // after a successful move command must already see the move.
                if (res.Success) stickyProblem = null;
                if (res.Success && snapshot.Connection == ConnectionState.Connected) Poll();
                w.Result = res;
                Interlocked.Exchange(ref w.State, 2);
                w.Done.Set();
                nextPollMs = 0;
                return;
            }

            long now = NowMs();
            if (now >= nextPollMs)
            {
                Poll();
                if (snapshot.Connection != ConnectionState.Connected) return;
                BoardStatus st = snapshot.Status;
                nextPollMs = NowMs() + (st != null && st.Busy ? opt.PollBusyMs : opt.PollIdleMs);
                Watchdog(st);
                if (snapshot.Connection == ConnectionState.Connected) MaybeRefreshHousekeeping(st);
                if (snapshot.Connection == ConnectionState.Connected) MaybeRefreshEnvironment();
            }
            else
            {
                wake.WaitOne((int)Math.Min(nextPollMs - now, 50));
            }
        }

        private void Poll()
        {
            Reply r = Send(Commands.Status);
            if (snapshot.Connection != ConnectionState.Connected) return;

            BoardStatus st;
            if (r.Kind == ReplyKind.Value && BoardStatus.TryParse(r.Value, out st))
            {
                Publish(ConnectionState.Connected, snapshot.Identity, st, ProblemFor(st) ?? stickyProblem);
                return;
            }
            if (r.Kind == ReplyKind.Value)
            {
                RegisterFailure("unexpected reply to status: " + r.Value);
                return;
            }
            if (r.Kind == ReplyKind.Error)
            {
                problem = "board rejected the status request: " + BoardErrors.Describe(r.ErrorCode);
                Publish(ConnectionState.Connected, snapshot.Identity, snapshot.Status, problem);
            }
        }

        private void Watchdog(BoardStatus st)
        {
            if (st == null || !st.Busy || st.State == BoardState.Init) { busySinceMs = -1; watchdogTripped = false; return; }
            long now = NowMs();
            if (busySinceMs < 0) busySinceMs = now;
            int limit = st.State == BoardState.ImuBusy ? opt.ImuTimeoutMs : opt.MoveTimeoutMs;
            if (now - busySinceMs > limit && !watchdogTripped)
            {
                watchdogTripped = true;
                LogMsg("watchdog: board busy (" + st.State + ") for more than " + limit + " ms, halting");
                Send(Commands.HaltAll);
                problem = "move did not finish in time and was halted by the driver";
                stickyProblem = problem;
                nextPollMs = 0;
                Publish(snapshot.Connection, snapshot.Identity, snapshot.Status, problem);
            }
        }

        // ------------------------------------------------------------------ cached reads

        private void ReadHousekeeping()
        {
            hkNextMs = NowMs() + opt.HousekeepingMs;
            Reply r = Send(Commands.Housekeeping);
            if (transport == null) return;
            Housekeeping h;
            if (r.Kind != ReplyKind.Value || !Housekeeping.TryParse(r.Value, out h)) return;

            HousekeepingWarnings before = hk == null ? HousekeepingWarnings.None : hk.Warnings;
            if (h.Warnings != before)
            {
                HousekeepingWarnings added = h.Warnings & ~before;
                if (added != HousekeepingWarnings.None) LogMsg("board warning: " + Housekeeping.Describe(added) + " (" + h + ")");
                else LogMsg("board warnings cleared, now: " + Housekeeping.Describe(h.Warnings));
            }
            hk = h;
        }

        private void MaybeRefreshHousekeeping(BoardStatus st)
        {
            if (st == null) return;
            bool flagChanged = hk != null && st.Has(StatusFlags.HousekeepingWarning) != (hk.Warnings != HousekeepingWarnings.None);
            if (!flagChanged && NowMs() < hkNextMs) return;
            ReadHousekeeping();
            if (snapshot.Connection == ConnectionState.Connected) PublishSame();
        }

        private void ReadEnvironment()
        {
            envNextMs = NowMs() + opt.EnvironmentMs;
            BoardIdentity id = snapshot.Identity;
            if (id == null || !id.Has(BoardCaps.Environment)) { env = null; return; }
            Reply r = Send(Commands.Environment);
            if (transport == null) return;
            EnvironmentReading e;
            if (r.Kind == ReplyKind.Value && EnvironmentReading.TryParse(r.Value, out e)) { env = e; return; }
            if (r.Kind == ReplyKind.Error && r.ErrorCode == (int)BoardError.Sensor)
            {
                if (env != null) LogMsg("environment sensor has no valid reading");
                env = null;
            }
        }

        private void MaybeRefreshEnvironment()
        {
            if (NowMs() < envNextMs) return;
            ReadEnvironment();
            if (snapshot.Connection == ConnectionState.Connected) PublishSame();
        }

        /// <summary>Reads the focuser and/or rotator settings into the cache (keeps the old values on a failed read).</summary>
        private void RefreshSettings(bool foc, bool rot)
        {
            BoardIdentity id = snapshot.Identity;
            if (id == null) return;
            if (foc && id.Has(BoardCaps.Focuser))
            {
                int max, speed, accel, backlash, rev, step;
                if (GetInt(54, out max) && GetInt(58, out speed) && GetInt(60, out accel) && GetInt(62, out backlash) &&
                    GetInt(64, out rev) && GetInt(66, out step))
                    focuser = new FocuserSettings { MaxPosition = max, Speed = speed, Acceleration = accel, Backlash = backlash, Reverse = rev != 0, StepSizeCentiMicrons = step };
                if (transport == null) return;
            }
            if (rot && id.Has(BoardCaps.Rotator))
            {
                int speed, accel, rev, spk, boot, tol;
                int[] wrap = null;
                Reply w = Send(Commands.Get(74));
                if (w.Kind == ReplyKind.Value)
                {
                    string[] p = w.Value.Split(',');
                    int a, b;
                    if (p.Length == 2 && int.TryParse(p[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out a) &&
                        int.TryParse(p[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out b)) wrap = new[] { a, b };
                }
                if (wrap != null && GetInt(78, out speed) && GetInt(80, out accel) && GetInt(82, out rev) && GetInt(84, out spk) &&
                    GetInt(86, out boot) && GetInt(88, out tol))
                    rotator = new RotatorSettings
                    {
                        WrapMinCdeg = wrap[0], WrapMaxCdeg = wrap[1], SpeedCdegPerS = speed, AccelerationCdegPerS2 = accel,
                        Reverse = rev != 0, StepsPerKiloDegree = spk, BootHome = boot != 0, HomeToleranceCdeg = tol
                    };
            }
            if (snapshot.Connection == ConnectionState.Connected) PublishSame();
        }

        private bool GetInt(int number, out int value)
        {
            value = 0;
            if (transport == null) return false;
            Reply r = Send(Commands.Get(number));
            return r.Kind == ReplyKind.Value && int.TryParse(r.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        // ------------------------------------------------------------------ command implementations (worker thread)

        private CommandResult DoFocuserMove(int target)
        {
            BoardStatus st = snapshot.Status;
            FocuserSettings f = focuser;
            if (st == null) return CommandResult.Fail("board status unknown");
            if (target < 0 || (f != null && target > f.MaxPosition))
                return CommandResult.Fail("focuser target " + target + " is outside 0.." + (f == null ? "max" : f.MaxPosition.ToString(CultureInfo.InvariantCulture)), (int)BoardError.BadParameter);

            Reply r = Send(Commands.FocuserMoveTo(target));
            if (snapshot.Connection != ConnectionState.Connected) return CommandResult.Fail("connection lost");
            if (r.IsOk) return CommandResult.Ok;
            if (r.Kind == ReplyKind.Error) return Rejected(r);

            // reply lost or garbled: ask the board whether the move started
            Reply s = Send(Commands.Status);
            BoardStatus st2;
            if (s.Kind == ReplyKind.Value && BoardStatus.TryParse(s.Value, out st2) &&
                ((st2.State == BoardState.FocuserMoving && st2.FocuserTarget == target) || (!st2.Busy && st2.FocuserPosition == target)))
                return CommandResult.Ok;
            return CommandResult.Fail("no valid answer from the board");
        }

        private CommandResult DoRotatorMove(int target)
        {
            Reply r = Send(Commands.RotatorMoveTo(target));
            if (snapshot.Connection != ConnectionState.Connected) return CommandResult.Fail("connection lost");
            if (r.IsOk) return CommandResult.Ok;
            if (r.Kind == ReplyKind.Error) return Rejected(r);

            Reply s = Send(Commands.Status);
            BoardStatus st2;
            if (s.Kind == ReplyKind.Value && BoardStatus.TryParse(s.Value, out st2) &&
                ((st2.State == BoardState.RotatorMoving && st2.RotatorTarget == target) || (!st2.Busy && st2.RotatorAngle == target)))
                return CommandResult.Ok;
            return CommandResult.Fail("no valid answer from the board");
        }

        private CommandResult SendAction(string command, int timeoutMs = 0)
        {
            Reply r = Send(command, timeoutMs);
            if (snapshot.Connection != ConnectionState.Connected) return CommandResult.Fail("connection lost");
            if (r.IsOk) return CommandResult.Ok;
            if (r.Kind == ReplyKind.Error) return Rejected(r);
            return CommandResult.Fail("no valid answer from the board");
        }

        private CommandResult QueryParsed(string command, Func<string, bool> parse)
        {
            Reply rep = Send(command);
            if (snapshot.Connection != ConnectionState.Connected) return CommandResult.Fail("connection lost");
            if (rep.Kind == ReplyKind.Error) return Rejected(rep);
            if (rep.Kind != ReplyKind.Value || !parse(rep.Value)) return CommandResult.Fail("no valid answer from the board");
            return CommandResult.Ok;
        }

        /// <summary>Set commands whose values the controller caches (see RefreshSettings).</summary>
        private static readonly HashSet<string> settingCommands = new HashSet<string>
        { "24", "48", "55", "59", "61", "63", "65", "67", "75", "79", "81", "83", "85", "87", "89" };

        private static bool ChangesSettings(string command)
        {
            return command.Length >= 4 && settingCommands.Contains(command.Substring(1, 2));
        }

        private static CommandResult Rejected(Reply r)
        {
            return CommandResult.Fail("board rejected the command: " + BoardErrors.Describe(r.ErrorCode), r.ErrorCode);
        }

        /// <summary>After :22#: the board resets and re-enumerates; reconnect to the same serial.</summary>
        private void ExpectReset()
        {
            CloseLink();
            FailAllQueued("board is rebooting");
            problem = "board is rebooting";
            nextConnectAttemptMs = NowMs() + 1500;
            backoffMs = opt.ReconnectMinMs;
            Publish(ConnectionState.Reconnecting, snapshot.Identity, null, problem);
        }

        // ------------------------------------------------------------------ transport with failure accounting

        /// <summary>
        /// Sends one command. ERR,16 (identify gate re-locked: someone reopened the port) is answered by
        /// identifying again — same serial only — and repeating the command once. A broken link or too
        /// many bad replies in a row ends the connection.
        /// </summary>
        private Reply Send(string command, int timeoutMs = 0)
        {
            Reply r = SendOnce(command, timeoutMs);
            if (r.Kind == ReplyKind.Error && r.ErrorCode == (int)BoardError.NotIdentified && command != Commands.Identify && transport != null)
            {
                LogMsg("board asks to identify again (port was reopened or reconfigured)");
                Reply id = SendOnce(Commands.Identify, 0);
                if (transport == null) return Reply.TimeoutReply;
                BoardIdentity again;
                BoardIdentity known = snapshot.Identity;
                if (id.Kind != ReplyKind.Value || !BoardIdentity.TryParse(id.Value, out again) ||
                    (known != null && !string.Equals(again.Serial, known.Serial, StringComparison.OrdinalIgnoreCase)))
                {
                    LoseConnection("board did not identify as the same device again (" + id + ")");
                    return Reply.TimeoutReply;
                }
                r = SendOnce(command, timeoutMs);
            }
            return r;
        }

        private Reply SendOnce(string command, int timeoutMs)
        {
            if (transport == null) return Reply.TimeoutReply;
            Reply r;
            try
            {
                r = transport.Execute(command, timeoutMs > 0 ? timeoutMs : opt.CommandTimeoutMs);
            }
            catch (LinkException ex)
            {
                LoseConnection("connection to the board lost: " + ex.Message);
                return Reply.TimeoutReply;
            }

            if (r.IsTransportFailure) RegisterFailure(r.Kind == ReplyKind.Timeout ? "board did not answer " + command : "board sent garbage to " + command);
            else consecutiveFailures = 0;
            return r;
        }

        private void RegisterFailure(string what)
        {
            consecutiveFailures++;
            LogMsg(what + " (" + consecutiveFailures + "/" + opt.MaxConsecutiveFailures + ")");
            if (consecutiveFailures >= opt.MaxConsecutiveFailures)
                LoseConnection(what + " " + consecutiveFailures + " times in a row");
        }

        private void LoseConnection(string why)
        {
            LogMsg("connection lost: " + why);
            CloseLink();
            FailAllQueued("connection lost");
            problem = why;
            busySinceMs = -1;
            if (wantConnected && opt.AutoReconnect)
            {
                ScheduleReconnect();
                Publish(ConnectionState.Reconnecting, snapshot.Identity, null, why);
            }
            else
            {
                wantConnected = false;
                Publish(ConnectionState.Disconnected, null, null, why);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static string ProblemFor(BoardStatus st)
        {
            if (st == null) return null;
            if (st.Error != 0) return "board reports: " + BoardErrors.Describe(st.Error) + " (code " + st.Error + ")";
            if (st.Has(StatusFlags.EepromFault)) return "board reports an EEPROM fault (defaults in use)";
            if (st.Has(StatusFlags.SafeMode)) return "board started in safe mode after a watchdog reset (sensors off)";
            return null;
        }

        private void FailAllQueued(string why)
        {
            lock (queueLock)
            {
                foreach (Work w in queue)
                {
                    if (Interlocked.CompareExchange(ref w.State, 2, 0) == 0)
                    {
                        w.Result = CommandResult.Fail(why);
                        w.Done.Set();
                    }
                }
                queue.Clear();
            }
        }

        private void CloseLink()
        {
            ILink l = link;
            link = null;
            transport = null;
            if (l != null) SafeClose(l);
        }

        private static void SafeClose(ILink l)
        {
            try { l.Close(); } catch { }
            try { l.Dispose(); } catch { }
        }

        private void PublishSame()
        {
            Publish(snapshot.Connection, snapshot.Identity, snapshot.Status, snapshot.Problem);
        }

        private void Publish(ConnectionState conn, BoardIdentity id, BoardStatus st, string prob)
        {
            bool up = conn == ConnectionState.Connected;
            var s = new BoardSnapshot
            {
                Connection = conn,
                PortName = up && link != null ? link.Name : (conn == ConnectionState.Reconnecting || conn == ConnectionState.Connecting ? snapshot.PortName : null),
                Identity = id,
                Status = st,
                StatusTimeUtc = st == null ? DateTime.MinValue : DateTime.UtcNow,
                Problem = prob,
                Housekeeping = up ? hk : null,
                Environment = up ? env : null,
                Focuser = up ? focuser : null,
                Rotator = up ? rotator : null
            };
            snapshot = s;
            Action<BoardSnapshot> h = SnapshotChanged;
            if (h != null)
            {
                try { h(s); } catch { }
            }
        }

        private void LogMsg(string m)
        {
            Action<string> h = Log;
            if (h != null)
            {
                try { h(m); } catch { }
            }
        }
    }
}
