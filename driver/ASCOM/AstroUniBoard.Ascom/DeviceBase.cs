// What the Focuser, Rotator and ObservingConditions COM classes share: the reference-counted
// connection to the one BoardController, the Action contract, raw commands and identity.
// Every call returns quickly from the controller's cached snapshot or a bounded command.
using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ASCOM;
using AstroUniBoard.Core;

namespace AstroUniBoard.Ascom
{
    internal static class Identity
    {
        public static string DriverVersion
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor;
            }
        }

        public static string DriverInfo(string what)
        {
            return "AstroUniBoard ASCOM " + what + " driver " + DriverVersion + " (LocalServer, protocol 2). Open source, https://github.com/xlazam01/AstroUniBoard";
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public abstract class DeviceBase : IDisposable
    {
        private const int ConnectTimeoutMs = 10000;

        private readonly object gate = new object();
        private bool connected;       // this instance holds a connection reference
        private bool connecting;
        private Exception connectError;
        private bool disposed;

        protected DeviceBase()
        {
            Server.CountObject();
        }

        ~DeviceBase()
        {
            Release();
        }

        public void Dispose()
        {
            Release();
            GC.SuppressFinalize(this);
        }

        private void Release()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (connected) { connected = false; Server.ReleaseConnection(); }
            }
            Server.UncountObject();
        }

        /// <summary>"Focuser", "Rotator", "ObservingConditions".</summary>
        protected abstract string DeviceKind { get; }

        /// <summary>The board capability this device needs (checked on connect).</summary>
        protected abstract BoardCaps RequiredCaps { get; }

        protected bool InstanceConnected { get { lock (gate) return connected; } }

        // ---------------------------------------------------------------- helpers

        protected BoardSnapshot RequireConnected()
        {
            if (!InstanceConnected) throw new NotConnectedException("The AstroUniBoard " + DeviceKind + " is not connected");
            BoardSnapshot s = Server.Controller.Snapshot;
            if (!s.HasStatus)
                throw new NotConnectedException("The connection to the AstroUniBoard was lost" + (s.Problem != null ? ": " + s.Problem : "") + ". The driver keeps trying to reconnect.");
            return s;
        }

        protected static DriverException Failed(string what, CommandResult r)
        {
            return new DriverException(what + ": " + r.Message);
        }

        private string MissingCapability(BoardSnapshot s)
        {
            if (s.Identity == null || RequiredCaps == BoardCaps.None || s.Identity.Has(RequiredCaps)) return null;
            return "this AstroUniBoard firmware (" + s.Identity.Firmware + ", caps " + s.Identity.Caps + ") has no " + DeviceKind.ToLowerInvariant();
        }

        // ---------------------------------------------------------------- connection

        public bool Connected
        {
            get
            {
                lock (gate) { if (!connected) return false; }
                return Server.Controller.Snapshot.IsConnected;
            }
            set
            {
                lock (gate)
                {
                    if (value == connected) return;
                    if (!value) { connected = false; Server.ReleaseConnection(); return; }
                    connected = true;
                    Server.AcquireConnection();
                }

                string why = WaitForBoard();
                if (why != null)
                {
                    lock (gate) { if (connected) { connected = false; Server.ReleaseConnection(); } }
                    throw new DriverException("Cannot connect to the AstroUniBoard: " + why);
                }
            }
        }

        /// <summary>Waits for the shared connection; null on success, else the reason.</summary>
        private string WaitForBoard()
        {
            BoardController c = Server.Controller;
            if (!c.WaitConnected(ConnectTimeoutMs)) return c.Snapshot.Problem ?? "no AstroUniBoard found";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // The controller reports Connected before it has read the settings, housekeeping and
            // environment (a few more commands): wait for them so MaxStep, StepSize, Reverse, ... work at once.
            while (sw.ElapsedMilliseconds < 3000 && !SettingsRead(c.Snapshot)) System.Threading.Thread.Sleep(20);
            return MissingCapability(c.Snapshot);
        }

        private static bool SettingsRead(BoardSnapshot s)
        {
            if (!s.IsConnected || s.Identity == null) return true;   // lost again: the caller reports it
            if (s.Identity.Has(BoardCaps.Focuser) && s.Focuser == null) return false;
            if (s.Identity.Has(BoardCaps.Rotator) && s.Rotator == null) return false;
            return s.Housekeeping != null;
        }

        public void Connect()
        {
            lock (gate)
            {
                if (connected || connecting) return;
                connecting = true;
                connectError = null;
                connected = true;
                Server.AcquireConnection();
            }

            Task.Run(() =>
            {
                string why = WaitForBoard();
                lock (gate)
                {
                    if (why != null)
                    {
                        if (connected) { connected = false; Server.ReleaseConnection(); }
                        connectError = new DriverException("Cannot connect to the AstroUniBoard: " + why);
                    }
                    connecting = false;
                }
            });
        }

        public void Disconnect()
        {
            lock (gate)
            {
                if (connected) { connected = false; Server.ReleaseConnection(); }
            }
        }

        public bool Connecting
        {
            get
            {
                lock (gate)
                {
                    if (connectError != null)
                    {
                        Exception e = connectError;
                        connectError = null;
                        throw e;
                    }
                    return connecting;
                }
            }
        }

        // ---------------------------------------------------------------- actions and raw commands

        public ArrayList SupportedActions
        {
            get { return new ArrayList(BoardActions.Names); }
        }

        public string Action(string actionName, string actionParameters)
        {
            return BoardActions.Run(actionName, actionParameters, InstanceConnected);
        }

        /// <summary>CommandString(":NN#", raw) is the same as Action AstroUniBoard:Raw, but throws on failure.</summary>
        public string CommandString(string command, bool raw)
        {
            if (!InstanceConnected) throw new NotConnectedException("The AstroUniBoard " + DeviceKind + " is not connected");
            string result = BoardActions.Raw(command);
            if (result.StartsWith(BoardActions.ErrorPrefix, StringComparison.Ordinal)) throw new DriverException(result.Substring(BoardActions.ErrorPrefix.Length));
            return result;
        }

        public void CommandBlind(string command, bool raw) { throw new MethodNotImplementedException("CommandBlind (use CommandString or Action AstroUniBoard:Raw)"); }
        public bool CommandBool(string command, bool raw) { throw new MethodNotImplementedException("CommandBool (use CommandString or Action AstroUniBoard:Raw)"); }

        // ---------------------------------------------------------------- identity

        public void SetupDialog() { Server.ShowSetupDialog(); }

        public string DriverVersion { get { return Identity.DriverVersion; } }

        public string DriverInfo { get { return Identity.DriverInfo(DeviceKind); } }

        public string Name
        {
            get
            {
                BoardSnapshot s = Server.Controller.Snapshot;
                return "AstroUniBoard " + DeviceKind + (s.IsConnected && s.Identity != null ? " (" + s.Identity.Firmware + ")" : "");
            }
        }
    }
}
