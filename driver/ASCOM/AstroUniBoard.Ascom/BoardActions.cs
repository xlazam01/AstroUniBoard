// The Action contract shared by all three devices (driver/ASCOM/SPEC.md §5). The control & debug
// app builds against exactly this, so names, parameters and JSON keys must not change silently.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ASCOM;
using AstroUniBoard.Core;

namespace AstroUniBoard.Ascom
{
    internal static class BoardActions
    {
        public const string Prefix = "AstroUniBoard:";
        public const string ErrorPrefix = "ERROR: ";
        public const string OkReply = "OK";

        public const string RawAction = Prefix + "Raw";
        public const string SnapshotAction = Prefix + "Snapshot";
        public const string LogAction = Prefix + "Log";

        public static readonly string[] Names =
        {
            RawAction, SnapshotAction, LogAction,
            Prefix + "HaltAll", Prefix + "ClearError", Prefix + "SaveSettings",
            Prefix + "ImuHome", Prefix + "ImuCalibrate", Prefix + "Reboot", Prefix + "EnterDfu"
        };

        /// <summary>Time the board-side commands may take before the action reports a timeout.</summary>
        private const int WaitMs = 4000;

        public static string Run(string actionName, string actionParameters, bool instanceConnected)
        {
            string name = (actionName ?? string.Empty).Trim();
            string match = null;
            foreach (string n in Names)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { match = n; break; }
            if (match == null)
                throw new ActionNotImplementedException("Action " + actionName + " is not supported. Supported: " + string.Join(", ", Names));

            // Snapshot and Log also work before Connected, so a status window can show "looking for the board".
            if (match == SnapshotAction) return SnapshotJson(Server.Controller.Snapshot);
            if (match == LogAction) return LogJson(actionParameters);

            if (!instanceConnected) return ErrorPrefix + "not connected (set Connected = true first)";
            if (!Server.Controller.Snapshot.IsConnected) return ErrorPrefix + "board is not connected: " + (Server.Controller.Snapshot.Problem ?? "looking for it");

            BoardController c = Server.Controller;
            CommandResult r;
            switch (match.Substring(Prefix.Length))
            {
                case "Raw": return Raw(actionParameters);
                case "HaltAll": r = c.HaltAll(WaitMs); break;
                case "ClearError": r = c.ClearError(WaitMs); break;
                case "SaveSettings": r = c.SaveSettings(WaitMs + 2000); break;
                case "ImuHome": r = c.RotatorImuHome(WaitMs); break;
                case "ImuCalibrate": r = c.ImuCalibrate(WaitMs); break;
                case "Reboot": r = c.Reboot(WaitMs); break;
                case "EnterDfu": r = c.EnterDfu(WaitMs); break;
                default: throw new ActionNotImplementedException(actionName);
            }
            Server.Log("action " + match + ": " + r);
            return r.Success ? OkReply : ErrorPrefix + r.Message;
        }

        /// <summary>Sends ":NN...#" and returns the board's reply line including the '#', or "ERROR: ...".</summary>
        public static string Raw(string command)
        {
            string cmd = (command ?? string.Empty).Trim();
            if (!Server.Controller.Snapshot.IsConnected) return ErrorPrefix + "board is not connected";
            Reply reply;
            CommandResult r = Server.Controller.Raw(cmd, out reply, WaitMs);
            Server.Log("raw " + cmd + " -> " + (r.Success && reply != null ? reply + "#" : "failed: " + r.Message));
            if (!r.Success || reply == null) return ErrorPrefix + r.Message;
            return reply + "#";
        }

        // ---------------------------------------------------------------- Log

        private static string LogJson(string afterParam)
        {
            long after = 0;
            string p = (afterParam ?? string.Empty).Trim();
            if (p.Length > 0 && !long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out after))
                throw new InvalidValueException("Action " + LogAction + ": the parameter must be empty or the last seen sequence number");
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (LogBuffer.Entry e in Server.LogRing.After(after))
            {
                if (!first) sb.Append(',');
                first = false;
                var j = new Json(sb);
                j.Begin();
                j.Num("seq", e.Seq);
                j.Str("time", e.Time.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
                j.Str("text", e.Text);
                j.End();
            }
            sb.Append(']');
            return sb.ToString();
        }

        // ---------------------------------------------------------------- Snapshot

