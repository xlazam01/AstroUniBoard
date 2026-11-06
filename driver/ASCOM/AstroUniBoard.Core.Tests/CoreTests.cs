using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Xunit;

namespace AstroUniBoard.Core.Tests
{
    public class ParserTests
    {
        [Fact]
        public void StatusParsesHexFlags()
        {
            BoardStatus s;
            Assert.True(BoardStatus.TryParse("S,2,4120,4500,9000,-1,0,1B0", out s));
            Assert.Equal(BoardState.FocuserMoving, s.State);
            Assert.Equal(4120, s.FocuserPosition);
            Assert.Equal(4500, s.FocuserTarget);
            Assert.Equal(9000, s.RotatorAngle);
            Assert.True(s.FocuserMoving);
            Assert.False(s.RotatorMoving);
            Assert.True(s.Has(StatusFlags.SafeMode));
            Assert.True(s.Has(StatusFlags.ImuNotCalibrated));
            Assert.True(s.Has(StatusFlags.ExternalShtValid));
            Assert.False(s.Has(StatusFlags.FocuserUnverified));
        }

        [Theory]
        [InlineData("")]
        [InlineData("S,1,0,-1,0,-1,0")]           // too few fields
        [InlineData("S,1,0,-1,0,-1,0,30,1")]      // too many
        [InlineData("X,1,0,-1,0,-1,0,30")]        // wrong tag
        [InlineData("S,6,0,-1,0,-1,0,30")]        // unknown state
        [InlineData("S,1,a,-1,0,-1,0,30")]        // not a number
        [InlineData("S,1,0,-1,36000,-1,0,30")]    // angle out of range
        [InlineData("S,1,0,-1,0,-1,0,XYZ")]       // flags not hex
        public void StatusRejectsBadLines(string line)
        {
            BoardStatus s;
            Assert.False(BoardStatus.TryParse(line, out s));
        }

        [Fact]
        public void IdentityParsesCapsAndChecksProtocol()
        {
            BoardIdentity id;
            Assert.True(BoardIdentity.TryParse("ASTROUNIBOARD,REV2,FW2.0.0,2,2076349A4B31,F", out id));
            Assert.Equal("2076349A4B31", id.Serial);
            Assert.True(id.IsSupported);
            Assert.True(id.Has(BoardCaps.Focuser | BoardCaps.Rotator | BoardCaps.Imu | BoardCaps.Environment));
            Assert.True(BoardIdentity.TryParse("ASTROUNIBOARD,REV2,FW1.0.0,1,AB,1", out id));
            Assert.False(id.IsSupported);
            Assert.False(BoardIdentity.TryParse("OPENWHEEL,G0B1,FW1.0.1,2,207834924B31", out id));
            Assert.False(BoardIdentity.TryParse("ASTROUNIBOARD,REV2,FW2,2,AB", out id));   // no caps
        }

        [Fact]
        public void RecordsParse()
        {
            Housekeeping hk;
            Assert.True(Housekeeping.TryParse("H,312,3297,-1,-1,-1,A,0,-1", out hk));
            Assert.Equal(31.2, hk.McuTemperature, 3);
            Assert.Null(hk.DriverTemperatureClass);
            Assert.Equal(HousekeepingWarnings.DriverOvertemperature | HousekeepingWarnings.NoCrystal, hk.Warnings);
            Assert.Null(hk.RotatorDriverTemperatureClass);

            EnvironmentReading e;
            Assert.True(EnvironmentReading.TryParse("W,2,85,812,54", out e));
            Assert.Equal(8.5, e.Temperature, 3);
            Assert.Equal(81.2, e.Humidity, 3);
            Assert.Equal(5.4, e.DewPoint, 3);
            Assert.True(EnvironmentReading.TryParse("W,1,-123,999,-200", out e));
            Assert.False(EnvironmentReading.TryParse("W,3,85,812,54", out e));

            ImuReading imu;
            Assert.True(ImuReading.TryParse("I,-99999,6230,12,-998,41", out imu));
            Assert.Null(imu.RollCdeg);
            Assert.Equal(6230, imu.TiltCdeg);

            DriverConfig d;
            Assert.True(DriverConfig.TryParse("D,900,450,4,0,30", out d));
            Assert.Equal(4, d.Microsteps);
            Assert.False(DriverConfig.TryParse("D,900,450,0,0,30", out d));

            StallGuardInfo g;
            Assert.True(StallGuardInfo.TryParse("G,0,60,48,136", out g));
            Assert.Equal(16, g.SuggestedThreshold);

            ResetInfo r;
            Assert.True(ResetInfo.TryParse("R,20,7,1", out r));
            Assert.True(r.WatchdogReset);
            Assert.True(r.SafeMode);
        }

