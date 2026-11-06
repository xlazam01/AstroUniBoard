// Status tab: identity, state, positions, error with text and Clear, decoded flags, housekeeping
// (:40#) and reset info (:46#), plus the board-wide maintenance actions.
using System;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using AstroUniBoard.Core;

namespace AstroUniBoard.DebugApp
{
    internal sealed partial class MainForm
    {
        private TabPage tabStatus;
        private Label lblBigFocuser, lblBigRotator, lblState, lblError;
        private TextBox txtStatus;
        private string resetInfoText = "(not read — press Reset info)";

        private static readonly Tuple<StatusFlags, string>[] FlagTexts =
        {
            Tuple.Create(StatusFlags.FocuserUnverified, "focuser position unverified (power lost while moving, or never saved) — sync it"),
            Tuple.Create(StatusFlags.RotatorUnverified, "rotator angle unverified (not homed since boot) — IMU home or set home"),
            Tuple.Create(StatusFlags.EepromFault, "EEPROM fault (missing or bad CRC, defaults in use)"),
            Tuple.Create(StatusFlags.HousekeepingWarning, "housekeeping warning (see below)"),
            Tuple.Create(StatusFlags.OnboardShtValid, "on-board SHT31 has a valid reading"),
            Tuple.Create(StatusFlags.ExternalShtValid, "external SHT31 (J9) has a valid reading"),
            Tuple.Create(StatusFlags.ImuAnswering, "IMU answering"),
            Tuple.Create(StatusFlags.ImuNotCalibrated, "IMU zero not calibrated — run the IMU calibration"),
            Tuple.Create(StatusFlags.SafeMode, "SAFE MODE: started after a watchdog reset with sensors off (see reset info)"),
            Tuple.Create(StatusFlags.RotatorSuspect, "rotator angle SUSPECT (power lost mid-move, stall, never saved): rotator moves refused until IMU home or set home")
        };

        private void BuildStatusTab()
        {
            TabPage p = tabStatus = AddTab("Status");
            Lbl(p, "Focuser", 16, 12, 200).ForeColor = Color.DimGray;
            lblBigFocuser = Lbl(p, "—", 16, 30, 300, 40);
            lblBigFocuser.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
            Lbl(p, "Rotator (mechanical)", 330, 12, 200).ForeColor = Color.DimGray;
            lblBigRotator = Lbl(p, "—", 330, 30, 300, 40);
            lblBigRotator.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
            lblState = Lbl(p, "", 640, 36, 300, 24);
            lblState.Font = new Font("Segoe UI", 12f, FontStyle.Bold);

            lblError = Lbl(p, "", 16, 78, 700, 22);
            lblError.Font = new Font(Font, FontStyle.Bold);
            Btn(p, "Clear error", 730, 74, 110, async (s, e) => await DoAction(BoardAction.ClearError));

            txtStatus = Mono(p, 16, 106, 800, 470);
            txtStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right;

            int bx = 826, by = 106;
            Btn(p, "Reset info (:46#)", bx, by, 124, async (s, e) => await ReadResetInfo()); by += 34;
            Btn(p, "Save settings (:23#)", bx, by, 124, async (s, e) =>
            {
                if (Confirm("Write all running settings of both axes to the EEPROM (:23#)?")) await DoAction(BoardAction.SaveSettings);
            }); by += 34;
            Btn(p, "Reboot (:22#)", bx, by, 124, async (s, e) =>
            {
                if (Confirm("Reboot the board? The connection comes back by itself.") && await EnsureIdle("reboot")) await DoAction(BoardAction.Reboot);
            });
            foreach (Control c in p.Controls) if (c is Button && c.Left == bx) c.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        }

        private async System.Threading.Tasks.Task ReadResetInfo()
        {
            string r = await Send(Commands.ResetInfo, "reset info");
            ResetInfo info;
            if (ResetInfo.TryParse(r, out info))
            {
                var sb = new StringBuilder();
                int f = info.ResetFlags;
                if ((f & 0x80) != 0) sb.Append("low-power ");
                if ((f & 0x40) != 0) sb.Append("WINDOW-WATCHDOG ");
                if ((f & 0x20) != 0) sb.Append("INDEPENDENT-WATCHDOG ");
                if ((f & 0x10) != 0) sb.Append("software ");
                if ((f & 0x08) != 0) sb.Append("power-on/brown-out ");
                if ((f & 0x04) != 0) sb.Append("reset-pin ");
                if ((f & 0x02) != 0) sb.Append("option-byte-load ");
                resetInfoText = "flags 0x" + f.ToString("X2") + " (" + sb.ToString().Trim() + "), last marker " + info.LastMarker +
                                ", safe mode " + (info.SafeMode ? "YES" : "no") + (info.WatchdogReset ? "  ← the last reset was a WATCHDOG reset" : "");
            }
            else resetInfoText = Replies.Explain(r);
            UpdateStatusTab(view);
        }

