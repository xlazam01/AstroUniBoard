// Firmware tab: update over USB DFU (:31# then dfu-util), the same flow as
// fw-onstepx/tools/firmware_manager enter_dfu_and_upload. "Only enter DFU" hands the board to any
// other tool (firmware_manager upload_firmware, STM32CubeProgrammer, dfu-util by hand).
using System;
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
            txtDfuUtil = new TextBox { Left = 160, Top = y, Width = 640, Text = DfuUpdater.FindDfuUtil() ?? "" };
            p.Controls.Add(txtDfuUtil);
            Btn(p, "Browse…", 810, y - 1, 90, (s, e) => Browse(txtDfuUtil, "dfu-util (dfu-util.exe)|dfu-util*.exe|All files|*.*"), false);
            y += 34;
            btnFwUpdate = Btn(p, "Update firmware", 160, y, 150, async (s, e) => await UpdateFirmware(), false);
            btnFwUpdate.Font = new Font(Font, FontStyle.Bold);
            btnFwDfuOnly = Btn(p, "Only enter DFU (:31#)", 320, y, 160, async (s, e) =>
            {
                if (!Confirm("Put the board into the STM32 DFU bootloader now? It disappears from the COM port until it is flashed (or power-cycled)."))
                    return;
                string r = await DoAction(BoardAction.EnterDfu);
                FwOut(":31# → " + r + (Replies.IsOk(r) ? "  — the board is now 0483:DF11; flash it with dfu-util, firmware_manager upload_firmware or STM32CubeProgrammer" : ""));
            }, false);
            y += 36;
            var help = Lbl(p,
                "Update = check the image (≤ 128 KB), make sure no other board is in DFU mode, send :31# (the board jumps to the STM32 bootloader 0483:DF11), " +
                "run dfu-util -d 0483:df11 -a 0 -s 0x08000000:leave -D <image>, then reconnect to the same board and compare the firmware version. " +
                "dfu-util is found next to this app, on PATH or in PlatformIO (~/.platformio/packages/tool-dfuutil). On Windows the bootloader needs the WinUSB driver once (Zadig). " +
                "Through the ASCOM driver, N.I.N.A. loses the focuser and rotator for about 15 s: do not update while imaging. " +
                "Same flow as fw-onstepx/tools/firmware_manager (enter_dfu_and_upload).",
                16, y, 930, 66);
            help.ForeColor = Color.DimGray;
            y += 70;
            txtFwOut = Mono(p, 16, y, 930, 560 - y);
            txtFwOut.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        }

        private void Browse(TextBox target, string filter)
        {
            using (var dlg = new OpenFileDialog { Filter = filter })
            {
                try { if (File.Exists(target.Text)) dlg.InitialDirectory = Path.GetDirectoryName(target.Text); } catch (ArgumentException) { }
                if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.FileName;
            }
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
            string problem = DfuUpdater.CheckImage(image);
            if (problem != null) { MessageBox.Show(this, problem, "AstroUniBoard"); return; }
            if (!File.Exists(txtDfuUtil.Text.Trim())) { MessageBox.Show(this, "dfu-util.exe not found — choose it with Browse.", "AstroUniBoard"); return; }
            BoardView before = view;
            if (before.Status != null && before.Status.Busy) { MessageBox.Show(this, "The board is busy (moving). Wait until it is idle.", "AstroUniBoard"); return; }
            string oldFw = before.Identity != null ? before.Identity.Firmware : "?";
            string serial = before.Identity != null ? before.Identity.Serial : null;
            if (!Confirm("Flash " + Path.GetFileName(image) + " (" + new FileInfo(image).Length + " bytes) to the board " + serial + " running " + oldFw + "?\n\n" +
                         "Do not unplug the board while it is flashed.")) return;

            var dfu = new DfuUpdater { DfuUtilPath = txtDfuUtil.Text.Trim() };
            fwRunning = true;
            pausePoll = true;
            UpdateFirmwareTab(view);
            txtFwOut.Clear();
            try
            {
                bool ok = await Task.Run(() => dfu.Update(s, image, FwOut));
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