        [Fact]
        public void ReplyClassification()
        {
            Assert.Equal(ReplyKind.Ok, Reply.Parse("OK").Kind);
            Assert.Equal(16, Reply.Parse("ERR,16").ErrorCode);
            Assert.Equal(ReplyKind.Garbage, Reply.Parse("ERR,xx").Kind);
            Assert.Equal(ReplyKind.Garbage, Reply.Parse("a\u0001b").Kind);
            Assert.Equal("-9000", Reply.Parse("-9000").Value);
        }

        [Fact]
        public void CommandsMatchTheProtocol()
        {
            Assert.Equal(":51,4500#", Commands.FocuserMoveTo(4500));
            Assert.Equal(":52,-20#", Commands.FocuserMoveBy(-20));
            Assert.Equal(":71,19000#", Commands.RotatorMoveTo(19000));
            Assert.Equal(":75,-18000,18000#", Commands.RotatorSetWrap(-18000, 18000));
            Assert.Equal(":42,F,16#", Commands.SetStallThreshold(Axis.Focuser, 16));
            Assert.Equal(":48,R,650,325,16,0,0#", Commands.SetDriverConfig(Axis.Rotator, new DriverConfig { RunCurrentMa = 650, HoldCurrentMa = 325, Microsteps = 16, Mode = 0, IdleOffSeconds = 0 }));
            Assert.Equal(":54#", Commands.Get(54));
            Assert.Throws<ArgumentOutOfRangeException>(() => Commands.RotatorMoveTo(36000));
            Assert.True(Commands.SetDriverConfig(Axis.Focuser, new DriverConfig { RunCurrentMa = 9999, HoldCurrentMa = 9999, Microsteps = 256, Mode = 1, IdleOffSeconds = 99999 }).Length - 2 <= Commands.MaxCommandBody);
        }

        [Fact]
        public void VidPidCandidates()
        {
            var en = new FakeEnumerator();
            en.Devices.Add(new UsbSerialDevice { PortName = "COM3", Vid = 0x0483, Pid = 0x5740, UsbSerial = "ST" });
            en.Devices.Add(new UsbSerialDevice { PortName = "COM9", Vid = 0x1209, Pid = 0x0001, UsbSerial = "B" });
            en.Devices.Add(new UsbSerialDevice { PortName = "COM7", Vid = 0x1209, Pid = 0x0001, UsbSerial = "A" });
            List<UsbSerialDevice> c = DeviceFinder.FindCandidates(en, "B");
            Assert.Equal(2, c.Count);
            Assert.Equal("COM9", c[0].PortName);   // preferred serial first
            Assert.Equal("COM7", c[1].PortName);
        }
    }

    public class TransportTests
    {
        private static Transport Make(FakeBoard b) { b.Open(); return new Transport(b); }

        [Fact]
        public void PingWorksBeforeIdentifyButOtherCommandsAreGated()
        {
            var b = new FakeBoard();
            Transport t = Make(b);
            Assert.True(t.Execute(Commands.Ping, 200).IsOk);
            Assert.Equal(16, t.Execute(Commands.Status, 200).ErrorCode);
            Assert.Equal(ReplyKind.Value, t.Execute(Commands.Identify, 200).Kind);
            Assert.Equal(ReplyKind.Value, t.Execute(Commands.Status, 200).Kind);
        }

