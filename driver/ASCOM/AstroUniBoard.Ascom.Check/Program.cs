// Drives the real driver classes (without COM registration) through IFocuserV4, IRotatorV4 and
// IObservingConditionsV2 against a real board. Moves the focuser by a few hundred steps and the
// rotator by 10-20 degrees; restores the rotator sky offset at the end.
//   AstroUniBoard.Ascom.Check.exe          all checks
using System;
using System.Diagnostics;
using System.Threading;
using ASCOM.DeviceInterface;

internal static class Program
{
    private static int failures;

    private static void Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what + (detail.Length > 0 ? "   " + detail : ""));
        if (!ok) failures++;
    }

    private static void Info(string text) { Console.WriteLine("  info  " + text); }

    private static T Expect<T>(Action a) where T : Exception
    {
        try { a(); }
        catch (T ex) { return ex; }
        catch (Exception ex) { Console.WriteLine("    wrong exception: " + ex.GetType().Name + ": " + ex.Message); return null; }
        return null;
    }

    private static bool WaitUntil(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }

    private static double AngleDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % 360.0;
        return d > 180 ? 360 - d : d;
    }

    private static object Create(string cls)
    {
        return Activator.CreateInstance(Type.GetType("AstroUniBoard.Ascom." + cls + ", AstroUniBoard.Ascom.Server"));
    }

    private static int Main()
    {
        var foc = (IFocuserV4)Create("Focuser");
        var rot = (IRotatorV4)Create("Rotator");
        var oc = (IObservingConditionsV2)Create("ObservingConditions");

        Console.WriteLine("identity and not-connected behaviour");
        Check("interface versions 4 / 4 / 2", foc.InterfaceVersion == 4 && rot.InterfaceVersion == 4 && oc.InterfaceVersion == 2);
        Check("names and driver info", foc.Name.Contains("Focuser") && rot.Name.Contains("Rotator") && foc.DriverInfo.Contains("AstroUniBoard"), foc.DriverVersion);
        Check("not connected: Focuser.Position throws NotConnectedException", Expect<ASCOM.NotConnectedException>(() => { var p = foc.Position; }) != null);
        Check("not connected: Rotator.Position throws NotConnectedException", Expect<ASCOM.NotConnectedException>(() => { var p = rot.Position; }) != null);
        Check("not connected: Temperature throws NotConnectedException", Expect<ASCOM.NotConnectedException>(() => { var p = oc.Temperature; }) != null);
        Check("not connected: Snapshot action works", foc.Action("AstroUniBoard:Snapshot", "").Contains("\"connection\":"));
        Check("not connected: Raw action returns ERROR:", foc.Action("AstroUniBoard:Raw", ":33#").StartsWith("ERROR: "));

        Console.WriteLine("focuser");
        var sw = Stopwatch.StartNew();
        foc.Connected = true;
        Check("Focuser Connected = true", foc.Connected, sw.ElapsedMilliseconds + " ms");
        string snap = foc.Action("AstroUniBoard:Snapshot", "");
        Info("Snapshot = " + snap);
        Check("Snapshot has identity, status, focuser and rotator settings", snap.Contains("\"identity\":{") && snap.Contains("\"status\":{") && snap.Contains("\"focuser\":{") && snap.Contains("\"rotator\":{"));
        string raw = foc.Action("AstroUniBoard:Raw", ":33#");
        Check("Raw :33# returns the status line with '#'", raw.StartsWith("S,") && raw.EndsWith("#"), raw);
        Check("Raw :99# (reserved) returns ERR,10#", foc.Action("AstroUniBoard:Raw", ":99#") == "ERR,10#");
        Check("Raw garbage returns ERROR:", foc.Action("AstroUniBoard:Raw", "hello").StartsWith("ERROR: "));
        Check("CommandString(:25#) = OK#", foc.CommandString(":25#", true) == "OK#");
        Check("CommandString garbage throws DriverException", Expect<ASCOM.DriverException>(() => foc.CommandString("x", true)) != null);
        Check("unknown action throws ActionNotImplementedException", Expect<ASCOM.ActionNotImplementedException>(() => foc.Action("Explode", "")) != null);
        Check("SupportedActions lists the 10 contract actions", foc.SupportedActions.Count == 10 && foc.SupportedActions.Contains("AstroUniBoard:Raw"));
        string log = foc.Action("AstroUniBoard:Log", "");
        Check("Log returns a JSON array with entries", log.StartsWith("[{\"seq\":") && log.EndsWith("]"), log.Length + " chars");
        Check("Log after a huge seq is empty", foc.Action("AstroUniBoard:Log", "999999999") == "[]");
        Check("Action ClearError = OK", foc.Action("AstroUniBoard:ClearError", "") == "OK");

        int max = foc.MaxStep;
        int pos = foc.Position;
        Check("Absolute, MaxStep > 0, MaxIncrement = MaxStep", foc.Absolute && max > 0 && foc.MaxIncrement == max, "max " + max);
        Check("Position inside 0..MaxStep", pos >= 0 && pos <= max, "position " + pos);
        try { Info("StepSize = " + foc.StepSize + " µm"); } catch (ASCOM.PropertyNotImplementedException) { Info("StepSize not implemented (board says 0)"); }
        bool tca = foc.TempCompAvailable;
        Info("TempCompAvailable = " + tca);
        if (tca)
        {
            try { Info("Temperature = " + foc.Temperature + " °C"); } catch (Exception ex) { Check("Temperature readable", false, ex.Message); }
            foc.TempComp = true;
            Check("TempComp can be switched on", foc.TempComp);
            int tpos = foc.Position;
            int t1 = tpos > max / 2 ? tpos - 50 : tpos + 50;
            foc.Move(t1);
            Check("Move is accepted while TempComp is on (IFocuserV3+)", WaitUntil(() => !foc.IsMoving, 10000) && foc.Position == t1, "at " + foc.Position);
            foc.TempComp = false;
            Check("TempComp switched off", !foc.TempComp);
            pos = foc.Position;
        }

        int target = pos > max / 2 ? pos - 300 : pos + 300;
        foc.Move(target);
        Check("IsMoving right after Move", foc.IsMoving);
        Check("focuser arrives at " + target, WaitUntil(() => !foc.IsMoving, 15000) && foc.Position == target, "at " + foc.Position);
        foc.Move(target);
        Check("Move to the current position is a no-op", !foc.IsMoving && foc.Position == target);
        foc.Move(max + 100000);
        Thread.Sleep(300);
        Info("Move(max + 100000) clamped: moving toward " + max);
        foc.Halt();
        Check("Halt stops the move", WaitUntil(() => !foc.IsMoving, 5000), "stopped at " + foc.Position);
        int stopped = foc.Position;
        Check("stopped between start and max", stopped > target && stopped <= max, stopped.ToString());
        foc.Move(pos);
        Check("focuser back at " + pos, WaitUntil(() => !foc.IsMoving, 20000) && foc.Position == pos, "at " + foc.Position);
        Check("DeviceState has IsMoving, Position, TimeStamp", foc.DeviceState.Count >= 3);

        Console.WriteLine("rotator (shares the connection)");
        sw.Restart();
        rot.Connect();
        Check("Rotator async Connect", WaitUntil(() => !rot.Connecting, 10000) && rot.Connected, sw.ElapsedMilliseconds + " ms");
        float mech0 = rot.MechanicalPosition;
        string before = foc.Action("AstroUniBoard:Snapshot", "");
        double offset0 = double.Parse(before.Substring(before.IndexOf("\"skyOffsetDegrees\":") + 19).Split(',')[0], System.Globalization.CultureInfo.InvariantCulture);
        Info("mechanical " + mech0 + "°, sky " + rot.Position + "°, offset " + offset0 + "°, step " + rot.StepSize + "°, reverse " + rot.Reverse);
        Check("CanReverse", rot.CanReverse);
        Check("MechanicalPosition in 0..360", mech0 >= 0 && mech0 < 360);
        rot.Sync(100f);
        Check("Sync(100): Position = 100", AngleDiff(rot.Position, 100) < 0.02, rot.Position.ToString());
        Check("Sync does not move: MechanicalPosition unchanged", AngleDiff(rot.MechanicalPosition, mech0) < 0.02);
        Check("MoveAbsolute(400) throws InvalidValueException", Expect<ASCOM.InvalidValueException>(() => rot.MoveAbsolute(400)) != null);
        Check("Sync(-1) throws InvalidValueException", Expect<ASCOM.InvalidValueException>(() => rot.Sync(-1)) != null);
        rot.MoveAbsolute(115f);
        Check("TargetPosition = 115 right after MoveAbsolute", AngleDiff(rot.TargetPosition, 115) < 0.02 && rot.IsMoving, rot.TargetPosition.ToString());
        Check("rotator arrives at sky 115", WaitUntil(() => !rot.IsMoving, 15000) && AngleDiff(rot.Position, 115) < 0.05, rot.Position.ToString());
        Check("mechanical moved by 15", AngleDiff(rot.MechanicalPosition, mech0 + 15) < 0.05, rot.MechanicalPosition.ToString());
        rot.Move(-20f);
        Check("relative Move(-20) arrives at sky 95", WaitUntil(() => !rot.IsMoving, 15000) && AngleDiff(rot.Position, 95) < 0.05, rot.Position.ToString());

        // the firmware moves one axis at a time: the driver must wait for the other one
        int ft = foc.Position > max / 2 ? foc.Position - 400 : foc.Position + 400;
        foc.Move(ft);
        sw.Restart();
        rot.MoveMechanical(mech0);
        Check("rotator move while the focuser moves waits and then runs", rot.IsMoving || AngleDiff(rot.MechanicalPosition, mech0) < 0.05, sw.ElapsedMilliseconds + " ms wait");
        Check("rotator back at mechanical " + mech0, WaitUntil(() => !rot.IsMoving, 15000) && AngleDiff(rot.MechanicalPosition, mech0) < 0.05, rot.MechanicalPosition.ToString());
        Check("focuser reached " + ft + " meanwhile", foc.Position == ft, foc.Position.ToString());
        foc.Move(pos);
        WaitUntil(() => !foc.IsMoving, 15000);

        rot.MoveAbsolute(rot.Position + 10f >= 360f ? rot.Position - 350f : rot.Position + 10f);
        Thread.Sleep(200);
        rot.Halt();
        Check("Rotator Halt stops the move", WaitUntil(() => !rot.IsMoving, 5000), rot.MechanicalPosition.ToString());
        rot.MoveMechanical(mech0);
        WaitUntil(() => !rot.IsMoving, 15000);
        Check("DeviceState has IsMoving, MechanicalPosition, Position, TimeStamp", rot.DeviceState.Count >= 4);

        Console.WriteLine("observing conditions");
        oc.Connected = true;
        Check("ObservingConditions connected", oc.Connected);
        try
        {
            Info("T " + oc.Temperature + " °C, RH " + oc.Humidity + " %, dew " + oc.DewPoint + " °C, age " + oc.TimeSinceLastUpdate("") + " s, " + oc.SensorDescription("Temperature"));
            Check("TimeSinceLastUpdate(Humidity) < 10 s", oc.TimeSinceLastUpdate("Humidity") < 10);
        }
        catch (Exception ex) { Check("environment readable", false, ex.GetType().Name + ": " + ex.Message); }
        Check("AveragePeriod is 0", oc.AveragePeriod == 0);
        Check("AveragePeriod = 1 throws InvalidValueException", Expect<ASCOM.InvalidValueException>(() => oc.AveragePeriod = 1) != null);
        Check("Pressure throws PropertyNotImplementedException", Expect<ASCOM.PropertyNotImplementedException>(() => { var p = oc.Pressure; }) != null);
        Check("SensorDescription(Pressure) throws MethodNotImplementedException", Expect<ASCOM.MethodNotImplementedException>(() => oc.SensorDescription("Pressure")) != null);
        Check("TimeSinceLastUpdate(Bogus) throws InvalidValueException", Expect<ASCOM.InvalidValueException>(() => oc.TimeSinceLastUpdate("Bogus")) != null);

        Console.WriteLine("reference counting");
        foc.Connected = false;
        Check("Focuser disconnected, Rotator still connected", !foc.Connected && rot.Connected && rot.MechanicalPosition >= 0);
        Check("disconnected Focuser: Position throws NotConnectedException", Expect<ASCOM.NotConnectedException>(() => { var p = foc.Position; }) != null);
        Check("disconnected Focuser: Raw returns ERROR: not connected", foc.Action("AstroUniBoard:Raw", ":25#").StartsWith("ERROR: not connected"));
        rot.Sync((float)((rot.MechanicalPosition + offset0) % 360));   // restore the sky offset
        rot.Disconnect();
        oc.Disconnect();
        Check("last client gone: the board connection closes", WaitUntil(() => foc.Action("AstroUniBoard:Snapshot", "").StartsWith("{\"connection\":\"Disconnected\""), 3000));
        foc.Connected = true;
        Check("reconnect after all disconnected", foc.Connected);
        foc.Connected = false;

        foc.Dispose(); rot.Dispose(); oc.Dispose();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
        return failures;
    }
}
