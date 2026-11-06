// Firmware tab: update over USB DFU (:31# then dfu-util), the same flow as
// fw-onstepx/tools/firmware_manager enter_dfu_and_upload. "Only enter DFU" hands the board to any
// other tool (firmware_manager upload_firmware, STM32CubeProgrammer, dfu-util by hand).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AstroUniBoard.DebugApp
{
    internal sealed partial class MainForm
    {
        private TabPage tabFirmware;
        private Label lblFwCurrent;
        private TextBox txtFwImage, txtDfuUtil, txtFwOut;
        private Button btnFwUpdate, btnFwDfuOnly;
        private bool fwRunning;
        /// <summary>dfu-util.exe copies the user picked or agreed to use (a copy next to the app needs no OK).</summary>
        private readonly HashSet<string> trustedDfuUtil = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DfuUtilLocation foundDfuUtil;

        private void BuildFirmwareTab()
        {
            TabPage p = tabFirmware = AddTab("Firmware");
            lblFwCurrent = Lbl(p, "", 16, 12, 900, 22);
            lblFwCurrent.Font = new Font(Font, FontStyle.Bold);
            int y = 44;
            Lbl(p, "Firmware image (.bin):", 16, y + 4, 140);
            txtFwImage = new TextBox { Left = 160, Top = y, Width = 640 };
            p.Controls.Add(txtFwImage);
            Btn(p, "Browse…", 810, y - 1, 90, (s, e) => Browse(txtFwImage, "Firmware (*.bin)|*.bin|All files|*.*"), false);
            y += 32;
            Lbl(p, "dfu-util.exe:", 16, y + 4, 140);
            foundDfuUtil = DfuUpdater.FindDfuUtil();
            txtDfuUtil = new TextBox { Left = 160, Top = y, Width = 640, Text = foundDfuUtil != null ? foundDfuUtil.Path : "" };
            p.Controls.Add(txtDfuUtil);
            Btn(p, "Browse…", 810, y - 1, 90, (s, e) =>
            {
                if (Browse(txtDfuUtil, "dfu-util (dfu-util.exe)|dfu-util*.exe|All files|*.*")) trustedDfuUtil.Add(Path.GetFullPath(txtDfuUtil.Text));
            }, false);
            y += 34;
            btnFwUpdate = Btn(p, "Update firmware", 160, y, 150, async (s, e) => await UpdateFirmware(), false);
            btnFwUpdate.Font = new Font(Font, FontStyle.Bold);
            btnFwDfuOnly = Btn(p, "Only enter DFU (:31#)", 320, y, 160, async (s, e) =>
            {
                if (!Confirm("Put the board into the STM32 DFU bootloader now? It disappears from the COM port until it is flashed (or power-cycled)."))
                    return;
                if (!await EnsureIdle("enter DFU")) return;
                string r = await DoAction(BoardAction.EnterDfu);
                FwOut(":31# → " + r + (Replies.IsOk(r) ? "  — the board is now 0483:DF11; flash it with dfu-util, firmware_manager upload_firmware or STM32CubeProgrammer" : ""));
            }, false);
            y += 36;
            var help = Lbl(p,
                "Update = check the image (≤ 128 KB, vector table, ASTROUNIBOARD-IMG marker = this board's model), halt a moving axis, make sure no other board is in DFU mode, " +
                "send :31# (the board jumps to the STM32 bootloader 0483:DF11), " +
                "run dfu-util -d 0483:df11 -a 0 -s 0x08000000:leave -D <image>, then reconnect to the same board and compare the firmware version. " +
                "dfu-util is the copy installed next to this app; one found on PATH or in PlatformIO is used only after you agree. On Windows the bootloader needs the WinUSB driver once (Zadig). " +
                "Through the ASCOM driver, N.I.N.A. loses the focuser and rotator for about 15 s: do not update while imaging. " +
                "Same flow as fw-onstepx/tools/firmware_manager (enter_dfu_and_upload).",
                16, y, 930, 66);
            help.ForeColor = Color.DimGray;
            y += 70;
            txtFwOut = Mono(p, 16, y, 930, 560 - y);
            txtFwOut.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        }

        private bool Browse(TextBox target, string filter)
        {
            using (var dlg = new OpenFileDialog { Filter = filter })
            {
                try { if (File.Exists(target.Text)) dlg.InitialDirectory = Path.GetDirectoryName(target.Text); } catch (ArgumentException) { }
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                target.Text = dlg.FileName;
                return true;
            }
        }

        /// <summary>
        /// dfu-util runs with the user's rights and gets the board: a copy on PATH or in PlatformIO could
        /// have been planted (#79), so anything but the one next to the app needs the user's OK once.
        /// </summary>
        private bool TrustDfuUtil(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { full = path; }
            if (DfuUpdater.IsNextToApp(full) || trustedDfuUtil.Contains(full)) return true;
            string where = foundDfuUtil != null && string.Equals(foundDfuUtil.Path, full, StringComparison.OrdinalIgnoreCase) ? foundDfuUtil.SourceText : "entered by hand";
            bool ok = MessageBox.Show(this,
                "dfu-util.exe is not the copy installed next to this app:\n\n" + full + "\n(" + where + ")\n\n" +
                "It runs with your rights and writes the board's flash. Use it only if you know where it comes from. Use this dfu-util?",
                "AstroUniBoard", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            FwOut("dfu-util " + full + " (" + where + "): " + (ok ? "accepted by the user" : "refused by the user"));
            if (ok) trustedDfuUtil.Add(full);
            return ok;
        }

        /// <summary>Shows the image check; true when the image may be flashed, <paramref name="accepted"/> when a warning was accepted.</summary>
        private bool AcceptImage(ImageCheck check, out bool accepted)
        {
            accepted = false;
            switch (check.Verdict)
            {
                case ImageVerdict.Ok:
                    return true;
                case ImageVerdict.MissingMarker:
                    accepted = MessageBox.Show(this, check.Message + "\n\nFlash it anyway? Only if you are sure it is AstroUniBoard firmware for this board.",
                        "AstroUniBoard — image not checked", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    break;
                case ImageVerdict.ModelMismatch:
                    if (MessageBox.Show(this, check.Message + "\n\nNot flashed. A firmware for another board can leave this one without USB, and it may not be recoverable " +
                            "without the BOOT0 button or SWD.\n\nForce flashing it anyway?",
                            "AstroUniBoard — wrong board model", MessageBoxButtons.YesNo, MessageBoxIcon.Error, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                        accepted = Confirm("Really flash a " + check.MarkerModel + " image to this board (force)?");
                    break;
                default:
                    MessageBox.Show(this, check.Message, "AstroUniBoard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
            }
            FwOut("image check: " + check.Message + " — " + (accepted ? "flashing anyway (user's choice)" : "not flashed"));
            return accepted;
        }

        private void UpdateFirmwareTab(BoardView v)
        {
            lblFwCurrent.Text = v.Identity != null && v.IsConnected
                ? "Running: " + v.Identity.Model + " " + v.Identity.Firmware + " (protocol " + v.Identity.Protocol + "), serial " + v.Identity.Serial
                : "Running firmware: unknown (not connected)";
            bool up = session != null && v.IsConnected && !fwRunning;
            btnFwUpdate.Enabled = up;
            btnFwDfuOnly.Enabled = up;
        }

        private void FwOut(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(FwOut), line); return; }
            txtFwOut.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + "\r\n");
            AppLog("firmware: " + line);
        }

        private async Task UpdateFirmware()
        {
            IBoardSession s = session;
            if (s == null) return;
            string image = txtFwImage.Text.Trim();
            string dfuUtil = txtDfuUtil.Text.Trim();
            BoardView before = view;
            ImageCheck check = DfuUpdater.CheckImage(image, before.Identity != null ? before.Identity.Model : null);
            bool accepted;
            if (!AcceptImage(check, out accepted)) return;
            if (!File.Exists(dfuUtil)) { MessageBox.Show(this, "dfu-util.exe not found — choose it with Browse.", "AstroUniBoard"); return; }
            if (!TrustDfuUtil(dfuUtil)) return;
            if (!await EnsureIdle("update the firmware")) return;
            before = view;
            string oldFw = before.Identity != null ? before.Identity.Firmware : "?";
            string serial = before.Identity != null ? before.Identity.Serial : null;
            if (!Confirm("Flash " + Path.GetFileName(image) + " (" + new FileInfo(image).Length + " bytes) to the board " + serial + " running " + oldFw + "?\n\n" +
                         "Do not unplug the board while it is flashed.")) return;

            var dfu = new DfuUpdater { DfuUtilPath = Path.GetFullPath(dfuUtil) };
            fwRunning = true;
            pausePoll = true;
            UpdateFirmwareTab(view);
            txtFwOut.Clear();
            try
            {
                bool ok = await Task.Run(() => dfu.Update(s, image, FwOut, accepted));
                FwOut(ok ? "update finished" : "update did not finish — see above");
            }
            finally
            {
                fwRunning = false;
                pausePoll = false;
                await PollAsync();
            }
        }
    }
}