        public static string SnapshotJson(BoardSnapshot s)
        {
            var sb = new StringBuilder();
            var j = new Json(sb);
            j.Begin();
            j.Str("connection", s.Connection.ToString());
            j.Str("port", s.PortName);
            j.Str("problem", s.Problem);

            BoardIdentity id = s.Identity;
            if (id == null) j.Null("identity");
            else
            {
                j.Obj("identity");
                j.Str("model", id.Model);
                j.Str("firmware", id.Firmware);
                j.Num("protocol", id.ProtocolVersion);
                j.Str("serial", id.Serial);
                j.Num("caps", (int)id.Caps);
                j.End();
            }

            BoardStatus st = s.Status;
            if (st == null) j.Null("status");
            else
            {
                j.Obj("status");
                j.Str("state", st.State.ToString());
                j.Num("stateCode", (int)st.State);
                j.Num("focuserPosition", st.FocuserPosition);
                j.Num("focuserTarget", st.FocuserTarget);
                j.Num("rotatorAngleCdeg", st.RotatorAngle);
                j.Num("rotatorTargetCdeg", st.RotatorTarget);
                j.Num("error", st.Error);
                j.Str("errorText", st.Error == 0 ? null : BoardErrors.Describe(st.Error));
                j.Num("flags", (int)st.Flags);
                j.Str("timeUtc", Iso(s.StatusTimeUtc));
                j.End();
            }

            Housekeeping h = s.Housekeeping;
            if (h == null) j.Null("housekeeping");
            else
            {
                j.Obj("housekeeping");
                j.Dbl("mcuTemperature", h.McuTemperature);
                j.Dbl("vdda", h.Vdda);
                j.NumOrNull("driverTemperatureClass", h.DriverTemperatureClass);
                j.NumOrNull("focuserDriverTemperatureClass", h.FocuserDriverTemperatureClass);
                j.NumOrNull("rotatorDriverTemperatureClass", h.RotatorDriverTemperatureClass);
                j.Num("warnings", (int)h.Warnings);
                j.Str("warningsText", h.Warnings == HousekeepingWarnings.None ? null : Housekeeping.Describe(h.Warnings));
                j.End();
            }

            EnvironmentReading e = s.Environment;
            if (e == null) j.Null("environment");
            else
            {
                j.Obj("environment");
                j.Num("source", e.Source);
                j.Dbl("temperature", e.Temperature);
                j.Dbl("humidity", e.Humidity);
                j.Dbl("dewPoint", e.DewPoint);
                j.Str("timeUtc", Iso(e.TimeUtc));
                j.End();
            }

            FocuserSettings f = s.Focuser;
            if (f == null) j.Null("focuser");
            else
            {
                j.Obj("focuser");
                j.Num("maxPosition", f.MaxPosition);
                j.Num("speed", f.Speed);
                j.Num("acceleration", f.Acceleration);
                j.Num("backlash", f.Backlash);
                j.Bool("reverse", f.Reverse);
                j.Num("stepSizeCentiMicrons", f.StepSizeCentiMicrons);
                j.End();
            }

            RotatorSettings r = s.Rotator;
            if (r == null) j.Null("rotator");
            else
            {
                j.Obj("rotator");
                j.Num("wrapMinCdeg", r.WrapMinCdeg);
                j.Num("wrapMaxCdeg", r.WrapMaxCdeg);
                j.Num("speedCdegPerS", r.SpeedCdegPerS);
                j.Num("accelerationCdegPerS2", r.AccelerationCdegPerS2);
                j.Bool("reverse", r.Reverse);
                j.Num("stepsPerKiloDegree", r.StepsPerKiloDegree);
                j.Bool("bootHome", r.BootHome);
                j.Num("homeToleranceCdeg", r.HomeToleranceCdeg);
                j.End();
            }

            // host-side state of this LocalServer
            j.Obj("host");
            j.Str("driverVersion", Identity.DriverVersion);
            j.Num("clients", Server.ConnectionClients);
            j.Dbl("skyOffsetDegrees", RotatorState.SkyOffset);
            j.Bool("tempComp", Server.TempComp.Enabled);
            j.Dbl("tempCompStepsPerDegree", Server.TempComp.StepsPerDegree);
            j.Dbl("tempCompThresholdDegrees", Server.TempComp.ThresholdDegrees);
            j.End();

            j.End();
            return sb.ToString();
        }

        private static string Iso(DateTime utc)
        {
            return utc == DateTime.MinValue ? null : utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        /// <summary>Tiny JSON writer (no dependency; net48 has no System.Text.Json).</summary>
        private sealed class Json
        {
            private readonly StringBuilder sb;
            private readonly Stack<bool> first = new Stack<bool>();

            public Json(StringBuilder sb) { this.sb = sb; }

            public void Begin() { sb.Append('{'); first.Push(true); }
            public void End() { sb.Append('}'); first.Pop(); }

            private void Key(string k)
            {
                if (!first.Peek()) sb.Append(',');
                first.Pop(); first.Push(false);
                sb.Append('"').Append(k).Append("\":");
            }

            public void Obj(string k) { Key(k); Begin(); }
            public void Null(string k) { Key(k); sb.Append("null"); }
            public void Num(string k, long v) { Key(k); sb.Append(v.ToString(CultureInfo.InvariantCulture)); }
            public void NumOrNull(string k, int? v) { if (v.HasValue) Num(k, v.Value); else Null(k); }
            public void Bool(string k, bool v) { Key(k); sb.Append(v ? "true" : "false"); }

            public void Dbl(string k, double v)
            {
                Key(k);
                sb.Append(double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("0.######", CultureInfo.InvariantCulture));
            }

            public void Str(string k, string v)
            {
                Key(k);
                if (v == null) { sb.Append("null"); return; }
                sb.Append('"');
                foreach (char c in v)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
            }
        }
    }
}
