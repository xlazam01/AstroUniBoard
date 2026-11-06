// Runs only with a real board: set ASTROUNIBOARD_PORT=COM9 (or "auto"). Small moves only:
// focuser +50 steps and back, rotator +1° and back. Without the variable the tests return at once.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using AstroUniBoard.Core;
using Xunit;
using Xunit.Abstractions;

namespace AstroUniBoard.DebugApp.Tests
{
    public class HardwareTests
    {
        private readonly ITestOutputHelper output;
        public HardwareTests(ITestOutputHelper output) { this.output = output; }

        private static string Port
        {
            get
            {
                string p = Environment.GetEnvironmentVariable("ASTROUNIBOARD_PORT");
                return string.IsNullOrEmpty(p) ? null : p;
            }
        }

        [Fact]
        public void DirectPathOnRealBoard()
        {
            if (Port == null) return;
            using (var s = new DirectSession(Port == "auto" ? null : Port)) Exercise(s);
        }

        [Fact]
        public void AscomCodePathOverLoopbackOnRealBoard()
        {
            if (Port == null) return;
            using (var s = new AscomSession(() => new LoopbackDriver(new DirectSession(Port == "auto" ? null : Port)))) Exercise(s);
        }

        /// <summary>
        /// ASTROUNIBOARD_ASCOM=1: the installed LocalServer, read only (no moves), alongside whatever
        /// other client is connected. Closing the app must not drop that client.
        /// </summary>
        [Fact]
        public void InstalledDriverReadOnly()
        {
            if (Environment.GetEnvironmentVariable("ASTROUNIBOARD_ASCOM") != "1") return;
            int before = Clients();
            using (var s = new AscomSession())
            {
                s.Open();
                output.WriteLine(s.Description + ", clients before " + before);
                BoardView v = s.Snapshot();
                Assert.True(v.IsConnected);
                Assert.NotNull(v.Focuser);
                Assert.Equal(before + 1, Clients());
                foreach (string c in new[] { Commands.Identify, Commands.Status, Commands.Housekeeping, Commands.Imu, Commands.EnvironmentDetail, Commands.GetStallGuard(Axis.Focuser), ":70#" })
                {
                    string r = s.Raw(c);
                    output.WriteLine(c + " → " + Replies.Explain(r) + "   " + MainForm.Decode(r));
                    Assert.False(Replies.IsFailure(r), c + " → " + r);
                }
                Assert.Equal("ERR,7", s.Raw(Commands.FocuserMoveBy(99999999)));
                Assert.Equal("ERR,10", s.Raw(":99#"));
                IList<LogEntry> log = s.ReadLog(0);
                Assert.NotEmpty(log);
                foreach (LogEntry e in log.Skip(Math.Max(0, log.Count - 5))) output.WriteLine("log " + e.Seq + " " + e.Time.ToLocalTime().ToString("HH:mm:ss") + " " + e.Text);
                Assert.True(Math.Abs((DateTime.UtcNow - log.Last().Time).TotalHours) < 0.9, "log time looks shifted: " + log.Last().Time);
            }
            Assert.Equal(before, Clients());
        }

        /// <summary>
        /// ASTROUNIBOARD_ASCOM=moves: the installed LocalServer with no other client. Small moves, a
        /// halted focuser move and IMU home (needs a valid IMU calibration and the scope off the zenith).
        /// </summary>
        [Fact]
        public void InstalledDriverMoves()
        {
            if (Environment.GetEnvironmentVariable("ASTROUNIBOARD_ASCOM") != "moves") return;
            using (var s = new AscomSession())
            {
                Exercise(s);

                BoardView v = s.Snapshot();
                int foc = v.Status.FocuserPosition;
                int target = foc >= v.Focuser.MaxPosition / 2 ? 0 : v.Focuser.MaxPosition;
                Assert.Equal("OK", s.Raw(Commands.FocuserMoveTo(target)));
                Thread.Sleep(700);
                Assert.Equal((int)BoardState.FocuserMoving, s.Snapshot().Status.State);
                Assert.Equal("OK", s.Do(BoardAction.HaltAll));
                v = WaitIdle(s);
                output.WriteLine("halted at " + v.Status.FocuserPosition + " (from " + foc + " towards " + target + ")");
                Assert.NotEqual(target, v.Status.FocuserPosition);
                Assert.NotEqual(foc, v.Status.FocuserPosition);
                Assert.Equal("OK", s.Raw(Commands.FocuserMoveTo(foc)));
                Assert.Equal(foc, WaitIdle(s).Status.FocuserPosition);

                Assert.Equal("OK", s.Do(BoardAction.ImuHome));
                v = WaitIdle(s);
                output.WriteLine("IMU home: rotator " + v.Status.RotatorAngle + " cdeg, error " + v.Status.Error + ", flags 0x" + v.Status.Flags.ToString("X") + ", " + s.Raw(Commands.Imu));
                Assert.Equal(0, v.Status.Error);
                Assert.False(v.Status.Has(StatusFlags.RotatorUnverified));
                foreach (LogEntry e in s.ReadLog(0).Skip(Math.Max(0, s.ReadLog(0).Count - 8))) output.WriteLine("log " + e.Text);
            }
        }

