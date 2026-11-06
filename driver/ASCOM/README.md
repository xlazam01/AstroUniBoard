# AstroUniBoard host software (Windows)

Copied and adapted from OpenWheel `driver/ASCOM` (decision: copy & adapt, see `docs/adr`).

| Project | What |
|---|---|
| `AstroUniBoard.Core` | net48, no ASCOM dependency: serial transport, USB detection (VID/PID + `:32#` identify), the worker that owns the port (`BoardController`), protocol 2.0 parsers |
| `AstroUniBoard.Core.Tests` | xUnit tests against an in-memory fake board that misbehaves: silence, garbage, partial lines, floods, late replies, unplug, identify gate re-lock, a different board after a reboot |
| `AstroUniBoard.DebugApp` | WinForms control & debug app (#45): through the ASCOM driver's Action contract or directly on the port. See its [README](AstroUniBoard.DebugApp/README.md) |
| `AstroUniBoard.DebugApp.Tests` | the app's ASCOM path against a fake driver and a loopback over `FakeBoard`; opt-in hardware tests |
| `AstroUniBoard.Ascom` | ASCOM LocalServer `AstroUniBoard.Ascom.Server.exe`: Focuser (IFocuserV4), Rotator (IRotatorV4), ObservingConditions (IObservingConditionsV2) on one port, setup dialog, Action contract for the debug app — see `SPEC.md` |
| `AstroUniBoard.Ascom.Check` | direct checks of the driver classes against a real board (no registration needed) |
| `conformance/` | ConformU reports |

Planned next to these: the Inno Setup installer (`docs/V2_PLAN.md` §5).

## BoardController in short

- One instance owns the COM port; every public member is thread-safe and returns within a bounded time.
- `Snapshot` is an immutable view (status, housekeeping, environment, cached focuser/rotator settings),
  refreshed every 1 s idle / 200 ms while moving; `SnapshotChanged` fires on each update.
- Detection: USB 1209:0001 candidates (shared pid.codes test PID — an OpenWheel on the same PID is
  rejected by identify), preferred USB serial first. Once connected, it only ever reconnects to the
  same serial (after unplug, reboot or `:22#`).
- Identify gate (protocol 2.0): an `ERR,16` reply (another program opened the port) is answered by
  `:32#` — same serial only — and the command is repeated once.
- Three timeouts/garbage replies in a row → `Reconnecting` with backoff. A move busy longer than
  `MoveTimeoutMs` (IMU: `ImuTimeoutMs`) is halted by the driver.
- Focuser targets outside `0..max` are rejected before sending (the ASCOM layer clamps, as
  `IFocuserV4.Move` allows).

## Build and test

```
dotnet test driver/ASCOM/AstroUniBoard.sln
```

Needs the .NET SDK (any recent) and the .NET Framework 4.8 targeting pack (Windows).
