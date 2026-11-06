// The ASCOM path of the debug app against the Action contract (a hand-written fake driver and the
// loopback over a BoardController with the in-memory FakeBoard), the direct path against FakeBoard,
// and the parsers/formatters the tabs use.
using System;
using System.Collections.Generic;
using System.Linq;
using AstroUniBoard.Core;
using AstroUniBoard.Core.Tests;
using Xunit;

namespace AstroUniBoard.DebugApp.Tests
{
    public class SnapshotParsing
    {
        [Fact]
        public void CamelCaseContractJsonParses()
        {
            const string json = @"{""connection"":""Connected"",""port"":""COM9"",""problem"":null,
              ""identity"":{""model"":""REV1"",""firmware"":""FW2.0.0"",""protocol"":2,""serial"":""2076349A4B31"",""caps"":15},
              ""status"":{""state"":2,""focuserPosition"":4120,""focuserTarget"":4500,""rotatorAngle"":9000,""rotatorTarget"":-1,""error"":0,""flags"":48},
              ""housekeeping"":{""mcuTemperature"":31.2,""vdda"":3.297,""driverTemperatureClass"":null,""warnings"":4,""focuserDriverTemperatureClass"":0,""rotatorDriverTemperatureClass"":null},
              ""environment"":{""source"":2,""temperature"":8.5,""humidity"":81.2,""dewPoint"":5.4},
              ""focuser"":{""maxPosition"":8100,""speed"":900,""acceleration"":2000,""backlash"":0,""reverse"":true,""stepSizeCentiMicrons"":111},
              ""rotator"":{""wrapMinCdeg"":-18000,""wrapMaxCdeg"":18000,""speedCdegPerS"":1200,""accelerationCdegPerS2"":2400,""reverse"":false,""stepsPerKiloDegree"":88889,""bootHome"":1,""homeToleranceCdeg"":50}}";
            BoardView v = BoardView.FromJson(json);
            Assert.True(v.IsConnected);
            Assert.Equal("COM9", v.Port);
            Assert.Equal("FW2.0.0", v.Identity.Firmware);
            Assert.True(v.Identity.Has(BoardCaps.Imu | BoardCaps.Environment));
            Assert.Equal((int)BoardState.FocuserMoving, v.Status.State);
            Assert.Equal(4500, v.Status.FocuserTarget);
            Assert.True(v.Status.Has(StatusFlags.OnboardShtValid | StatusFlags.ExternalShtValid));
            Assert.Null(v.Housekeeping.DriverTemperatureClass);
            Assert.Equal(0, v.Housekeeping.FocuserDriverTemperatureClass);
            Assert.Equal(3.297, v.Housekeeping.Vdda, 3);
            Assert.Equal(5.4, v.Environment.DewPoint, 1);
            Assert.True(v.Focuser.Reverse);
            Assert.Equal(-18000, v.Rotator.WrapMinCdeg);
            Assert.True(v.Rotator.BootHome);
        }

        [Fact]
        public void PascalCaseEnumNamesAndMissingObjectsParse()
        {
            BoardView v = BoardView.FromJson(@"{""Connection"":""Reconnecting"",""Problem"":""board is rebooting"",""Status"":{""State"":""ImuBusy"",""FocuserPosition"":1}}");
            Assert.False(v.IsConnected);
            Assert.Equal("board is rebooting", v.Problem);
            Assert.Equal((int)BoardState.ImuBusy, v.Status.State);
            Assert.Equal(-1, v.Status.FocuserTarget);
            Assert.Null(v.Identity);
            Assert.Null(v.Rotator);
        }

