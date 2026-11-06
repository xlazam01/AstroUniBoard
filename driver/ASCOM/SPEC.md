# AstroUniBoard ASCOM drivers — specification

Status (2026-11-06): `AstroUniBoard.Ascom` (LocalServer with Focuser, Rotator, ObservingConditions)
is built. It passes 60 direct checks against the real board (`AstroUniBoard.Ascom.Check`, rev board,
FW2.0.0 on COM9), and ConformU 4.5.2 reports no errors, warnings or issues for all three devices
(`conformance/ConformU-2026-11-06-*.txt`). Not yet tested in NINA (#48). No installer yet (#46).

Copied and adapted from OpenWheel `driver/ASCOM` (LocalServer, settings, setup dialog, check tool).
Plan and decisions: `docs/V2_PLAN.md` §5 (Q18, Q21, Q26, Q28). Protocol: `docs/PROTOCOL.md`.

## 1. Layout

| Project | What |
|---|---|
| `AstroUniBoard.Core` | transport, detection, `BoardController` (no ASCOM dependency), see `README.md` |
| `AstroUniBoard.Ascom` | `AstroUniBoard.Ascom.Server.exe`: COM LocalServer, x86, net48, ASCOM Platform 7 (DeviceInterfaces 6.0 from the GAC) |
| `AstroUniBoard.Ascom.Check` | console tool that drives the driver classes directly (no registration needed) against a real board |
| `conformance/` | ConformU reports |

| ProgID | Interface | CLSID |
|---|---|---|
| `ASCOM.AstroUniBoard.Focuser` | IFocuserV4 | `f6e22b09-e12f-46fb-ac89-26b2c8a63438` |
| `ASCOM.AstroUniBoard.Rotator` | IRotatorV4 | `90a0b732-d5d9-434d-856b-3f1656b1ad5d` |
| `ASCOM.AstroUniBoard.ObservingConditions` | IObservingConditionsV2 | `c2ef9ee5-4fdb-42cd-883d-67e80ddfb367` |

AppID `7dd32772-ad1e-4128-ae85-b7fa07968f2d`. The server exe takes these arguments:
- `/regserver`: register (asks for UAC when needed; `/silent` suppresses the message boxes).
- `/unregserver`: remove the registration.
- no argument: the setup dialog.
- `-Embedding`: started by COM.

Dev registration on the dev PC: `driver/ASCOM/dist/AstroUniBoard.Ascom/` (ignored by git), run from
an administrator terminal. Processes started from the Claude desktop app cannot write HKLM, and
their `%LOCALAPPDATA%` is redirected, so register from your own terminal.

## 2. One process, one port, many clients

- Every COM object of all three types lives in the one server process and uses **one shared
  `BoardController`**. NINA (focuser + rotator + weather), the Secondary Focuser plugin and the
  debug app can all be connected at once. Verified: a second 64-bit COM client held the Focuser and
  moved it while ConformU ran against it (snapshot `clients: 2`).
- Connections are **reference counted** over all objects (and the open setup dialog). The first
  connection rebuilds the controller so changed settings (board choice) take effect. The last
  disconnection closes the port. The server exits 4 s after the last object is released.
- `Connected = true` waits for the board (up to 10 s), then up to 3 s for the settings,
  housekeeping and environment to be cached. It fails if the board lacks the capability the device
  needs (`caps` from `:32#`). `Connect()` does the same asynchronously (`Connecting`).
- Property reads return the controller's cached snapshot (fast, never block). Commands wait at
  most a few seconds.
- The firmware moves one axis at a time (Q12, `ERR,8`). A move on one axis while the other is busy
  **waits** for it (up to 30 s). A new move on an axis that is still moving halts it first.
- Log: `%LOCALAPPDATA%\AstroUniBoard\driver.log` (1 MB, one rotation) plus the in-memory ring used
  by `AstroUniBoard:Log`.

## 3. Interface mapping

### Focuser (IFocuserV4)

| Member | Implementation |
|---|---|
| `Absolute` | true |
| `MaxStep`, `MaxIncrement` | board max position (`:54`) |
| `Position`, `IsMoving` | status (`:33`) |
| `Move(n)` | clamped to `0..max` (`IFocuserV4` allows clamping), `:51`. Also accepted while TempComp is on (see below) |
| `Halt` | `:53` |
| `StepSize` | `:66` / 100 µm; `PropertyNotImplementedException` when the board stores 0 |
| `Temperature` | `:90` (selected SHT31). `PropertyNotImplemented` without the environment capability, `DriverException` while the sensor has no valid reading |
| `TempCompAvailable` | true when the board has the environment capability |
| `TempComp` | host-side compensation, §4 |
| `DeviceState` | IsMoving, Position, Temperature (when valid), TimeStamp |

### Rotator (IRotatorV4)

| Member | Implementation |
|---|---|
| `MechanicalPosition` | board angle from status, 0..360° |
| `Position` | sky angle = mechanical + sky offset, modulo 360 |
| `Sync(sky)` | sets the offset = sky − mechanical. No motion, and nothing is sent to the board. The offset is stored in the ASCOM profile (Rotator ProgID, `SkyOffsetDegrees`) — host only (Q18) |
| `MoveAbsolute(sky)`, `MoveMechanical(mech)` | 0 ≤ value < 360, else `InvalidValueException`. Sends `:71` with the mechanical target in cdeg; the firmware picks the path inside the cable wrap |
| `Move(rel)` | relative to the current sky position (or the running target), any finite amount, result modulo 360 |
| `TargetPosition` | the sky target of the last move (current position before the first one) |
| `Halt` | `:73` |
| `CanReverse`, `Reverse` | true; `:82` / `:83`. Setting Reverse also saves (`:23#`) because ASCOM expects it to persist |
| `StepSize` | 1000 / steps-per-1000° (`:84`), degrees |
| `DeviceState` | IsMoving, MechanicalPosition, Position, TimeStamp |

### ObservingConditions (IObservingConditionsV2)

| Member | Implementation |
|---|---|
| `Temperature`, `Humidity`, `DewPoint` | `:90` (the board prefers the external J9 sensor). `DriverException` while there is no valid reading |
| `AveragePeriod` | 0 only; any other value → `InvalidValueException` |
| `Refresh` | no-op: the board refreshes every 2 s, the driver reads every 5 s |
| `SensorDescription`, `TimeSinceLastUpdate` | for the three properties above (`""` = latest); `MethodNotImplementedException` for the other valid names; `InvalidValueException` for unknown names |
| all other properties | `PropertyNotImplementedException` |
| `DeviceState` | DewPoint, Humidity, Temperature, TimeStamp |

## 4. Host-side temperature compensation (Q21, #44)

- Coefficient in **steps per °C** (signed: positive moves outward when it gets warmer) and a
  threshold in °C (default 0.5). Set in the setup dialog; stored in the ASCOM profile (Focuser ProgID).
- `TempComp = true` takes the current temperature as the reference. Every 2 s, if the board is idle
  and the temperature has moved by at least the threshold, the focuser moves by
  `round(coefficient × Δ)`, clamped to `0..max`, and the reference becomes the new temperature.
  A move the board refuses is retried at the next tick.
- One compensator for all clients. It is switched off when the first client connects and when the
  last one leaves.
- **Moves while TempComp is on are allowed.** The brief for this work said to refuse them, but since
  IFocuserV3 ASCOM requires `Move` to work while `TempComp` is true (only IFocuserV2 threw
  `InvalidOperationException`), and ConformU tests it. Compensation simply continues from the new
  position. With a coefficient of 0 it never moves.

## 5. Action contract (for the control & debug app, #45)

The same handler serves all three devices. Names are case-insensitive and all are listed in
`SupportedActions`.

| Action | Parameter | Returns |
|---|---|---|
| `AstroUniBoard:Raw` | `:NN...#` | the board's reply **without '#'** (`OK`, `ERR,7`, `S,1,...`), or `ERROR: <message>` (not connected, timeout, malformed command). Goes through `BoardController.Raw`, so setting changes refresh the cache and `:22#`/`:31#` trigger the reconnect logic |
| `AstroUniBoard:Snapshot` | — | JSON object below. Also works before `Connected = true` |
| `AstroUniBoard:Log` | last seen `seq`, or empty | JSON array `[{"seq":12,"time":"2026-11-06T14:41:01.694","text":"..."}]` with `seq` > parameter, oldest first (ring of 1000, local time). Also works before `Connected` |
| `AstroUniBoard:HaltAll` | — | `OK` / `ERROR: <message>` (`:30#`, jumps the queue) |
| `AstroUniBoard:ClearError` | — | `OK` / `ERROR: ...` (`:34,0#`) |
| `AstroUniBoard:SaveSettings` | — | `OK` / `ERROR: ...` (`:23#`) |
| `AstroUniBoard:ImuHome` | — | `OK` / `ERROR: ...` (`:77#`) |
| `AstroUniBoard:ImuCalibrate` | — | `OK` / `ERROR: ...` (`:95#`) |
| `AstroUniBoard:Reboot` | — | `OK` / `ERROR: ...` (`:22#`, reconnects to the same serial) |
| `AstroUniBoard:EnterDfu` | — | `OK` / `ERROR: ...` (`:31#`). While clients stay connected, the driver keeps looking for the board and reconnects when it leaves the bootloader |

- Every action except Snapshot and Log needs the calling COM object to be connected; otherwise it
  returns `ERROR: not connected ...` (no exception). An unknown action name throws
  `ActionNotImplementedException`; a non-numeric Log parameter throws `InvalidValueException`.
- `CommandString(":NN#", raw)` is the same as Raw, but throws `DriverException` instead of
  returning `ERROR:`. `CommandBlind` and `CommandBool` are not implemented.

Snapshot (keys are fixed; each object is `null` while unknown):

```json
{"connection":"Connected","port":"COM9","problem":null,
 "identity":{"model":"REV2","firmware":"FW2.0.0","protocol":2,"serial":"2076349A4B31","caps":15},
 "status":{"state":"Idle","stateCode":1,"focuserPosition":1050,"focuserTarget":-1,"rotatorAngle":0,"rotatorTarget":-1,
           "error":0,"errorText":null,"flags":96,"timeUtc":"2026-11-06T13:40:04.987Z"},
 "housekeeping":{"mcuTemperature":42.2,"vdda":3.307,"driverTemperatureClass":0,"focuserDriverTemperatureClass":0,
                 "rotatorDriverTemperatureClass":0,"warnings":0,"warningsText":null},
 "environment":{"source":2,"temperature":26,"humidity":42,"dewPoint":12.1,"timeUtc":"2026-11-06T13:40:04.987Z"},
 "focuser":{"maxPosition":8100,"speed":900,"acceleration":2000,"backlash":0,"reverse":false,"stepSizeCentiMicrons":111},
 "rotator":{"wrapMinCdeg":-18000,"wrapMaxCdeg":18000,"speedCdegPerS":1200,"accelerationCdegPerS2":2400,"reverse":false,
            "stepsPerKiloDegree":88889,"bootHome":false,"homeToleranceCdeg":50},
 "host":{"driverVersion":"0.1","clients":1,"skyOffsetDegrees":0,"tempComp":false,"tempCompStepsPerDegree":0,"tempCompThresholdDegrees":0.5}}
```

- `connection` is one of Disconnected, Connecting, Connected, Reconnecting.
- `state` is the `BoardState` name and `stateCode` its number.
- `rotatorAngle` and `rotatorTarget` are in cdeg; `rotatorTarget` is −1 when there is none.
- `flags` and `caps` are ints; the bits are in PROTOCOL.md §3.1 and §3.2.

## 6. Setup dialog

One dialog for all three devices (the same one from NINA's setup button of any of them):
- **Board**: the 1209:0001 candidates by USB serial; **Identify** asks every board not in use for its
  `:32#` identity. Manual COM port as a fallback. A changed choice takes effect when all clients
  have disconnected.
- **Live status**: positions, sky/mechanical angle, environment, MCU and supply, IMU, error.
- **Focuser** and **Rotator** board settings (max, speed, acceleration, backlash, step size,
  reverse; speed, acceleration, steps per degree, cable wrap, home tolerance, reverse, boot home).
  **Write to board** sends only the changed values, then `:23#`.
- **This PC**: rotator sky offset; temperature-compensation coefficient and threshold.
- Halt all and Clear error buttons.
- **IMU calibration / debug app...** starts `AstroUniBoard.DebugApp.exe --connect --tab imu` from the
  server exe's folder, where the installer (#46) puts it, and does not wait for it. The app
  connects back through this LocalServer as one more client, so the IMU zero calibration can be
  reached from N.I.N.A.'s setup button while N.I.N.A. holds the driver. Calls from other clients are
  served while the modal dialog is open. If the exe is missing, a message says where it is expected.

## 7. Settings (ASCOM profile)

| Key | ProgID | Meaning |
|---|---|---|
| `PreferredUsbSerial` | Focuser | board to use (empty = first one found) |
| `ManualPort` | Focuser | fixed COM port; still has to pass identify |
| `TempCompStepsPerDegree`, `TempCompThresholdDegrees` | Focuser | §4 |
| `SkyOffsetDegrees` | Rotator | sky offset from Sync (Q18) |

Everything else (speeds, limits, reverse, wrap) lives in the board's EEPROM.
