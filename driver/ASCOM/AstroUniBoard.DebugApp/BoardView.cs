// What the app shows, independent of the connection path. Both paths go through the same
// dictionary shape: the direct path builds it from a BoardSnapshot (SnapshotJson.Build), the ASCOM
// path gets it as JSON from the driver's AstroUniBoard:Snapshot action. Keys are matched without
// case, enums may be ints or names, missing objects are null.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;
using AstroUniBoard.Core;

namespace AstroUniBoard.DebugApp
{
    internal sealed class IdentityView
    {
        public string Model, Firmware, Serial;
        public int Protocol, Caps;
        public bool Has(BoardCaps c) { return ((BoardCaps)Caps & c) == c; }
    }

    internal sealed class StatusView
    {
        public int State, FocuserPosition, FocuserTarget, RotatorAngle, RotatorTarget, Error, Flags;
        public bool Has(StatusFlags f) { return ((StatusFlags)Flags & f) == f; }
        public bool Busy { get { return State != (int)BoardState.Idle; } }
    }

    internal sealed class HousekeepingView
    {
        public double McuTemperature, Vdda;
        public int? DriverTemperatureClass, FocuserDriverTemperatureClass, RotatorDriverTemperatureClass;
        public int Warnings;
    }

    internal sealed class EnvironmentView
    {
        public int Source;
        public double Temperature, Humidity, DewPoint;
    }

    internal sealed class FocuserView
    {
        public int MaxPosition, Speed, Acceleration, Backlash, StepSizeCentiMicrons;
        public bool Reverse;
    }

    internal sealed class RotatorView
    {
        public int WrapMinCdeg, WrapMaxCdeg, SpeedCdegPerS, AccelerationCdegPerS2, StepsPerKiloDegree, HomeToleranceCdeg;
        public bool Reverse, BootHome;
    }

    internal sealed class BoardView
    {
        public string Connection = "Disconnected";
        public string Port, Problem;
        public IdentityView Identity;
        public StatusView Status;
        public HousekeepingView Housekeeping;
        public EnvironmentView Environment;
        public FocuserView Focuser;
        public RotatorView Rotator;

        public bool IsConnected { get { return string.Equals(Connection, "Connected", StringComparison.OrdinalIgnoreCase); } }

        public static readonly BoardView Disconnected = new BoardView();

        public static BoardView FromJson(string json)
        {
            object o = new JavaScriptSerializer().DeserializeObject(json);
            var d = o as IDictionary<string, object>;
            if (d == null) throw new FormatException("snapshot is not a JSON object");
            return FromDictionary(d);
        }

        public static BoardView FromDictionary(IDictionary<string, object> d)
        {
            var v = new BoardView();
            object conn = J.Get(d, "connection");
            v.Connection = conn is string s ? s : (conn == null ? "Disconnected" : ((ConnectionState)J.ToInt(conn)).ToString());
            v.Port = J.Str(d, "port");
            v.Problem = J.Str(d, "problem");

            var id = J.Obj(d, "identity");
            if (id != null)
                v.Identity = new IdentityView
                {
                    Model = J.Str(id, "model"), Firmware = J.Str(id, "firmware"), Serial = J.Str(id, "serial"),
                    Protocol = J.Int(id, "protocol", J.Int(id, "protocolVersion")), Caps = J.Int(id, "caps")
                };

            var st = J.Obj(d, "status");
            if (st != null)
                v.Status = new StatusView
                {
                    State = J.EnumInt<BoardState>(st, "state"), FocuserPosition = J.Int(st, "focuserPosition"), FocuserTarget = J.Int(st, "focuserTarget", -1),
                    RotatorAngle = J.Int(st, "rotatorAngle"), RotatorTarget = J.Int(st, "rotatorTarget", -1),
                    Error = J.Int(st, "error"), Flags = J.Int(st, "flags")
                };

            var hk = J.Obj(d, "housekeeping");
            if (hk != null)
                v.Housekeeping = new HousekeepingView
                {
                    McuTemperature = J.Dbl(hk, "mcuTemperature"), Vdda = J.Dbl(hk, "vdda"), Warnings = J.Int(hk, "warnings"),
                    DriverTemperatureClass = J.NInt(hk, "driverTemperatureClass"),
                    FocuserDriverTemperatureClass = J.NInt(hk, "focuserDriverTemperatureClass"),
                    RotatorDriverTemperatureClass = J.NInt(hk, "rotatorDriverTemperatureClass")
                };

            var env = J.Obj(d, "environment");
            if (env != null)
                v.Environment = new EnvironmentView
                {
                    Source = J.Int(env, "source"), Temperature = J.Dbl(env, "temperature"), Humidity = J.Dbl(env, "humidity"), DewPoint = J.Dbl(env, "dewPoint")
                };

            var f = J.Obj(d, "focuser");
            if (f != null)
                v.Focuser = new FocuserView
                {
                    MaxPosition = J.Int(f, "maxPosition"), Speed = J.Int(f, "speed"), Acceleration = J.Int(f, "acceleration"),
                    Backlash = J.Int(f, "backlash"), Reverse = J.Bool(f, "reverse"), StepSizeCentiMicrons = J.Int(f, "stepSizeCentiMicrons")
                };

            var r = J.Obj(d, "rotator");
            if (r != null)
                v.Rotator = new RotatorView
                {
                    WrapMinCdeg = J.Int(r, "wrapMinCdeg"), WrapMaxCdeg = J.Int(r, "wrapMaxCdeg"), SpeedCdegPerS = J.Int(r, "speedCdegPerS"),
                    AccelerationCdegPerS2 = J.Int(r, "accelerationCdegPerS2"), Reverse = J.Bool(r, "reverse"),
                    StepsPerKiloDegree = J.Int(r, "stepsPerKiloDegree"), BootHome = J.Bool(r, "bootHome"), HomeToleranceCdeg = J.Int(r, "homeToleranceCdeg")
                };
            return v;
        }
    }

