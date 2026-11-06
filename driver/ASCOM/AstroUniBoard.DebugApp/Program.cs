// AstroUniBoard control & debug app (#45).
//   AstroUniBoard.DebugApp.exe [--port COM9] [--connect] [--loopback] [--screenshots <folder>]
//   --port         preselect a direct connection to this port
//   --connect      connect at start with the preselected choice
//   --loopback     add the development choice "ASCOM code path over an in-process driver contract"
//   --screenshots  connect, save a PNG of every tab into the folder, exit (for docs and PRs)
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AstroUniBoard.DebugApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string port = null, shots = null;
            bool loopback = false, connect = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--port" && i + 1 < args.Length) port = args[++i];
                else if (a == "--loopback") loopback = true;
                else if (a == "--connect") connect = true;
                else if (a == "--screenshots" && i + 1 < args.Length) { shots = args[++i]; connect = true; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm(loopback, port);
            if (connect) form.Shown += async (s, e) => await form.AutoConnect(shots);
            Application.Run(form);
        }
    }

    internal sealed partial class MainForm
    {
        internal async Task AutoConnect(string screenshotFolder)
        {
            await ConnectAsync();
            if (screenshotFolder == null) return;
            Directory.CreateDirectory(screenshotFolder);
            await Task.Delay(2500);
            foreach (TabPage p in tabs.TabPages)
            {
                tabs.SelectedTab = p;
                if (p == tabMotors) { await ReadMotor(motorFoc); await ReadMotor(motorRot); await ReadStall(motorFoc); await ReadStall(motorRot); }
                if (p == tabImu) await ReadImuCal();
                if (p == tabStatus) await ReadResetInfo();
                if (p == tabConsole) { foreach (string c in new[] { ":32#", ":33#", ":40#", ":52,99999999#", ":94#" }) { txtCommand.Text = c; await SendConsole(); } }
                await Task.Delay(1500);
                await PollAsync();
                using (var bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    string name = p.Text.Split(' ')[0].ToLowerInvariant();
                    bmp.Save(Path.Combine(screenshotFolder, (tabs.TabPages.IndexOf(p) + 1).ToString("00") + "-" + name + ".png"), ImageFormat.Png);
                }
            }
            Close();
        }
    }
}
