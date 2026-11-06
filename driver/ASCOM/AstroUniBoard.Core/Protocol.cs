// Protocol 2.0 types and parsers. See docs/PROTOCOL.md. Pure functions, no I/O.
using System;
using System.Globalization;

namespace AstroUniBoard.Core
{
    /// <summary>Firmware state (field 1 of the status reply, PROTOCOL.md 3.2).</summary>
    public enum BoardState
    {
        Init = 0,
        Idle = 1,
        FocuserMoving = 2,
        RotatorMoving = 3,
        /// <summary>Rotator IMU homing (:77) or IMU zero calibration (:95).</summary>
        ImuBusy = 4,
        Halting = 5
    }

    /// <summary>Axis selector of the common commands (:41 :42 :47 :48).</summary>
    public enum Axis { Focuser, Rotator }

    /// <summary>Error codes, same numbers as fw/src/error_codes.h.</summary>
    public enum BoardError
    {
        None = 0,
        HomeTimeout = 1,
        MoveTimeout = 2,
        CalibrationFailed = 3,
        Eeprom = 4,
        MotorDriver = 5,
        BadParameter = 7,
        Busy = 8,
        NotCalibrated = 9,
        UnknownCommand = 10,
        BadFormat = 11,
        NotSupported = 12,
        MotorStall = 13,
        Sensor = 14,
        ImuVertical = 15,
        NotIdentified = 16
    }

    public static class BoardErrors
    {
        /// <summary>True for sticky device faults (codes 1..5 and 13), false for command rejections.</summary>
        public static bool IsFault(int code) { return (code >= 1 && code <= 5) || code == 13; }

        /// <summary>Why a rotator move is refused while the board flags its angle unverified (#75).</summary>
        public const string RotatorUnverifiedHint =
            "the rotator angle is unverified (power lost during a move, rotator turned by hand while the board was off, or safe mode), " +
            "so the board refuses rotator moves to protect the cable wrap. Confirm the angle first: IMU home (:77#), or turn the rotator " +
            "to its home and use \"Set home here\" (:76,0#) in the AstroUniBoard debug app (Axes tab)";

        /// <summary>
        /// True when a rejected rotator move is the unverified-angle refusal of #75.
        /// TODO(#75): the firmware's error code for this refusal is not decided yet. Until it is, any
        /// rejection other than busy counts while the status has <see cref="StatusFlags.RotatorUnverified"/>;
        /// once PROTOCOL.md names the code, add it to <see cref="BoardError"/> and <see cref="Describe"/> and match it here.
        /// </summary>
        public static bool IsRotatorUnverifiedRefusal(int code, BoardStatus status)
        {
            return status != null && IsRotatorUnverifiedRefusal(code, status.Flags);
        }

        public static bool IsRotatorUnverifiedRefusal(int code, StatusFlags flags)
        {
            return code > 0 && code != (int)BoardError.Busy && (flags & StatusFlags.RotatorUnverified) != 0;
        }