    /// <summary>BoardSnapshot → the dictionary of the AstroUniBoard:Snapshot contract (camelCase, Core property names).</summary>
    internal static class SnapshotJson
    {
        public static Dictionary<string, object> Build(BoardSnapshot s)
        {
            var d = new Dictionary<string, object>
            {
                ["connection"] = s.Connection.ToString(),
                ["port"] = s.PortName,
                ["problem"] = s.Problem
            };
            BoardIdentity id = s.Identity;
            if (id != null)
                d["identity"] = new Dictionary<string, object>
                {
                    ["model"] = id.Model, ["firmware"] = id.Firmware, ["protocol"] = id.ProtocolVersion, ["serial"] = id.Serial, ["caps"] = (int)id.Caps
                };
            BoardStatus st = s.Status;
            if (st != null)
                d["status"] = new Dictionary<string, object>
                {
                    ["state"] = (int)st.State, ["focuserPosition"] = st.FocuserPosition, ["focuserTarget"] = st.FocuserTarget,
                    ["rotatorAngle"] = st.RotatorAngle, ["rotatorTarget"] = st.RotatorTarget, ["error"] = st.Error, ["flags"] = (int)st.Flags
                };
            Housekeeping hk = s.Housekeeping;
            if (hk != null)
                d["housekeeping"] = new Dictionary<string, object>
                {
                    ["mcuTemperature"] = hk.McuTemperature, ["vdda"] = hk.Vdda, ["driverTemperatureClass"] = hk.DriverTemperatureClass,
                    ["warnings"] = (int)hk.Warnings, ["focuserDriverTemperatureClass"] = hk.FocuserDriverTemperatureClass,
                    ["rotatorDriverTemperatureClass"] = hk.RotatorDriverTemperatureClass
                };
            EnvironmentReading e = s.Environment;
            if (e != null)
                d["environment"] = new Dictionary<string, object>
                {
                    ["source"] = e.Source, ["temperature"] = e.Temperature, ["humidity"] = e.Humidity, ["dewPoint"] = e.DewPoint
                };
            FocuserSettings f = s.Focuser;
            if (f != null)
                d["focuser"] = new Dictionary<string, object>
                {
                    ["maxPosition"] = f.MaxPosition, ["speed"] = f.Speed, ["acceleration"] = f.Acceleration, ["backlash"] = f.Backlash,
                    ["reverse"] = f.Reverse, ["stepSizeCentiMicrons"] = f.StepSizeCentiMicrons
                };
            RotatorSettings r = s.Rotator;
            if (r != null)
                d["rotator"] = new Dictionary<string, object>
                {
                    ["wrapMinCdeg"] = r.WrapMinCdeg, ["wrapMaxCdeg"] = r.WrapMaxCdeg, ["speedCdegPerS"] = r.SpeedCdegPerS,
                    ["accelerationCdegPerS2"] = r.AccelerationCdegPerS2, ["reverse"] = r.Reverse, ["stepsPerKiloDegree"] = r.StepsPerKiloDegree,
                    ["bootHome"] = r.BootHome, ["homeToleranceCdeg"] = r.HomeToleranceCdeg
                };
            return d;
        }

        public static string ToJson(object o) { return new JavaScriptSerializer().Serialize(o); }
    }

    /// <summary>Tolerant accessors for JavaScriptSerializer output.</summary>
    internal static class J
    {
        public static object Get(IDictionary<string, object> d, string key)
        {
            object v;
            if (d.TryGetValue(key, out v)) return v;
            foreach (var kv in d)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        public static IDictionary<string, object> Obj(IDictionary<string, object> d, string key) { return Get(d, key) as IDictionary<string, object>; }
        public static string Str(IDictionary<string, object> d, string key) { object v = Get(d, key); return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture); }
        public static int Int(IDictionary<string, object> d, string key, int dflt = 0) { object v = Get(d, key); return v == null ? dflt : ToInt(v); }
        public static int? NInt(IDictionary<string, object> d, string key) { object v = Get(d, key); return v == null ? (int?)null : ToInt(v); }
        public static double Dbl(IDictionary<string, object> d, string key) { object v = Get(d, key); return v == null ? double.NaN : Convert.ToDouble(v, CultureInfo.InvariantCulture); }

        public static bool Bool(IDictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v == null) return false;
            if (v is bool b) return b;
            if (v is string s) return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
            return ToInt(v) != 0;
        }

        /// <summary>An enum given as a number or as its name.</summary>
        public static int EnumInt<T>(IDictionary<string, object> d, string key) where T : struct
        {
            object v = Get(d, key);
            T e;
            if (v is string s && !int.TryParse(s, out _) && Enum.TryParse(s, true, out e)) return Convert.ToInt32(e, CultureInfo.InvariantCulture);
            return v == null ? 0 : ToInt(v);
        }

        public static int ToInt(object v)
        {
            if (v is string s)
            {
                int n;
                if (int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) return n;
                throw new FormatException("not a number: " + s);
            }
            return Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }

        public static IEnumerable<IDictionary<string, object>> Array(object o)
        {
            var list = o as IEnumerable;
            if (list == null || o is string) yield break;
            foreach (object item in list)
            {
                var d = item as IDictionary<string, object>;
                if (d != null) yield return d;
            }
        }
    }
}