        [Fact]
        public void LocalServerSnapshotParses()
        {
            // verbatim shape of driver/ASCOM/AstroUniBoard.Ascom BoardActions (ascom-drivers branch)
            const string json = @"{""connection"":""Connected"",""port"":""COM9"",""problem"":null,
             ""identity"":{""model"":""G0B1"",""firmware"":""FW2.0.0"",""protocol"":2,""serial"":""X"",""caps"":15},
             ""status"":{""state"":""RotatorMoving"",""stateCode"":3,""focuserPosition"":4000,""focuserTarget"":-1,""rotatorAngle"":1200,""rotatorTarget"":9000,""error"":0,""errorText"":null,""flags"":48,""timeUtc"":""2026-11-06T20:00:00.000Z""},
             ""housekeeping"":{""mcuTemperature"":31.2,""vdda"":3.301,""driverTemperatureClass"":0,""focuserDriverTemperatureClass"":0,""rotatorDriverTemperatureClass"":0,""warnings"":0,""warningsText"":null},
             ""environment"":null,
             ""focuser"":{""maxPosition"":8100,""speed"":900,""acceleration"":2000,""backlash"":0,""reverse"":false,""stepSizeCentiMicrons"":111},
             ""rotator"":{""wrapMinCdeg"":-18000,""wrapMaxCdeg"":18000,""speedCdegPerS"":1200,""accelerationCdegPerS2"":2400,""reverse"":false,""stepsPerKiloDegree"":88889,""bootHome"":false,""homeToleranceCdeg"":50},
             ""host"":{""driverVersion"":""0.1"",""clients"":2,""skyOffsetDegrees"":0,""tempComp"":false,""tempCompStepsPerDegree"":0,""tempCompThresholdDegrees"":0.5}}";
            BoardView v = BoardView.FromJson(json);
            Assert.True(v.IsConnected);
            Assert.Equal((int)BoardState.RotatorMoving, v.Status.State);
            Assert.Equal(9000, v.Status.RotatorTarget);
            Assert.Null(v.Environment);
            Assert.Equal(88889, v.Rotator.StepsPerKiloDegree);
        }

        [Fact]
        public void LocalServerLogTimesAreLocal()
        {
            var d = new FakeDriver();
            using (var s = new AscomSession(() => d))
            {
                s.Open();
                d.LogJson = "[{\"seq\":5,\"time\":\"2026-11-06T14:41:01.694\",\"text\":\"x\"}]";
                LogEntry e = s.ReadLog(0).Single();
                Assert.Equal(new DateTime(2026, 11, 6, 14, 41, 1, 694, DateTimeKind.Local).ToUniversalTime(), e.Time);
            }
        }

        [Fact]
        public void ConnectionAsNumberParses()
        {
            Assert.True(BoardView.FromJson(@"{""connection"":2}").IsConnected);
        }

        [Fact]
        public void RepliesAreExplainedAndDecoded()
        {
            Assert.Equal("ERR,7 — " + BoardErrors.Describe(7), Replies.Explain("ERR,7"));
            Assert.Equal("OK", Replies.Explain("OK"));
            Assert.True(Replies.IsFailure("ERROR: board is not connected"));
            Assert.True(Replies.IsFailure("ERR,16"));
            Assert.False(Replies.IsFailure("S,1,0,-1,0,-1,0,30"));
            Assert.Contains("state=Idle", MainForm.Decode("S,1,12500,-1,9000,-1,0,30"));
            Assert.Contains("suggested 16", MainForm.Decode("G,0,60,48,136"));
            Assert.Contains("StealthChop", MainForm.Decode("D,900,450,4,0,30"));
            Assert.Contains("motor stalled", MainForm.Decode("E,13"));
            Assert.Null(MainForm.Decode("OK"));
        }

        [Theory]
        [InlineData("imu", "IMU")]
        [InlineData("IMU", "IMU")]
        [InlineData("motors", "Motors / StallGuard")]
        [InlineData("firmware", "Firmware")]
        public void TabOptionSelectsTheTab(string option, string title)
        {
            using (var f = new MainForm(false, null))
            {
                Assert.True(f.SelectTab(option));
                var tabs = (System.Windows.Forms.TabControl)typeof(MainForm).GetField("tabs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(f);
                Assert.Equal(title, tabs.SelectedTab.Text);
                Assert.False(f.SelectTab("nonsense"));
            }
        }

        [Theory]
        [InlineData("33", ":33#")]
        [InlineData(":33", ":33#")]
        [InlineData(" :41,F# ", ":41,F#")]
        [InlineData("52,-100#", ":52,-100#")]
        public void ConsoleCommandsAreNormalized(string typed, string sent)
        {
            Assert.Equal(sent, MainForm.NormalizeCommand(typed));
        }
    }

    /// <summary>A driver that answers the contract from canned data, the way the LocalServer should.</summary>
    internal sealed class FakeDriver : IDriverChannel
    {
        public readonly List<string> Calls = new List<string>();
        public bool Connected;
        public string SnapshotJson = @"{""Connection"":""Connected"",""Port"":""COM9"",""Identity"":{""Model"":""REV1"",""Firmware"":""FW2.0.0"",""Protocol"":2,""Serial"":""ABC"",""Caps"":15}}";
        public string LogJson;
        public Func<string, string> RawReply = c => c == ":33#" ? "S,1,0,-1,0,-1,0,30" : "ERR,10";

        public string Name { get { return "fake driver"; } }
        public void Connect() { Connected = true; Calls.Add("connect"); }
        public void Disconnect() { Connected = false; Calls.Add("disconnect"); }
        public void Dispose() { Calls.Add("dispose"); }

        public string Action(string name, string parameter)
        {
            Calls.Add(name + "(" + parameter + ")");
            if (!Connected) return "ERROR: not connected";
            switch (name)
            {
                case "AstroUniBoard:Snapshot": return SnapshotJson;
                case "AstroUniBoard:Raw": return RawReply(parameter);
                case "AstroUniBoard:Log":
                    if (LogJson != null) return LogJson;
                    var all = new[] { "[{\"seq\":1,\"time\":\"2026-11-06T13:00:00.000Z\",\"text\":\"connected COM9\"}", ",{\"seq\":2,\"time\":\"2026-11-06T13:00:01.000Z\",\"text\":\"board warning\"}]" };
                    return parameter == "1" ? "[{\"seq\":2,\"time\":\"2026-11-06T13:00:01.000Z\",\"text\":\"board warning\"}]" : all[0] + all[1];
                case "AstroUniBoard:HaltAll": case "AstroUniBoard:ClearError": case "AstroUniBoard:SaveSettings": return "OK";
                case "AstroUniBoard:ImuCalibrate": return "ERROR: board rejected the command: " + BoardErrors.Describe(15);
                default: throw new NotSupportedException(name);
            }
        }
    }

    public class AscomPathWithFakeDriver
    {
        [Fact]
        public void UsesOnlyTheActionContract()
        {
            var d = new FakeDriver();
            using (var s = new AscomSession(() => d))
            {
                s.Open();
                Assert.Contains("REV1 FW2.0.0 on COM9", s.Description);
                Assert.Equal("S,1,0,-1,0,-1,0,30", s.Raw(":33#"));
                Assert.Equal("ERR,10", s.Raw(":99#"));
                Assert.Equal("OK", s.Do(BoardAction.HaltAll));
                Assert.StartsWith("ERROR: board rejected", s.Do(BoardAction.ImuCalibrate));
                // a driver exception becomes an ERROR reply, not a crash
                Assert.StartsWith("ERROR: ", s.Do(BoardAction.EnterDfu));
            }
            Assert.Contains("AstroUniBoard:Raw(:33#)", d.Calls);
            Assert.Contains("AstroUniBoard:HaltAll()", d.Calls);
            Assert.Equal("dispose", d.Calls.Last());
            Assert.All(d.Calls.Where(c => c.Contains("(")), c => Assert.StartsWith("AstroUniBoard:", c));
        }

        [Fact]
        public void LogIsReadIncrementally()
        {
            var d = new FakeDriver();
            using (var s = new AscomSession(() => d))
            {
                s.Open();
                IList<LogEntry> all = s.ReadLog(0);
                Assert.Equal(new long[] { 1, 2 }, all.Select(e => e.Seq).ToArray());
                Assert.Equal(new DateTime(2026, 11, 6, 13, 0, 0, DateTimeKind.Utc), all[0].Time);
                IList<LogEntry> next = s.ReadLog(1);
                Assert.Single(next);
                Assert.Equal("board warning", next[0].Text);
                Assert.Contains("AstroUniBoard:Log(1)", d.Calls);
            }
        }

        [Fact]
        public void SnapshotErrorIsReported()
        {
            var d = new FakeDriver { SnapshotJson = "ERROR: driver busy" };
            using (var s = new AscomSession(() => d))
            {
                var ex = Assert.Throws<InvalidOperationException>(() => s.Open());
                Assert.Equal("driver busy", ex.Message);
            }
        }

        [Fact]
        public void MissingDriverSaysSo()
        {
            using (var s = new AscomSession(() => new ComDriverChannel("ASCOM.AstroUniBoard.DoesNotExist")))
            {
                var ex = Assert.Throws<InvalidOperationException>(() => s.Open());
                Assert.Contains("not installed", ex.Message);
            }
        }
    }

    public class LoopbackOverFakeBoard : IDisposable
    {
        private readonly FakeBoard board = new FakeBoard { PortName = "COM9" };
        private readonly FakeBoard openWheel = new FakeBoard { PortName = "COM14", IsOpenWheel = true };
        private readonly AscomSession session;

        private static BoardOptions Fast()
        {
            return new BoardOptions { CommandTimeoutMs = 150, PollIdleMs = 50, PollBusyMs = 20, HousekeepingMs = 200, EnvironmentMs = 200, ReconnectMinMs = 50, ReconnectMaxMs = 200 };
        }

        public LoopbackOverFakeBoard()
        {
            var en = new FakeEnumerator();
            // the OpenWheel on the shared test PID comes first and must be skipped
            en.Devices.Add(new UsbSerialDevice { PortName = "COM14", Vid = 0x1209, Pid = 0x0001, UsbSerial = "OW" });
            en.Devices.Add(new UsbSerialDevice { PortName = "COM9", Vid = 0x1209, Pid = 0x0001, UsbSerial = board.Serial });
            var boards = new Dictionary<string, FakeBoard> { ["COM9"] = board, ["COM14"] = openWheel };
            session = new AscomSession(() => new LoopbackDriver(new DirectSession(null, n => boards[n], en, Fast())));
        }

        public void Dispose() { session.Dispose(); }

        [Fact]
        public void FullContractRoundTrip()
        {
            session.Open();
            BoardView v = session.Snapshot();
            Assert.True(v.IsConnected);
            Assert.Equal("COM9", v.Port);
            Assert.Equal(board.Serial, v.Identity.Serial);
            Assert.Equal(4000, v.Status.FocuserPosition);
            Assert.Equal(8100, v.Focuser.MaxPosition);
            Assert.Equal(-18000, v.Rotator.WrapMinCdeg);

            Assert.StartsWith("S,1,4000,", session.Raw(":33#"));
            Assert.Equal("ERR,7", session.Raw(Commands.FocuserMoveBy(99999999)));
            Assert.StartsWith("ERROR: not a valid command", session.Raw("33"));
            Assert.Equal("OK", session.Raw(Commands.FocuserMoveTo(4100)));
            Assert.Equal("OK", session.Do(BoardAction.HaltAll));
            Assert.True(board.HaltCount >= 1);

            board.ErrorCode = 13;
            Assert.Equal("OK", session.Do(BoardAction.ClearError));
            Assert.Equal(0, board.ErrorCode);

            // a setting change through Raw refreshes the cached settings in the snapshot
            board.FocMax = 8100;
            Assert.Equal("OK", session.Raw(Commands.Set(59, 1500)));
            Assert.Equal(1500, session.Snapshot().Focuser.Speed);

            IList<LogEntry> log = session.ReadLog(0);
            Assert.Contains(log, e => e.Text.StartsWith("connected COM9", StringComparison.Ordinal));
            Assert.Contains(log, e => e.Text.Contains("COM14"));   // the OpenWheel was rejected
            long last = log.Max(e => e.Seq);
            Assert.Empty(session.ReadLog(last));
        }

        [Fact]
        public void ImuReadingGoesThrough()
        {
            session.Open();
            ImuReading imu;
            Assert.True(ImuReading.TryParse(session.Raw(Commands.Imu), out imu));
            Assert.Equal(-120, imu.RollCdeg);
        }
    }
}