        public static string Describe(int code)
        {
            switch (code)
            {
                case 0: return "no error";
                case 1: return "rotator IMU homing did not finish";
                case 2: return "move did not finish in its expected time";
                case 3: return "IMU zero calibration failed (rotator slipped or IMU moved)";
                case 4: return "EEPROM missing or write failed";
                case 5: return "motor driver (TMC2209) not answering or reporting overtemperature/short circuit";
                case 7: return "bad parameter or target outside the limits";
                case 8: return "busy (an axis is moving, homing or calibrating)";
                case 9: return "IMU zero is not calibrated";
                case 10: return "unknown command (not in this firmware)";
                case 11: return "malformed command";
                case 12: return "setting cannot be changed at runtime";
                case 13: return "motor stalled (StallGuard: drawtube or rotator blocked)";
                case 14: return "sensor missing or no valid reading";
                case 15: return "optical axis too close to vertical for the IMU";
                case 16: return "board not identified on this connection";
                default: return "unknown error code " + code.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    internal static class Fields
    {
        /// <summary>Splits "TAG,a,b,..." and parses the numbers after the tag. hexMask marks fields (0-based after the tag) that are hex.</summary>
        public static bool TryInts(string value, string tag, int count, out int[] n, int hexMask = 0)
        {
            n = null;
            if (string.IsNullOrEmpty(value)) return false;
            string[] p = value.Split(',');
            if (p.Length != count + 1 || p[0] != tag) return false;
            var r = new int[count];
            for (int i = 0; i < count; i++)
            {
                bool hex = (hexMask & (1 << i)) != 0;
                if (hex)
                {
                    if (!int.TryParse(p[i + 1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out r[i])) return false;
                }
                else if (!int.TryParse(p[i + 1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out r[i])) return false;
            }
            n = r;
            return true;
        }
    }

    /// <summary>Capability bits of the identify reply (what the firmware build supports).</summary>
    [Flags]
    public enum BoardCaps
    {
        None = 0,
        Focuser = 1,
        Rotator = 2,
        Imu = 4,
        Environment = 8
    }

    /// <summary>Result of :32# (identify).</summary>
    public sealed class BoardIdentity
    {
        public string Model { get; private set; }
        public string Firmware { get; private set; }
        public int ProtocolVersion { get; private set; }
        public string Serial { get; private set; }
        public BoardCaps Caps { get; private set; }

        public const string Signature = "ASTROUNIBOARD";
        public const int MinProtocolVersion = 2;
        public const int MaxProtocolVersion = 2;

        /// <summary>Parses "ASTROUNIBOARD,model,firmware,protocol,serial,caps(hex)" (without the '#').</summary>
        public static bool TryParse(string value, out BoardIdentity identity)
        {
            identity = null;
            if (string.IsNullOrEmpty(value)) return false;
            string[] p = value.Split(',');
            int proto, caps;
            if (p.Length != 6 || p[0] != Signature) return false;
            if (!int.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out proto)) return false;
            if (!int.TryParse(p[5], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out caps)) return false;
            if (p[1].Length == 0 || p[2].Length == 0 || p[4].Length == 0) return false;
            identity = new BoardIdentity { Model = p[1], Firmware = p[2], ProtocolVersion = proto, Serial = p[4], Caps = (BoardCaps)caps };
            return true;
        }

        public bool IsSupported { get { return ProtocolVersion >= MinProtocolVersion && ProtocolVersion <= MaxProtocolVersion; } }
        public bool Has(BoardCaps c) { return (Caps & c) == c; }

        public override string ToString()
        {
            return Model + " " + Firmware + " (protocol " + ProtocolVersion + ", serial " + Serial + ", caps " + Caps + ")";
        }
    }

    [Flags]
    public enum StatusFlags
    {
        None = 0,
        FocuserUnverified = 1,
        RotatorUnverified = 2,
        EepromFault = 4,
        HousekeepingWarning = 8,
        OnboardShtValid = 16,
        ExternalShtValid = 32,
        ImuAnswering = 64,
        ImuNotCalibrated = 128,
        SafeMode = 256
    }

    /// <summary>Result of :33# (status).</summary>
    public sealed class BoardStatus
    {
        public BoardState State { get; private set; }
        public int FocuserPosition { get; private set; }
        /// <summary>Target of the running focuser move, else -1.</summary>
        public int FocuserTarget { get; private set; }
        /// <summary>Rotator mechanical angle 0..35999 cdeg.</summary>
        public int RotatorAngle { get; private set; }
        /// <summary>Target of the running rotator move (0..35999), else -1.</summary>
        public int RotatorTarget { get; private set; }
        public int Error { get; private set; }
        public StatusFlags Flags { get; private set; }

        public bool Has(StatusFlags f) { return (Flags & f) == f; }
        public bool Busy { get { return State != BoardState.Idle; } }
        public bool FocuserMoving { get { return State == BoardState.FocuserMoving || (State == BoardState.Halting && FocuserTarget >= 0); } }
        public bool RotatorMoving { get { return State == BoardState.RotatorMoving || State == BoardState.ImuBusy || (State == BoardState.Halting && RotatorTarget >= 0); } }

        /// <summary>Parses "S,state,foc_pos,foc_target,rot_cdeg,rot_target,error,flags(hex)" (without the '#').</summary>
        public static bool TryParse(string value, out BoardStatus status)
        {
            status = null;
            int[] n;
            if (!Fields.TryInts(value, "S", 7, out n, 1 << 6)) return false;
            if (n[0] < 0 || n[0] > 5) return false;
            if (n[2] < -1 || n[3] < 0 || n[3] > 35999 || n[4] < -1 || n[4] > 35999) return false;
            if (n[5] < 0 || n[5] > 255 || n[6] < 0 || n[6] > 0xFFFF) return false;
            status = new BoardStatus
            {
                State = (BoardState)n[0],
                FocuserPosition = n[1],
                FocuserTarget = n[2],
                RotatorAngle = n[3],
                RotatorTarget = n[4],
                Error = n[5],
                Flags = (StatusFlags)n[6]
            };
            return true;
        }

        public override string ToString()
        {
            return "state=" + State + " foc=" + FocuserPosition + "->" + FocuserTarget + " rot=" + RotatorAngle + "->" + RotatorTarget +
                   " error=" + Error + " flags=" + Flags;
        }
    }

    [Flags]
    public enum HousekeepingWarnings
    {
        None = 0,
        SupplyLow = 1,
        DriverOvertemperature = 2,
        DriverReset = 4,
        NoCrystal = 8
    }

    /// <summary>Result of :40# (housekeeping).</summary>
    public sealed class Housekeeping
    {
        public double McuTemperature { get; private set; }
        public double Vdda { get; private set; }
        /// <summary>Worst TMC2209 temperature class (0, 120, 143, 150, 157), null when a driver does not answer.</summary>
        public int? DriverTemperatureClass { get; private set; }
        public HousekeepingWarnings Warnings { get; private set; }
        public int? FocuserDriverTemperatureClass { get; private set; }
        public int? RotatorDriverTemperatureClass { get; private set; }

        /// <summary>Parses "H,mcu(0.1C),vdda(mV),v5,v5min,drv,warnings(hex),foc_drv,rot_drv" (without the '#').</summary>
        public static bool TryParse(string value, out Housekeeping hk)
        {
            hk = null;
            int[] n;
            if (!Fields.TryInts(value, "H", 8, out n, 1 << 5)) return false;
            if (n[0] < -600 || n[0] > 1500 || n[1] < 0 || n[1] > 4000 || n[5] < 0 || n[5] > 255) return false;
            hk = new Housekeeping
            {
                McuTemperature = n[0] / 10.0,
                Vdda = n[1] / 1000.0,
                DriverTemperatureClass = n[4] < 0 ? (int?)null : n[4],
                Warnings = (HousekeepingWarnings)n[5],
                FocuserDriverTemperatureClass = n[6] < 0 ? (int?)null : n[6],
                RotatorDriverTemperatureClass = n[7] < 0 ? (int?)null : n[7]
            };
            return true;
        }

        public static string Describe(HousekeepingWarnings w)
        {
            if (w == HousekeepingWarnings.None) return "none";
            var parts = new System.Collections.Generic.List<string>();
            if ((w & HousekeepingWarnings.SupplyLow) != 0) parts.Add("supply low");
            if ((w & HousekeepingWarnings.DriverOvertemperature) != 0) parts.Add("motor driver overtemperature prewarning");
            if ((w & HousekeepingWarnings.DriverReset) != 0) parts.Add("a motor driver was reset by a supply dip and re-initialised");
            if ((w & HousekeepingWarnings.NoCrystal) != 0) parts.Add("crystal not running, clocks from the internal oscillator");
            return string.Join("; ", parts.ToArray());
        }

        public override string ToString()
        {
            return "mcu=" + McuTemperature.ToString("0.0", CultureInfo.InvariantCulture) + "C vdda=" +
                   Vdda.ToString("0.000", CultureInfo.InvariantCulture) + "V warnings=" + Warnings;
        }
    }

    /// <summary>Result of :41,F# / :41,R#.</summary>
    public sealed class StallGuardInfo
    {
        public int Threshold { get; private set; }
        public int? Last { get; private set; }
        public int? Min { get; private set; }
        public int? Max { get; private set; }

        public static bool TryParse(string value, out StallGuardInfo info)
        {
            info = null;
            int[] n;
            if (!Fields.TryInts(value, "G", 4, out n)) return false;
            if (n[0] < 0 || n[0] > 255) return false;
            info = new StallGuardInfo
            {
                Threshold = n[0],
                Last = n[1] < 0 ? (int?)null : n[1],
                Min = n[2] < 0 ? (int?)null : n[2],
                Max = n[3] < 0 ? (int?)null : n[3]
            };
            return true;
        }

        /// <summary>A third of the lowest reading (firmware stops at SG_RESULT &lt;= 2 x threshold). Null without data.</summary>
        public int? SuggestedThreshold
        {
            get { return Min.HasValue && Min.Value > 0 ? (int?)Math.Max(1, Math.Min(255, Min.Value / 3)) : null; }
        }
    }

    /// <summary>Result of :47,F# (TMC2209 config of one axis).</summary>
    public sealed class DriverConfig
    {
        public int RunCurrentMa { get; set; }
        public int HoldCurrentMa { get; set; }
        public int Microsteps { get; set; }
        /// <summary>0 StealthChop, 1 SpreadCycle.</summary>
        public int Mode { get; set; }
        public int IdleOffSeconds { get; set; }

        public static bool TryParse(string value, out DriverConfig cfg)
        {
            cfg = null;
            int[] n;
            if (!Fields.TryInts(value, "D", 5, out n)) return false;
            if (n[0] < 0 || n[1] < 0 || n[2] < 1 || n[2] > 256 || n[3] < 0 || n[3] > 1 || n[4] < 0) return false;
            cfg = new DriverConfig { RunCurrentMa = n[0], HoldCurrentMa = n[1], Microsteps = n[2], Mode = n[3], IdleOffSeconds = n[4] };
            return true;
        }
    }

    /// <summary>Result of :90# (environment from the selected SHT31).</summary>
    public sealed class EnvironmentReading
    {
        /// <summary>1 on-board, 2 external (J9).</summary>
        public int Source { get; private set; }
        public double Temperature { get; private set; }
        public double Humidity { get; private set; }
        public double DewPoint { get; private set; }
        public DateTime TimeUtc { get; private set; }

        public static bool TryParse(string value, out EnvironmentReading env)
        {
            env = null;
            int[] n;
            if (!Fields.TryInts(value, "W", 4, out n)) return false;
            if (n[0] < 1 || n[0] > 2 || n[1] < -600 || n[1] > 1500 || n[2] < 0 || n[2] > 1000 || n[3] < -1000 || n[3] > 1500) return false;
            env = new EnvironmentReading { Source = n[0], Temperature = n[1] / 10.0, Humidity = n[2] / 10.0, DewPoint = n[3] / 10.0, TimeUtc = DateTime.UtcNow };
            return true;
        }

        public override string ToString()
        {
            return (Source == 1 ? "on-board" : "external") + " " + Temperature.ToString("0.0", CultureInfo.InvariantCulture) + "C " +
                   Humidity.ToString("0.0", CultureInfo.InvariantCulture) + "%RH dew " + DewPoint.ToString("0.0", CultureInfo.InvariantCulture) + "C";
        }
    }

    /// <summary>Result of :94# (IMU reading).</summary>
    public sealed class ImuReading
    {
        public const int Undefined = -99999;
        /// <summary>Camera roll relative to the IMU zero, cdeg; null when uncalibrated or too steep.</summary>
        public int? RollCdeg { get; private set; }
        /// <summary>Angle between the optical axis and vertical, cdeg; null when uncalibrated.</summary>
        public int? TiltCdeg { get; private set; }
        public int Ax { get; private set; }
        public int Ay { get; private set; }
        public int Az { get; private set; }

        public static bool TryParse(string value, out ImuReading imu)
        {
            imu = null;
            int[] n;
            if (!Fields.TryInts(value, "I", 5, out n)) return false;
            imu = new ImuReading
            {
                RollCdeg = n[0] == Undefined ? (int?)null : n[0],
                TiltCdeg = n[1] == Undefined ? (int?)null : n[1],
                Ax = n[2], Ay = n[3], Az = n[4]
            };
            return true;
        }
    }

    /// <summary>Result of :46# (reset and safe-mode info).</summary>
    public sealed class ResetInfo
    {
        public int ResetFlags { get; private set; }
        public int LastMarker { get; private set; }
        public bool SafeMode { get; private set; }

        public static bool TryParse(string value, out ResetInfo info)
        {
            info = null;
            int[] n;
            if (!Fields.TryInts(value, "R", 3, out n, 1)) return false;
            info = new ResetInfo { ResetFlags = n[0], LastMarker = n[1], SafeMode = n[2] != 0 };
            return true;
        }

        public bool WatchdogReset { get { return (ResetFlags & 0x20) != 0 || (ResetFlags & 0x40) != 0; } }
    }

    /// <summary>Focuser values read with :54 :58 :60 :62 :64 :66 (cached by the controller).</summary>
    public sealed class FocuserSettings
    {
        public int MaxPosition { get; internal set; }
        public int Speed { get; internal set; }
        public int Acceleration { get; internal set; }
        public int Backlash { get; internal set; }
        public bool Reverse { get; internal set; }
        /// <summary>0.01 µm per step, 0 = unknown.</summary>
        public int StepSizeCentiMicrons { get; internal set; }
    }

    /// <summary>Rotator values read with :74 :78 :80 :82 :84 :86 :88 (cached by the controller).</summary>
    public sealed class RotatorSettings
    {
        public int WrapMinCdeg { get; internal set; }
        public int WrapMaxCdeg { get; internal set; }
        public int SpeedCdegPerS { get; internal set; }
        public int AccelerationCdegPerS2 { get; internal set; }
        public bool Reverse { get; internal set; }
        /// <summary>Microsteps per 1000 degrees.</summary>
        public int StepsPerKiloDegree { get; internal set; }
        public bool BootHome { get; internal set; }
        public int HomeToleranceCdeg { get; internal set; }

        /// <summary>Smallest angle step the motor makes, degrees.</summary>
        public double StepSizeDegrees { get { return StepsPerKiloDegree > 0 ? 1000.0 / StepsPerKiloDegree : 0; } }
    }

    public enum ReplyKind
    {
        Value,
        Ok,
        Error,
        /// <summary>Nothing (or no terminating '#') within the timeout.</summary>
        Timeout,
        /// <summary>Bytes arrived but they are not a plausible reply.</summary>
        Garbage
    }

    /// <summary>One answer line from the board.</summary>
    public sealed class Reply
    {
        public ReplyKind Kind { get; private set; }
        public string Value { get; private set; }
        public int ErrorCode { get; private set; }

        public bool IsOk { get { return Kind == ReplyKind.Ok; } }
        public bool IsTransportFailure { get { return Kind == ReplyKind.Timeout || Kind == ReplyKind.Garbage; } }

        public static readonly Reply TimeoutReply = new Reply { Kind = ReplyKind.Timeout };
        public static readonly Reply GarbageReply = new Reply { Kind = ReplyKind.Garbage };

        public static Reply Parse(string line)
        {
            if (line == null) return GarbageReply;
            foreach (char c in line)
            {
                if (c < 0x20 || c > 0x7E) return GarbageReply;
            }
            if (line == "OK") return new Reply { Kind = ReplyKind.Ok };
            if (line.StartsWith("ERR,", StringComparison.Ordinal))
            {
                int code;
                if (int.TryParse(line.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out code) && code <= 255)
                    return new Reply { Kind = ReplyKind.Error, ErrorCode = code };
                return GarbageReply;
            }
            return new Reply { Kind = ReplyKind.Value, Value = line };
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case ReplyKind.Ok: return "OK";
                case ReplyKind.Error: return "ERR," + ErrorCode;
                case ReplyKind.Value: return Value;
                default: return Kind.ToString();
            }
        }
    }

    /// <summary>Command builders (PROTOCOL.md 3 and 4). Commands are at most 46 characters between ':' and '#'.</summary>
    public static class Commands
    {
        public const int MaxCommandBody = 46;

        public const string Reboot = ":22#";
        public const string SaveSettings = ":23#";
        public const string ResetSettings = ":24#";
        public const string Ping = ":25#";
        public const string HaltAll = ":30#";
        public const string EnterDfu = ":31#";
        public const string Identify = ":32#";
        public const string Status = ":33#";
        public const string GetError = ":34#";
        public const string ClearError = ":34,0#";
        public const string Housekeeping = ":40#";
        public const string GetLedBrightness = ":43#";
        public const string ResetInfo = ":46#";

        public const string FocuserPosition = ":50#";
        public const string FocuserHalt = ":53#";
        public const string RotatorAngle = ":70#";
        public const string RotatorHalt = ":73#";
        public const string RotatorImuHome = ":77#";
        public const string Environment = ":90#";
        public const string EnvironmentDetail = ":91#";
        public const string GetEnvironmentSource = ":92#";
        public const string Imu = ":94#";
        public const string ImuCalibrate = ":95#";
        public const string ImuCalibrationData = ":96#";

        private static string I(int v) { return v.ToString(CultureInfo.InvariantCulture); }
        public static string AxisLetter(Axis a) { return a == Axis.Focuser ? "F" : "R"; }

        /// <summary>":NN,v#" for any set command.</summary>
        public static string Set(int number, int value) { return ":" + number.ToString("00", CultureInfo.InvariantCulture) + "," + I(value) + "#"; }
        /// <summary>":NN#" for any get command.</summary>
        public static string Get(int number) { return ":" + number.ToString("00", CultureInfo.InvariantCulture) + "#"; }

        public static string GetStallGuard(Axis a) { return ":41," + AxisLetter(a) + "#"; }
        public static string SetStallThreshold(Axis a, int threshold)
        {
            if (threshold < 0 || threshold > 255) throw new ArgumentOutOfRangeException("threshold");
            return ":42," + AxisLetter(a) + "," + I(threshold) + "#";
        }
        public static string SetLedBrightness(int percent)
        {
            if (percent < 0 || percent > 100) throw new ArgumentOutOfRangeException("percent");
            return ":44," + I(percent) + "#";
        }
        public static string GetDriverConfig(Axis a) { return ":47," + AxisLetter(a) + "#"; }
        public static string SetDriverConfig(Axis a, DriverConfig c)
        {
            return ":48," + AxisLetter(a) + "," + I(c.RunCurrentMa) + "," + I(c.HoldCurrentMa) + "," + I(c.Microsteps) + "," + I(c.Mode) + "," + I(c.IdleOffSeconds) + "#";
        }

        public static string FocuserMoveTo(int steps) { return Set(51, steps); }
        public static string FocuserMoveBy(int steps) { return Set(52, steps); }
        public static string FocuserSync(int steps) { return Set(56, steps); }

        public static string RotatorMoveTo(int cdeg0to35999)
        {
            if (cdeg0to35999 < 0 || cdeg0to35999 > 35999) throw new ArgumentOutOfRangeException("cdeg0to35999");
            return Set(71, cdeg0to35999);
        }
        public static string RotatorMoveBy(int cdeg) { return Set(72, cdeg); }
        public static string RotatorSetWrap(int minCdeg, int maxCdeg) { return ":75," + I(minCdeg) + "," + I(maxCdeg) + "#"; }
        public static string RotatorSync(int signedCdeg) { return Set(76, signedCdeg); }
        public static string SetEnvironmentSource(int source) { return Set(93, source); }
    }
}
