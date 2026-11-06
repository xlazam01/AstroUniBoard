// ASCOM Rotator (IRotatorV4) on the AstroUniBoard rotator axis.
// MechanicalPosition = the board's mechanical angle (0..360). Position = sky angle =
// mechanical + sync offset; the offset lives only in the ASCOM profile (Q18). The firmware picks
// the path inside the cable wrap for every absolute move.
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
    [Guid(Server.RotatorClsid)]
    [ProgId(Server.RotatorProgId)]
    [ServedClassName("AstroUniBoard Rotator")]
    [ClassInterface(ClassInterfaceType.None)]
    public class Rotator : DeviceBase, IRotatorV4
    {
        private const int WaitOtherAxisMs = 30000;
        private const int WaitHaltMs = 10000;

        protected override string DeviceKind { get { return "Rotator"; } }
        protected override BoardCaps RequiredCaps { get { return BoardCaps.Rotator; } }

        public string Description { get { return "AstroUniBoard camera rotator (USB, shares the board with the focuser)"; } }
        public short InterfaceVersion { get { return 4; } }

        private static double Mechanical(BoardSnapshot s) { return s.Status.RotatorAngle / 100.0; }

        private static double Sky(double mechanical) { return RotatorState.Normalize(mechanical + RotatorState.SkyOffset); }

        public bool CanReverse { get { return true; } }

        public bool IsMoving { get { return RequireConnected().IsRotatorMoving; } }

        public float MechanicalPosition { get { return (float)Mechanical(RequireConnected()); } }

        public float Position { get { return (float)Sky(Mechanical(RequireConnected())); } }

        public float TargetPosition
        {
            get
            {
                BoardSnapshot s = RequireConnected();
                double? t = RotatorState.TargetSky;
                return (float)(t ?? Sky(Mechanical(s)));
            }
        }

        public float StepSize
        {
            get
            {
                RotatorSettings r = RequireConnected().Rotator;
                if (r == null || r.StepsPerKiloDegree <= 0) throw new PropertyNotImplementedException("StepSize", false);
                return (float)r.StepSizeDegrees;
            }
        }

        public bool Reverse
        {
            get
            {
                RotatorSettings r = RequireConnected().Rotator;
                if (r == null) throw new DriverException("The rotator settings have not been read from the board yet. Try again in a moment.");
                return r.Reverse;
            }
            set
            {
                BoardSnapshot s = RequireConnected();
                if (s.Rotator != null && s.Rotator.Reverse == value) return;
                if (s.IsBusy && !WaitIdle(WaitOtherAxisMs)) throw new DriverException("The board is busy and did not finish within " + WaitOtherAxisMs / 1000 + " s");
                CommandResult r = Server.Controller.SetRotatorSetting(RotatorSetting.Reverse, value ? 1 : 0, 4000);
                if (!r.Success) throw Failed("Cannot set the rotator direction", r);
                // ASCOM expects Reverse to persist: store it in the board's EEPROM
                r = Server.Controller.SaveSettings(6000);
                if (!r.Success) throw Failed("Rotator direction set but not saved", r);
            }
        }

        public void Halt()
        {
            RequireConnected();
            CommandResult r = Server.Controller.RotatorHalt(4000);
            if (!r.Success) throw Failed("Cannot halt the rotator", r);
        }

        private static void CheckAngle(string member, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0 || value >= 360)
                throw new InvalidValueException(member, value.ToString(CultureInfo.InvariantCulture), "0 to less than 360 degrees");
        }

        /// <summary>Relative move (sky = mechanical degrees), any finite amount; the result is taken modulo 360.</summary>
        public void Move(float position)
        {
            if (float.IsNaN(position) || float.IsInfinity(position))
                throw new InvalidValueException("Move", position.ToString(CultureInfo.InvariantCulture), "a finite angle in degrees");
            BoardSnapshot s = RequireConnected();
            double baseSky = RotatorState.TargetSky.HasValue && s.IsRotatorMoving ? RotatorState.TargetSky.Value : Sky(Mechanical(s));
            GoSky(RotatorState.Normalize(baseSky + position));
        }

        public void MoveAbsolute(float position)
        {
            CheckAngle("MoveAbsolute", position);
            RequireConnected();
            GoSky(position);
        }

        public void MoveMechanical(float position)
        {
            CheckAngle("MoveMechanical", position);
            RequireConnected();
            GoMechanical(position, Sky(position));
        }

        public void Sync(float position)
        {
            CheckAngle("Sync", position);
            BoardSnapshot s = RequireConnected();
            double offset = RotatorState.Normalize(position - Mechanical(s));
            RotatorState.SkyOffset = offset;
            RotatorState.TargetSky = null;
            Server.Log("rotator sync: sky " + position.ToString("0.##", CultureInfo.InvariantCulture) + " at mechanical " +
                       Mechanical(s).ToString("0.##", CultureInfo.InvariantCulture) + ", offset " + offset.ToString("0.##", CultureInfo.InvariantCulture));
        }

        private void GoSky(double sky)
        {
            GoMechanical(RotatorState.Normalize(sky - RotatorState.SkyOffset), sky);
        }

        private void GoMechanical(double mechanical, double sky)
        {
            int cdeg = (int)Math.Round(mechanical * 100.0) % 36000;
            BoardSnapshot s = Server.Controller.Snapshot;
            if (s.IsRotatorMoving)
            {
                Server.Controller.RotatorHalt(4000);
                if (!WaitIdle(WaitHaltMs)) throw new DriverException("The rotator did not stop for the new move");
            }
            else if (s.IsBusy && !WaitIdle(WaitOtherAxisMs))
                throw new DriverException("The board is busy (focuser moving) and did not finish within " + WaitOtherAxisMs / 1000 + " s");

            RotatorState.TargetSky = RotatorState.Normalize(sky);
            CommandResult r = Server.Controller.RotatorMoveTo(cdeg, 4000);
            if (!r.Success && r.ErrorCode == (int)BoardError.Busy && WaitIdle(WaitOtherAxisMs))
                r = Server.Controller.RotatorMoveTo(cdeg, 4000);
            if (r.Success) return;
            if (r.ErrorCode == (int)BoardError.BadParameter)
                throw new InvalidValueException("The rotator cannot reach mechanical " + (cdeg / 100.0).ToString("0.##", CultureInfo.InvariantCulture) +
                                                "° inside its cable-wrap range");
            throw Failed("Cannot move the rotator", r);
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
                c.Add("IsMoving", s.IsRotatorMoving);
                c.Add("MechanicalPosition", (float)Mechanical(s));
                c.Add("Position", (float)Sky(Mechanical(s)));
                c.AddUtcDateTime();
                return c;
            }
        }
    }
}