        [Fact]
        public void SilentDeviceTimesOutInTime()
        {
            var b = new FakeBoard { Mode = Misbehavior.Silent };
            Transport t = Make(b);
            var sw = Stopwatch.StartNew();
            Assert.Equal(ReplyKind.Timeout, t.Execute(Commands.Ping, 150).Kind);
            Assert.InRange(sw.ElapsedMilliseconds, 140, 600);
        }

        [Theory]
        [InlineData(Misbehavior.Garbage)]
        [InlineData(Misbehavior.NoTerminator)]
        [InlineData(Misbehavior.Flood)]
        public void BadReplyIsTransportFailureAndNeverHangs(Misbehavior mode)
        {
            var b = new FakeBoard { Mode = mode };
            Transport t = Make(b);
            var sw = Stopwatch.StartNew();
            Assert.True(t.Execute(Commands.Ping, 200).IsTransportFailure);
            Assert.True(sw.ElapsedMilliseconds < 800);
        }

        [Fact]
        public void LateReplyIsNotMistakenForTheNextAnswer()
        {
            var b = new FakeBoard { Mode = Misbehavior.Slow, ReplyDelayMs = 300 };
            Transport t = Make(b);
            Assert.Equal(ReplyKind.Timeout, t.Execute(Commands.Identify, 100).Kind);
            Thread.Sleep(350);                      // the late identify reply is now waiting in the buffer
            b.Mode = Misbehavior.None;
            Reply r = t.Execute(Commands.Ping, 200);  // stale input is discarded before sending
            Assert.True(r.IsOk);
        }

        [Fact]
        public void UnpluggedLinkThrowsLinkException()
        {
            var b = new FakeBoard();
            Transport t = Make(b);
            b.Unplug();
            Assert.Throws<LinkException>(() => t.Execute(Commands.Ping, 200));
        }

        [Fact]
        public void OverlongCommandIsRefusedBeforeSending()
        {
            var b = new FakeBoard();
            Transport t = Make(b);
            Assert.Throws<ArgumentException>(() => t.Execute(":" + new string('1', 47) + "#", 100));
            Assert.Empty(b.Received);
        }
    }

    public class ControllerTests : IDisposable
    {
        private readonly List<BoardController> controllers = new List<BoardController>();
        public void Dispose() { foreach (var c in controllers) c.Dispose(); }

        private static BoardOptions Fast()
        {
            return new BoardOptions
            {
                CommandTimeoutMs = 150,
                PollIdleMs = 50,
                PollBusyMs = 20,
                HousekeepingMs = 200,
                EnvironmentMs = 200,
                ReconnectMinMs = 50,
                ReconnectMaxMs = 200,
                MoveTimeoutMs = 2000,
                ImuTimeoutMs = 2000
            };
        }

        private BoardController Make(BoardOptions opt, FakeEnumerator en, Dictionary<string, FakeBoard> boards)
        {
            var c = new BoardController(opt, name => boards[name], en);
            controllers.Add(c);
            return c;
        }

        private static FakeEnumerator Devices(params string[] ports)
        {
            var en = new FakeEnumerator();
            foreach (string p in ports) en.Devices.Add(new UsbSerialDevice { PortName = p, Vid = 0x1209, Pid = 0x0001, UsbSerial = "S" + p });
            return en;
        }

        private static bool WaitFor(Func<bool> cond, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (cond()) return true;
                Thread.Sleep(5);
            }
            return cond();
        }

        private BoardController Connected(FakeBoard b, BoardOptions opt = null)
        {
            b.PortName = "COM9";
            BoardController c = Make(opt ?? Fast(), Devices("COM9"), new Dictionary<string, FakeBoard> { { "COM9", b } });
            c.Connect();
            Assert.True(c.WaitConnected(3000), c.Snapshot.Problem);
            Assert.True(WaitFor(() => c.Snapshot.Focuser != null && c.Snapshot.Rotator != null, 2000));
            return c;
        }