        private void UpdateStatusTab(BoardView v)
        {
            StatusView st = v.Status;
            if (st == null || !v.IsConnected)
            {
                lblBigFocuser.Text = "—";
                lblBigRotator.Text = "—";
                lblState.Text = v.IsConnected ? "" : "not connected";
                lblError.Text = "";
                txtStatus.Text = session == null ? "Not connected. Choose a connection above and press Connect." : v.Connection + (v.Problem != null ? ": " + v.Problem : "");
                return;
            }
            lblBigFocuser.Text = I(st.FocuserPosition) + (st.FocuserTarget >= 0 ? " → " + I(st.FocuserTarget) : "");
            lblBigRotator.Text = Deg(st.RotatorAngle) + (st.RotatorTarget >= 0 ? " → " + Deg(st.RotatorTarget) : "");
            lblState.Text = StateName(st.State);
            lblState.ForeColor = st.Busy ? Color.DarkOrange : Color.DarkGreen;
            lblError.Text = st.Error == 0 ? "No error" : "Error " + st.Error + ": " + BoardErrors.Describe(st.Error) + (BoardErrors.IsFault(st.Error) ? " (sticky fault)" : "");
            lblError.ForeColor = st.Error == 0 ? Color.DarkGreen : Color.Firebrick;

            var sb = new StringBuilder();
            IdentityView id = v.Identity;
            if (id != null)
            {
                sb.AppendLine("Board      " + id.Model + "  firmware " + id.Firmware + "  protocol " + id.Protocol + "  serial " + id.Serial);
                sb.AppendLine("Caps       0x" + id.Caps.ToString("X") + " = " + ((BoardCaps)id.Caps));
            }
            sb.AppendLine("Port       " + v.Port + "   connection " + v.Connection + (v.Problem != null ? "   problem: " + v.Problem : ""));
            sb.AppendLine();
            sb.AppendLine("Status     state " + st.State + " (" + StateName(st.State) + ")");
            sb.AppendLine("  focuser  position " + st.FocuserPosition + "  target " + (st.FocuserTarget >= 0 ? I(st.FocuserTarget) : "-"));
            sb.AppendLine("  rotator  angle " + Deg(st.RotatorAngle) + " (" + st.RotatorAngle + " cdeg)  target " + (st.RotatorTarget >= 0 ? Deg(st.RotatorTarget) : "-"));
            sb.AppendLine("  error    " + st.Error + "  " + BoardErrors.Describe(st.Error));
            sb.AppendLine("  flags    0x" + st.Flags.ToString("X") + ":");
            foreach (var f in FlagTexts)
                sb.AppendLine("    [" + (st.Has(f.Item1) ? "x" : " ") + "] " + f.Item2);
            sb.AppendLine();
            HousekeepingView hk = v.Housekeeping;
            if (hk != null)
            {
                sb.AppendLine("Housekeeping (:40#)");
                sb.AppendLine("  MCU " + hk.McuTemperature.ToString("0.0", CultureInfo.InvariantCulture) + " °C (die, reads above ambient)   VDDA " +
                              hk.Vdda.ToString("0.000", CultureInfo.InvariantCulture) + " V");
                sb.AppendLine("  TMC2209 temperature class: worst " + DriverTemp(hk.DriverTemperatureClass) + ", focuser " + DriverTemp(hk.FocuserDriverTemperatureClass) +
                              ", rotator " + DriverTemp(hk.RotatorDriverTemperatureClass));
                sb.AppendLine("  warnings 0x" + hk.Warnings.ToString("X") + ": " + Housekeeping.Describe((HousekeepingWarnings)hk.Warnings));
            }
            else sb.AppendLine("Housekeeping: not read yet");
            EnvironmentView env = v.Environment;
            sb.AppendLine("Environment " + (env == null ? "no valid reading" :
                (env.Source == 1 ? "on-board" : "external") + " " + env.Temperature.ToString("0.0", CultureInfo.InvariantCulture) + " °C  " +
                env.Humidity.ToString("0.0", CultureInfo.InvariantCulture) + " %RH  dew point " + env.DewPoint.ToString("0.0", CultureInfo.InvariantCulture) + " °C"));
            sb.AppendLine("Reset info " + resetInfoText);
            string text = sb.ToString();
            if (txtStatus.Text != text) txtStatus.Text = text;
        }

        private static string DriverTemp(int? c)
        {
            if (!c.HasValue) return "not answering";
            return c.Value == 0 ? "<120 °C" : "≥" + c.Value + " °C";
        }
    }
}
