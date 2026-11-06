// ASCOM Focuser (IFocuserV4) on the AstroUniBoard focuser axis. Absolute, 0..max steps.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using ASCOM;
using ASCOM.DeviceInterface;
using AstroUniBoard.Core;

namespace AstroUniBoard.Ascom
{
    [ComVisible(true)]
    [Guid(Server.FocuserClsid)]
    [ProgId(Server.FocuserProgId)]
    [ServedClassName("AstroUniBoard Focuser")]
    [ClassInterface(ClassInterfaceType.None)]
    public class Focuser : DeviceBase, IFocuserV4
    {
        /// <summary>The firmware refuses a move while the other axis is busy (Q12): wait this long for it.</summary>
        private const int WaitOtherAxisMs = 30000;
        private const int WaitHaltMs = 10000;

        protected override string DeviceKind { get { return "Focuser"; } }
        protected override BoardCaps RequiredCaps { get { return BoardCaps.Focuser; } }

        public string Description { get { return "AstroUniBoard focuser (USB, shares the board with the rotator)"; } }
        public short InterfaceVersion { get { return 4; } }

        private FocuserSettings RequireSettings(BoardSnapshot s)
        {
            if (s.Focuser == null) throw new DriverException("The focuser settings have not been read from the board yet. Try again in a moment.");
            return s.Focuser;
        }

        public bool Absolute { get { return true; } }

        public bool IsMoving { get { return RequireConnected().IsFocuserMoving; } }

        public bool Link
        {
            get { return Connected; }
            set { Connected = value; }
        }

        public int MaxIncrement { get { return RequireSettings(RequireConnected()).MaxPosition; } }

        public int MaxStep { get { return RequireSettings(RequireConnected()).MaxPosition; } }

        public int Position { get { return RequireConnected().Status.FocuserPosition; } }

        public double StepSize
        {
            get
            {
                FocuserSettings f = RequireSettings(RequireConnected());
                if (f.StepSizeCentiMicrons <= 0) throw new PropertyNotImplementedException("StepSize", false);
                return f.StepSizeCentiMicrons / 100.0;
            }
        }

        public bool TempCompAvailable
        {
            get
            {
                BoardSnapshot s = RequireConnected();
                return s.Identity != null && s.Identity.Has(BoardCaps.Environment);
            }
        }

        public bool TempComp
        {
            get { RequireConnected(); return Server.TempComp.Enabled; }
            set
            {
                if (!TempCompAvailable) throw new PropertyNotImplementedException("TempComp", true);
                Server.TempComp.Enable(value);
            }
        }

        public double Temperature
        {
            get
            {
                BoardSnapshot s = RequireConnected();
                if (s.Identity == null || !s.Identity.Has(BoardCaps.Environment)) throw new PropertyNotImplementedException("Temperature", false);
                if (s.Environment == null) throw new DriverException("The temperature sensor (SHT31) has no valid reading");
                return s.Environment.Temperature;
            }
        }

        public void Halt()
        {
            RequireConnected();
            CommandResult r = Server.Controller.FocuserHalt(4000);
            if (!r.Success) throw Failed("Cannot halt the focuser", r);
        }

        /// <summary>
        /// Absolute move, clamped to 0..max as IFocuserV4 allows. A move while the focuser is still
        /// moving halts it first; while the rotator moves, waits for it (the firmware moves one axis at a time).
        /// Moves are allowed while TempComp is on (IFocuserV3+): compensation continues from the new position.
        /// </summary>
        public void Move(int value)
        {
            BoardSnapshot s = RequireConnected();
            FocuserSettings f = RequireSettings(s);
            int target = Math.Max(0, Math.Min(f.MaxPosition, value));

            if (s.IsFocuserMoving)
            {
                Server.Controller.FocuserHalt(4000);
                if (!WaitIdle(WaitHaltMs)) throw new DriverException("The focuser did not stop for the new move");
            }
            else if (s.IsBusy && !WaitIdle(WaitOtherAxisMs))
                throw new DriverException("The board is busy (rotator moving or IMU homing) and did not finish within " + WaitOtherAxisMs / 1000 + " s");

            CommandResult r = Server.Controller.FocuserMoveTo(target, 4000);
            if (r.Success) return;
            if (r.ErrorCode == (int)BoardError.Busy && WaitIdle(WaitOtherAxisMs))
            {
                r = Server.Controller.FocuserMoveTo(target, 4000);
                if (r.Success) return;
            }
            throw Failed("Cannot move the focuser to " + target.ToString(CultureInfo.InvariantCulture), r);
        }

        private static bool WaitIdle(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                BoardSnapshot s = Server.Controller.Snapshot;
                if (!s.IsConnected) return false;
                if (s.HasStatus && !s.IsBusy) return true;
                Thread.Sleep(50);
            }
            return false;
        }

        public IStateValueCollection DeviceState
        {
            get
            {
                BoardSnapshot s = RequireConnected();
                var c = new StateValueCollection();
                c.Add("IsMoving", s.IsFocuserMoving);
                c.Add("Position", s.Status.FocuserPosition);
                if (s.Environment != null) c.Add("Temperature", s.Environment.Temperature);
                c.AddUtcDateTime();
                return c;
            }
        }
    }
}