        private static int Clients()
        {
            {
                // Snapshot works without Connected; read host.clients without connecting this instance
                var t = Type.GetTypeFromProgID(AscomSession.FocuserProgId);
                object d = Activator.CreateInstance(t);
                try
                {
                    string json = (string)t.InvokeMember("Action", System.Reflection.BindingFlags.InvokeMethod, null, d, new object[] { AscomSession.ActionSnapshot, "" });
                    var o = (IDictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json);
                    return J.Int(J.Obj(o, "host"), "clients");
                }
                finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(d); }
            }
        }

        /// <summary>Also needs ASTROUNIBOARD_DFU_IMAGE (a .bin built for this board) and dfu-util. Flashes it. ASTROUNIBOARD_DFU_FORCE=1 accepts an image without marker.</summary>
        [Fact]
        public void FirmwareUpdateOverLoopbackOnRealBoard()
        {
            string image = Environment.GetEnvironmentVariable("ASTROUNIBOARD_DFU_IMAGE");
            if (Port == null || string.IsNullOrEmpty(image)) return;
            DfuUtilLocation found = DfuUpdater.FindDfuUtil();
            var dfu = new DfuUpdater { DfuUtilPath = found != null ? found.Path : null };
            Assert.NotNull(dfu.DfuUtilPath);
            using (var s = new AscomSession(() => new LoopbackDriver(new DirectSession(Port == "auto" ? null : Port))))
            {
                s.Open();
                // ASTROUNIBOARD_DFU_FORCE=1 accepts an image without the marker (built before #79)
                Assert.True(dfu.Update(s, image, line => output.WriteLine(line), Environment.GetEnvironmentVariable("ASTROUNIBOARD_DFU_FORCE") == "1"));
                Assert.True(s.Snapshot().IsConnected);
            }
        }

        private void Exercise(IBoardSession s)
        {
            s.Open();
            output.WriteLine(s.Description);
            BoardView v = s.Snapshot();
            Assert.True(v.IsConnected);
            Assert.NotNull(v.Focuser);
            Assert.NotNull(v.Rotator);
            Assert.Equal(0, v.Status.Error);

            int foc = v.Status.FocuserPosition;
            int step = foc + 50 <= v.Focuser.MaxPosition ? 50 : -50;
            Assert.Equal("OK", s.Raw(Commands.FocuserMoveBy(step)));
            v = WaitIdle(s);
            Assert.Equal(foc + step, v.Status.FocuserPosition);
            Assert.Equal("OK", s.Raw(Commands.FocuserMoveTo(foc)));
            Assert.Equal(foc, WaitIdle(s).Status.FocuserPosition);

            int rot = v.Status.RotatorAngle;
            Assert.Equal("OK", s.Raw(Commands.RotatorMoveBy(100)));
            v = WaitIdle(s);
            Assert.Equal((rot + 100) % 36000, v.Status.RotatorAngle);
            Assert.Equal("OK", s.Raw(Commands.RotatorMoveBy(-100)));
            Assert.Equal(rot, WaitIdle(s).Status.RotatorAngle);

            Assert.Equal("ERR,7", s.Raw(Commands.FocuserMoveBy(99999999)));
            Assert.Equal("OK", s.Do(BoardAction.ClearError));
            string imu = s.Raw(Commands.Imu);
            output.WriteLine(":94# " + imu + "   " + MainForm.Decode(imu));
            output.WriteLine(":91# " + s.Raw(Commands.EnvironmentDetail));
            output.WriteLine(":41,F# " + s.Raw(Commands.GetStallGuard(Axis.Focuser)));
            foreach (LogEntry e in s.ReadLog(0)) output.WriteLine("log " + e.Seq + " " + e.Text);
        }

        private static BoardView WaitIdle(IBoardSession s)
        {
            var sw = Stopwatch.StartNew();
            Thread.Sleep(300);
            while (sw.ElapsedMilliseconds < 20000)
            {
                BoardView v = s.Snapshot();
                if (v.Status != null && !v.Status.Busy) return v;
                Thread.Sleep(100);
            }
            throw new TimeoutException("board still busy after 20 s");
        }
    }
}
