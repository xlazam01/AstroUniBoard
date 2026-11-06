# AstroUniBoard control & debug app (#45)

WinForms, net48, no extra assemblies: `AstroUniBoard.DebugApp.exe` + `AstroUniBoard.Core.dll`.
JSON comes from `System.Web.Extensions` (`JavaScriptSerializer`), which is part of .NET Framework 4.8,
so the installer has nothing extra to ship. dfu-util is optional (see Firmware).

## Connection

| Choice | How | When |
|---|---|---|
| **ASCOM driver** (default) | late-bound COM to `ASCOM.AstroUniBoard.Focuser`, only the `AstroUniBoard:*` Action contract below | on the rig: works while N.I.N.A. is connected to the same LocalServer |
| **Direct** (automatic or a COM port) | its own `BoardController` on the port | bench work, no driver installed. Takes the port: N.I.N.A. cannot connect meanwhile |
| Loopback (`--loopback`) | the ASCOM code path over an in-process copy of the contract (`LoopbackDriver.cs`) | development, before the LocalServer is installed |

Both go through `IBoardSession` (`Sessions.cs`). Moves use raw commands (`:51 :52 :71 :72`), so they
behave the same on both paths; per-axis halts are `:53#`/`:73#` (direct: the controller's urgent halt),
**HALT ALL** is `AstroUniBoard:HaltAll`.

### Action contract used (the LocalServer implements it)

| Action | Param | Returns |
|---|---|---|
| `AstroUniBoard:Raw` | `:NN...#` | reply without `#` (`OK`, `ERR,7`, `S,1,...`) or `ERROR: <message>` |
| `AstroUniBoard:Snapshot` | — | JSON: `connection`, `port`, `problem`, `identity`, `status`, `housekeeping`, `environment`, `focuser`, `rotator` (Core property names; keys case-insensitive, enums as int or name) |
| `AstroUniBoard:Log` | last seen seq or empty | JSON `[{"seq":12,"time":"2026-11-06T13:30:01.123Z","text":"..."}]` |
| `AstroUniBoard:HaltAll` `:ClearError` `:SaveSettings` `:ImuHome` `:ImuCalibrate` `:Reboot` `:EnterDfu` | — | `OK` / `ERROR: <message>` |

`LoopbackDriver.cs` is the reference implementation; `SnapshotJson.Build` makes the snapshot object.
On connect the app checks that `SupportedActions` contains `AstroUniBoard:Snapshot`.

## Tabs

| Tab | What |
|---|---|
| Status | positions, state, error with text + Clear, decoded flags, housekeeping `:40#`, reset info `:46#`, Save `:23#`, Reboot `:22#` |
| Focuser | jog ±10/100/1000 or any, absolute move, halt, sync `:56`; settings `:54..:67` edit + Apply, Save, Defaults `:24#` |
| Rotator | jog ±0.1/1/10/90° or any, mechanical angle `:71`, go home, IMU home `:77#`, halt, sync `:76`, set home `:76,0#`; settings incl. cable wrap `:75` |
| Motors / StallGuard | TMC2209 config `:47`/`:48` per axis; StallGuard `:41` with suggested threshold, set `:42` |
| IMU | live `:94#`, stored calibration `:96#`, IMU home, guided zero calibration `:95#` with pre-checks (IMU answering, tilt, cable wrap headroom, idle) and result |
| Environment | `:90#`, both SHT31 `:91#` with status decoded, sensor selection `:92#`/`:93` |
| Console | raw commands, history (Up/Down), `ERR,n` explained, structured replies decoded; every command any tab sends is echoed |
| Log | driver log (or the Core log, direct) + what the app sent; export to a text file |
| Firmware | DFU update: image check (≤ 128 KB), no other bootloader present, `:31#`, wait for `0483:df11`, `dfu-util -d 0483:df11 -a 0 -s 0x08000000:leave -D <bin>`, reconnect, compare firmware/serial — the flow of `fw-onstepx/tools/firmware_manager` (PR #62). "Only enter DFU" for other tools |

dfu-util is looked up next to the exe, on PATH, then in `~/.platformio/packages/tool-dfuutil/bin`.
The installer can ship `dfu-util.exe` + `libusb-1.0.dll` next to the app (GPL-2.0; ship its source
offer). The STM32 bootloader needs the WinUSB driver once (Zadig).

## Command line

```
AstroUniBoard.DebugApp.exe [--port COM9] [--connect] [--tab imu] [--loopback] [--screenshots <folder>]
```

`--tab` opens on a tab by the first word of its title (`status`, `focuser`, `rotator`, `motors`, `imu`,
`environment`, `console`, `log`, `firmware`). The driver's Setup dialog starts the app with
`--connect --tab imu` for the IMU calibration, so the installer must put `AstroUniBoard.DebugApp.exe`
next to `AstroUniBoard.Ascom.Server.exe`.
`--screenshots` connects, saves a PNG of every tab and exits (used for the docs).

## Tests

`AstroUniBoard.DebugApp.Tests`: snapshot JSON parsing, the ASCOM path against a fake driver and
against the loopback over Core's in-memory `FakeBoard` (with an OpenWheel on the shared PID that must
be skipped). Hardware tests run only with `ASTROUNIBOARD_PORT=COM9` (or `auto`): small focuser and
rotator moves on both paths; `ASTROUNIBOARD_DFU_IMAGE=<bin>` additionally flashes that image.
`ASTROUNIBOARD_ASCOM=1` checks the installed LocalServer read-only (no moves) next to any other
connected client, and that closing the app leaves that client connected.
`ASTROUNIBOARD_ASCOM=moves` (no other client): small moves, a halted focuser move and IMU home through
the installed driver.

```
dotnet test driver/ASCOM/AstroUniBoard.sln
```

![Status](../../../docs/img/debug-app/01-status.png)
