// Environment tab: the reading the drivers use (:90#), both SHT31 sensors (:91#), and the sensor
// selection (:92# / :93).
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
        private TabPage tabEnv;
        private Label lblEnvMain;
        private TextBox txtEnvDetail;
        private ComboBox cmbEnvSource;

        private void BuildEnvironmentTab()
        {
            TabPage p = tabEnv = AddTab("Environment");
            Lbl(p, "Reading used by the drivers (ObservingConditions, temperature compensation), :90#:", 16, 10, 700).ForeColor = Color.DimGray;
            lblEnvMain = Lbl(p, "—", 16, 30, 900, 34);
            lblEnvMain.Font = new Font("Segoe UI", 16f, FontStyle.Bold);

            Lbl(p, "Both sensors (:91#, refreshed every 3 s on this tab; the firmware reads them every 2 s):", 16, 76, 700).ForeColor = Color.DimGray;
            txtEnvDetail = Mono(p, 16, 96, 760, 110);
            Btn(p, "Read now (:91#)", 790, 96, 150, async (s, e) => ShowEnvDetail(await Send(Commands.EnvironmentDetail, "environment detail")));

            int y = 222;
            Lbl(p, "Sensor selection:", 16, y + 4, 110);
            cmbEnvSource = new ComboBox { Left = 130, Top = y, Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbEnvSource.Items.AddRange(new object[] { "0 automatic (external J9 if valid, else on-board)", "1 on-board (sits next to the drivers, reads warm)", "2 external (J9)" });
            p.Controls.Add(cmbEnvSource);
            Btn(p, "Read (:92#)", 470, y - 1, 100, async (s, e) =>
            {
                ShowEnvSource(await Send(Commands.GetEnvironmentSource, "sensor selection"));
            });
            Btn(p, "Set (:93)", 576, y - 1, 100, async (s, e) =>
            {
                if (cmbEnvSource.SelectedIndex < 0) return;
                await Send(Commands.SetEnvironmentSource(cmbEnvSource.SelectedIndex), "sensor selection");
            });
            Lbl(p, "(saved with :23#)", 684, y + 4, 120).ForeColor = Color.DimGray;
        }

        private void ShowEnvSource(string reply)
        {
            int n;
            if (int.TryParse(reply, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n >= 0 && n <= 2) cmbEnvSource.SelectedIndex = n;
        }

        private void UpdateEnvironmentTab(BoardView v)
        {
            EnvironmentView e = v.Environment;
            if (!v.IsConnected) { lblEnvMain.Text = "—"; return; }
            lblEnvMain.Text = e == null
                ? "no valid reading (ERR,14: no SHT31 answering)"
                : (e.Source == 1 ? "on-board  " : "external  ") + e.Temperature.ToString("0.0", CultureInfo.InvariantCulture) + " °C    " +
                  e.Humidity.ToString("0.0", CultureInfo.InvariantCulture) + " %RH    dew point " + e.DewPoint.ToString("0.0", CultureInfo.InvariantCulture) + " °C";
        }

        private static string ShtStatus(string st)
        {
            switch (st)
            {
                case "0": return "valid";
                case "1": return "no answer (absent, unpowered or bus fault)";
                case "2": return "CRC error";
                case "3": return "no measurement yet since boot";
                default: return "status " + st;
            }
        }

        private void ShowEnvDetail(string reply)
        {
            string[] f = reply.Split(',');
            if (f.Length != 10 || f[0] != "V") { txtEnvDetail.Text = Replies.Explain(reply); return; }
            var sb = new StringBuilder();
            sb.AppendLine("sensor     status                                     temp °C   RH %   dew °C");
            for (int i = 0; i < 2; i++)
            {
                int o = 1 + i * 4;
                sb.AppendLine((i == 0 ? "on-board   " : "external   ") + ShtStatus(f[o]).PadRight(43) + Tenth(f[o + 1]).PadLeft(7) + Tenth(f[o + 2]).PadLeft(7) + Tenth(f[o + 3]).PadLeft(9));
            }
            sb.AppendLine();
            sb.AppendLine("values are the last good reading; on-board sensor supply restarts: " + f[9]);
            txtEnvDetail.Text = sb.ToString();
        }

        private static string Tenth(string v)
        {
            int n;
            return int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) ? (n / 10.0).ToString("0.0", CultureInfo.InvariantCulture) : v;
        }
    }
}
