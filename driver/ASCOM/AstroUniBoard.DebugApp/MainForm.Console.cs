// Raw console (history with Up/Down, ERR codes explained, status lines decoded) and the log view
// (driver log + what this app sent), with export to a text file.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using AstroUniBoard.Core;

namespace AstroUniBoard.DebugApp
{
    internal sealed partial class MainForm
    {
        private TabPage tabConsole, tabLog;
        private TextBox txtConsole, txtCommand, txtLog;
        private CheckBox chkLogApp;
        private readonly List<string> history = new List<string>();
        private int historyPos;
        private readonly StringBuilder logAll = new StringBuilder();

        private void BuildConsoleTab()
        {
            TabPage p = tabConsole = AddTab("Console");
            txtConsole = Mono(p, 10, 10, 935, 470);
            txtConsole.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            int y = 490;
            txtCommand = new TextBox { Left = 10, Top = y + 1, Width = 300, Font = new Font(FontFamily.GenericMonospace, 10f), Tag = "needs-board" };
            txtCommand.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            p.Controls.Add(txtCommand);
            var send = Btn(p, "Send", 316, y, 70, async (s, e) => await SendConsole());
            send.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            int x = 400;
            foreach (string c in new[] { ":32#", ":33#", ":34#", ":40#", ":46#", ":47,F#", ":41,F#", ":70#", ":91#", ":94#", ":96#" })
            {
                string cmd = c;
                var b = Btn(p, c, x, y, 48, async (s, e) => { txtCommand.Text = cmd; await SendConsole(); });
                b.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
                b.Font = new Font(FontFamily.GenericMonospace, 7.5f);
                x += 50;
            }
            var hint = Lbl(p, "Type a command like :33# or 33 (colon and # are added). Up/Down = history. Every command any tab sends is echoed here.", 10, y + 32, 900);
            hint.ForeColor = Color.DimGray;
            hint.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            txtCommand.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SendConsole(); }
                else if (e.KeyCode == Keys.Up && history.Count > 0) { historyPos = Math.Max(0, historyPos - 1); txtCommand.Text = history[historyPos]; txtCommand.SelectionStart = txtCommand.Text.Length; e.Handled = true; }
                else if (e.KeyCode == Keys.Down && history.Count > 0)
                {
                    historyPos = Math.Min(history.Count, historyPos + 1);
                    txtCommand.Text = historyPos < history.Count ? history[historyPos] : "";
                    txtCommand.SelectionStart = txtCommand.Text.Length;
                    e.Handled = true;
                }
            };
        }

        /// <summary>":33#", "33", ":33", "33#" all become ":33#".</summary>
        internal static string NormalizeCommand(string text)
        {
            string c = (text ?? "").Trim();
            if (c.Length == 0) return null;
            if (c[0] != ':') c = ":" + c;
            if (c[c.Length - 1] != '#') c += "#";
            return c;
        }

        private async System.Threading.Tasks.Task SendConsole()
        {
            string c = NormalizeCommand(txtCommand.Text);
            if (c == null) return;
            if ((c == Commands.Reboot || c == Commands.EnterDfu) && !Confirm(c + " resets the board" + (c == Commands.EnterDfu ? " into the DFU bootloader (use the Firmware tab to flash)" : "") + ". Send it?")) return;
            if (history.Count == 0 || history[history.Count - 1] != c) history.Add(c);
            historyPos = history.Count;
            txtCommand.Clear();
            await Send(c);
        }

        private void ConsoleEcho(string command, string reply)
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("  > ").Append(command).Append("\r\n");
            sb.Append("              < ").Append(Replies.Explain(reply)).Append("\r\n");
            string decoded = Decode(reply);
            if (decoded != null) sb.Append("                ").Append(decoded).Append("\r\n");
            txtConsole.AppendText(sb.ToString());
        }

        /// <summary>A readable form of the structured replies, or null.</summary>
        internal static string Decode(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return null;
            BoardStatus st; Housekeeping hk; StallGuardInfo sg; DriverConfig dc; ImuReading imu; EnvironmentReading env; BoardIdentity id;
            if (BoardStatus.TryParse(reply, out st)) return st + (st.Error != 0 ? " (" + BoardErrors.Describe(st.Error) + ")" : "");
            if (Housekeeping.TryParse(reply, out hk)) return hk + " (" + Housekeeping.Describe(hk.Warnings) + ")";
            if (StallGuardInfo.TryParse(reply, out sg)) return "threshold " + sg.Threshold + ", last/min/max " + N(sg.Last) + "/" + N(sg.Min) + "/" + N(sg.Max) +
                                                              (sg.SuggestedThreshold.HasValue ? ", suggested " + sg.SuggestedThreshold : "");
            if (DriverConfig.TryParse(reply, out dc)) return dc.RunCurrentMa + "/" + dc.HoldCurrentMa + " mA, " + dc.Microsteps + " usteps, " + (dc.Mode == 0 ? "StealthChop" : "SpreadCycle") + ", off after " + dc.IdleOffSeconds + " s";
            if (ImuReading.TryParse(reply, out imu)) return "roll " + (imu.RollCdeg.HasValue ? Deg(imu.RollCdeg.Value) : "undefined") + ", tilt " + (imu.TiltCdeg.HasValue ? Deg(imu.TiltCdeg.Value) : "undefined") + ", a = " + imu.Ax + "/" + imu.Ay + "/" + imu.Az + " mg";
            if (EnvironmentReading.TryParse(reply, out env)) return env.ToString();
            if (BoardIdentity.TryParse(reply, out id)) return id.ToString();
            if (reply.StartsWith("E,", StringComparison.Ordinal))
            {
                int code;
                if (int.TryParse(reply.Substring(2), NumberStyles.None, CultureInfo.InvariantCulture, out code)) return "sticky error: " + BoardErrors.Describe(code);
            }
            return null;
        }

        // ------------------------------------------------------------------ log

        private void BuildLogTab()
        {
            TabPage p = tabLog = AddTab("Log");
            txtLog = Mono(p, 10, 10, 935, 495);
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            chkLogApp = new CheckBox { Text = "Show what this app sent (\"app\" lines)", Left = 10, Top = 515, Width = 280, Checked = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
            p.Controls.Add(chkLogApp);
            Btn(p, "Export to a text file…", 300, 512, 160, (s, e) => ExportLog(), false).Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            Btn(p, "Clear view", 470, 512, 100, (s, e) => { txtLog.Clear(); }, false).Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            var hint = Lbl(p, "Driver log: the ASCOM driver's (or, direct, this app's own controller's) connection and warning messages. Export keeps everything since start.", 580, 516, 370, 36);
            hint.ForeColor = Color.DimGray;
            hint.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        }

        private void AppLog(string text) { AddLogLine(DateTime.UtcNow, "app", text); }

        private void DriverLog(LogEntry e) { AddLogLine(e.Time, session != null && session.IsDirect ? "core" : "driver", e.Text); }

        private void AddLogLine(DateTime utc, string source, string text)
        {
            string line = utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + source.PadRight(6) + " " + text + "\r\n";
            logAll.Append(line);
            if (source != "app" || chkLogApp.Checked)
            {
                if (txtLog.TextLength > 400000) txtLog.Text = txtLog.Text.Substring(200000);
                txtLog.AppendText(line);
            }
        }

        private void ExportLog()
        {
            using (var dlg = new SaveFileDialog
            {
                Filter = "Text file (*.txt)|*.txt|All files|*.*",
                FileName = "AstroUniBoard-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var head = new StringBuilder();
                head.AppendLine("AstroUniBoard debug app log, exported " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                head.AppendLine("Connection: " + (session != null ? session.Description : "none"));
                if (view.Identity != null) head.AppendLine("Board: " + view.Identity.Model + " " + view.Identity.Firmware + " protocol " + view.Identity.Protocol + " serial " + view.Identity.Serial);
                head.AppendLine();
                File.WriteAllText(dlg.FileName, head.ToString() + logAll + "\r\n--- console ---\r\n" + txtConsole.Text, Encoding.UTF8);
                stripText.Text = "Log written to " + dlg.FileName;
            }
        }
    }
}