        [Fact]
        public void ConnectsAndReadsEverything()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            BoardSnapshot s = c.Snapshot;
            Assert.Equal("COM9", s.PortName);
            Assert.Equal("2076349A4B31", s.Identity.Serial);
            Assert.Equal(4000, s.Status.FocuserPosition);
            Assert.Equal(90.0, s.RotatorMechanicalDegrees, 3);
            Assert.Equal(8100, s.Focuser.MaxPosition);
            Assert.Equal(111, s.Focuser.StepSizeCentiMicrons);
            Assert.Equal(-18000, s.Rotator.WrapMinCdeg);
            Assert.Equal(88889, s.Rotator.StepsPerKiloDegree);
            Assert.NotNull(s.Housekeeping);
            Assert.NotNull(s.Environment);
            Assert.Equal(8.5, s.Environment.Temperature, 3);
            Assert.Equal("32", b.Received[1]);   // identify right after the ping, before anything else
        }

        [Fact]
        public void OpenWheelOnTheSharedTestPidIsSkipped()
        {
            var wheel = new FakeBoard { IsOpenWheel = true, PortName = "COM5" };
            var board = new FakeBoard { PortName = "COM9" };
            BoardController c = Make(Fast(), Devices("COM5", "COM9"), new Dictionary<string, FakeBoard> { { "COM5", wheel }, { "COM9", board } });
            c.Connect();
            Assert.True(c.WaitConnected(3000), c.Snapshot.Problem);
            Assert.Equal("COM9", c.Snapshot.PortName);
            Assert.DoesNotContain("33", wheel.Received);   // nothing but ping/identify was sent to the wheel
        }

        [Fact]
        public void FocuserMoveIsVisibleAtOnceThenArrives()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            Assert.True(c.FocuserMoveTo(4500).Success);
            Assert.True(c.Snapshot.IsFocuserMoving);
            Assert.True(WaitFor(() => !c.Snapshot.IsBusy && c.Snapshot.Status.FocuserPosition == 4500, 2000));
        }

        [Fact]
        public void FocuserTargetOutsideLimitsIsRejectedWithoutSending()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            CommandResult r = c.FocuserMoveTo(9000);
            Assert.False(r.Success);
            Assert.Equal((int)BoardError.BadParameter, r.ErrorCode);
            Assert.DoesNotContain("51,9000", b.Received);
            Assert.False(c.FocuserMoveBy(-5000).Success);
        }

        [Fact]
        public void LostMoveReplyIsRecoveredFromStatus()
        {
            var b = new FakeBoard { DropReplyToMove = true, MoveMs = 1000 };
            BoardController c = Connected(b);
            Assert.True(c.FocuserMoveTo(5000).Success);
            Assert.True(c.Snapshot.IsFocuserMoving);
        }

        [Fact]
        public void SecondMoveWhileBusyIsRejectedByTheBoard()
        {
            var b = new FakeBoard { MoveMs = 1000 };
            BoardController c = Connected(b);
            Assert.True(c.FocuserMoveTo(5000).Success);
            CommandResult r = c.RotatorMoveTo(0);
            Assert.False(r.Success);
            Assert.Equal((int)BoardError.Busy, r.ErrorCode);
        }

        [Fact]
        public void RotatorMoveUsesTheWrapAndArrives()
        {
            var b = new FakeBoard { RotCdeg = 17000 };
            BoardController c = Connected(b);
            Assert.True(c.RotatorMoveTo(19000).Success);    // 190 deg = -170, goes back through 0
            Assert.True(c.Snapshot.IsRotatorMoving);
            Assert.True(WaitFor(() => !c.Snapshot.IsBusy, 2000));
            Assert.Equal(-17000, b.RotCdeg);
            Assert.Equal(190.0, c.Snapshot.RotatorMechanicalDegrees, 3);
        }

        [Fact]
        public void HaltStopsAMove()
        {
            var b = new FakeBoard { MoveMs = 5000 };
            BoardController c = Connected(b);
            Assert.True(c.FocuserMoveTo(6000).Success);
            Assert.True(c.FocuserHalt().Success);
            Assert.True(WaitFor(() => !c.Snapshot.IsBusy, 1000));
            Assert.Equal(1, b.HaltCount);
        }

        [Fact]
        public void PortReopenedByAnotherProgramIsHandledByIdentifyingAgain()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            b.Relock();
            Assert.True(c.FocuserMoveTo(4100).Success);
            Assert.True(c.Snapshot.IsConnected);
            int i = b.Received.LastIndexOf("51,4100");
            Assert.True(i > 1);
            Assert.Equal("32", b.Received[i - 1]);   // re-identified, then the command repeated
        }

        [Fact]
        public void ADifferentBoardAnsweringAfterRelockEndsTheConnection()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            b.Serial = "OTHER0000001";
            b.Relock();
            Assert.False(c.FocuserMoveTo(4100).Success);
            Assert.True(WaitFor(() => !c.Snapshot.IsConnected, 1000));
        }

        [Fact]
        public void DeviceFaultIsReportedAndCleared()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            b.ErrorCode = 13;
            Assert.True(WaitFor(() => c.Snapshot.DeviceError == 13, 1000));
            Assert.Contains("stalled", c.Snapshot.Problem);
            Assert.True(c.ClearError().Success);
            Assert.True(WaitFor(() => c.Snapshot.DeviceError == 0 && c.Snapshot.Problem == null, 1000));
        }

        [Fact]
        public void MissingSensorGivesNoEnvironmentButKeepsTheConnection()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            b.NoSensor = true;
            Assert.True(WaitFor(() => c.Snapshot.Environment == null, 1500));
            Assert.True(c.Snapshot.IsConnected);
        }

        [Fact]
        public void SettingChangeIsReadBack()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            Assert.True(c.SetFocuserSetting(FocuserSetting.Speed, 1200).Success);
            Assert.Equal(1200, c.Snapshot.Focuser.Speed);
            CommandResult bad = c.SetFocuserSetting(FocuserSetting.Speed, 0);
            Assert.False(bad.Success);
            Assert.Equal((int)BoardError.BadParameter, bad.ErrorCode);
        }

        [Fact]
        public void RawCommandReturnsTheReply()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            Reply r;
            Assert.True(c.Raw(":70#", out r).Success);
            Assert.Equal("9000", r.Value);
            Assert.True(c.Raw(":99#", out r).Success);
            Assert.Equal(10, r.ErrorCode);
            Assert.False(c.Raw("70", out r).Success);
        }

        [Fact]
        public void WatchdogHaltsAStuckMove()
        {
            var b = new FakeBoard { StuckMoving = true };
            BoardOptions o = Fast();
            o.MoveTimeoutMs = 300;
            BoardController c = Connected(b, o);
            Assert.True(c.FocuserMoveTo(6000).Success);
            Assert.True(WaitFor(() => b.HaltCount > 0, 2000));
            Assert.Contains("halted by the driver", c.Snapshot.Problem);
        }

        [Theory]
        [InlineData(Misbehavior.Silent)]
        [InlineData(Misbehavior.Garbage)]
        [InlineData(Misbehavior.NoTerminator)]
        [InlineData(Misbehavior.Flood)]
        public void DeviceThatStartsMisbehavingIsDroppedWithoutHanging(Misbehavior mode)
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            b.Mode = mode;
            var sw = Stopwatch.StartNew();
            Assert.True(WaitFor(() => c.Snapshot.Connection == ConnectionState.Reconnecting, 3000));
            Assert.True(sw.ElapsedMilliseconds < 3000);
            sw.Restart();
            Assert.False(c.FocuserMoveTo(4100, 500).Success);   // callers get an answer, not a hang
            Assert.True(sw.ElapsedMilliseconds < 1500);
        }

        [Theory]
        [InlineData(Misbehavior.Silent)]
        [InlineData(Misbehavior.Garbage)]
        [InlineData(Misbehavior.AlwaysOk)]
        [InlineData(Misbehavior.Flood)]
        public void BrokenDeviceNeverConnects(Misbehavior mode)
        {
            var b = new FakeBoard { Mode = mode, PortName = "COM9" };
            BoardController c = Make(Fast(), Devices("COM9"), new Dictionary<string, FakeBoard> { { "COM9", b } });
            c.Connect();
            Assert.False(c.WaitConnected(800));
            Assert.NotNull(c.Snapshot.Problem);
        }

        [Fact]
        public void UnplugAndReplugReconnectsToTheSameBoard()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            b.Unplug();
            Assert.True(WaitFor(() => c.Snapshot.Connection == ConnectionState.Reconnecting, 2000));
            Assert.False(c.FocuserHalt(300).Success);
            b.Replug();
            Assert.True(c.WaitConnected(3000), c.Snapshot.Problem);
            Assert.Equal("2076349A4B31", c.Snapshot.Identity.Serial);
        }

        [Fact]
        public void AfterARebootOnlyTheSameSerialIsAccepted()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            b.Serial = "OTHER0000001";       // a different board now answers on this port
            Assert.True(c.Reboot().Success);
            // first reconnect attempt 1.5 s after the reboot
            Assert.True(WaitFor(() => (c.Snapshot.Problem ?? "").Contains("another board"), 4000), c.Snapshot.Problem);
            Assert.False(c.Snapshot.IsConnected);
            b.Serial = "2076349A4B31";
            Assert.True(c.WaitConnected(3000), c.Snapshot.Problem);
        }

        [Fact]
        public void EnterDfuDisconnects()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            Assert.True(c.EnterDfu().Success);
            Assert.True(WaitFor(() => c.Snapshot.Connection == ConnectionState.Disconnected, 1000));
            Assert.Contains("DFU", c.Snapshot.Problem);
        }

        [Fact]
        public void RebootAndDfuAreRefusedWhileAnAxisMoves()
        {
            var b = new FakeBoard { MoveMs = 1500, RefuseResetWhileBusy = false };   // also on firmware before #76
            BoardController c = Connected(b);
            Assert.True(c.FocuserMoveTo(6000).Success);
            CommandResult r = c.Reboot();
            Assert.False(r.Success);
            Assert.Equal((int)BoardError.Busy, r.ErrorCode);
            Assert.Contains("halt both axes first", r.Message);
            Assert.False(c.EnterDfu().Success);
            Reply reply;
            Assert.False(c.Raw(Commands.EnterDfu, out reply).Success);
            Assert.DoesNotContain("22", b.Received);
            Assert.DoesNotContain("31", b.Received);
            Assert.True(c.Snapshot.IsConnected);

            Assert.True(c.HaltAll().Success);
            Assert.True(WaitFor(() => !c.Snapshot.IsBusy, 1000));
            Assert.True(c.EnterDfu().Success);
        }

        [Fact]
        public void UnverifiedRotatorRefusalIsRecognised()
        {
            // TODO(#75): use the firmware's code once PROTOCOL.md names it; 7 stands in for it
            var b = new FakeBoard { RefuseUnverifiedRotatorCode = 7 };
            b.Flags |= (int)StatusFlags.RotatorUnverified;
            BoardController c = Connected(b);
            CommandResult r = c.RotatorMoveTo(0);
            Assert.False(r.Success);
            Assert.True(BoardErrors.IsRotatorUnverifiedRefusal(r.ErrorCode, c.Snapshot.Status));

            b.Flags &= ~(int)StatusFlags.RotatorUnverified;
            Assert.True(WaitFor(() => !c.Snapshot.Status.Has(StatusFlags.RotatorUnverified), 1000));
            b.WrapMin = 0; b.WrapMax = 100;
            r = c.RotatorMoveTo(20000);                       // outside the wrap: a real limit, no hint
            Assert.Equal((int)BoardError.BadParameter, r.ErrorCode);
            Assert.False(BoardErrors.IsRotatorUnverifiedRefusal(r.ErrorCode, c.Snapshot.Status));
        }

        [Fact]
        public void ConcurrentCallersAllGetBoundedAnswers()
        {
            var b = new FakeBoard();
            BoardController c = Connected(b);
            var threads = new List<Thread>();
            int failures = 0;
            for (int i = 0; i < 8; i++)
            {
                var t = new Thread(() =>
                {
                    for (int k = 0; k < 10; k++)
                    {
                        Reply r;
                        if (!c.Raw(":50#", out r).Success || r.Value == null) Interlocked.Increment(ref failures);
                    }
                });
                threads.Add(t);
                t.Start();
            }
            foreach (Thread t in threads) Assert.True(t.Join(20000));
            Assert.Equal(0, failures);
        }
    }
}
