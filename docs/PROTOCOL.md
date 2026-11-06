# Serial protocol — AstroUniBoard firmware V2

Status: **2.0 (2026-11-06)**. Changes since 1.0: identify gate, `ERR,16` (#58); IMU zero calibration
from three captures (#32). 1.0 covered framing, USB identity, common block 20–49 (#7), focuser 50–69
(#8), rotator 70–89 (#9), environment / IMU 90–99 (#10) and error codes (#11). Changes go through a PR
and raise the protocol version when incompatible.

The protocol is a member of the **OpenWheel family**: same framing, same reply style, same numbers
and reply formats for the common commands, so the host `*.Core` libraries (transport, detection,
worker, circuit breaker) can share code. Reference: OpenWheel `docs/PROTOCOL.md` (protocol 2).
Differences from OpenWheel are listed in [§7](#7-differences-from-openwheel).

Protocol version reported by identify: **2** (raised on any incompatible change).

## 1. Transport

- USB CDC serial, native USB of the STM32G0B1. 115200 8N1 is nominal (ignored over USB).
- The firmware does not gate output on DTR and does not reset on DTR.
- USB identity: VID **0x1209** (pid.codes), PID **0x0001** (pid.codes test PID) until AstroUniBoard has
  its own (#36, #37). Product string `AstroUniBoard Focuser Rotator`. Serial number: from the MCU
  unique ID, 12 hex digits (stm32duino USB core: UID0+UID2 as 8 digits, then the top 16 bits of
  UID1) — the same string identify returns.
- The device never sends anything unsolicited.

## 2. Framing

- A command is `:NN[params]#`: `:` starts it, `NN` is a two-digit decimal command number, `#` ends it.
- Bytes outside `:...#` are ignored. A `:` inside a command restarts it.
- At most **46 characters** between `:` and `#`; a longer line is answered `ERR,11`.
- Parameters follow `NN` directly or after a space or comma: `:41F#`, `:41 F#` and `:41,F#` are the
  same. Several values are comma separated: `:42,F,40#`.
- Axis selector in common commands: `F` focuser, `R` rotator (upper or lower case).
- Hosts send **one command at a time** and wait for the answer (timeout ~1 s; 2 s for `:23#`), and
  never burst more than ~64 bytes — a lost `#` means no reply, so a timeout is always needed.

### Identify first (#58)

USB VID/PID 1209:0001 is the shared pid.codes test PID (OpenWheel and other devices use it too), so
the firmware acts on nothing before the host has identified it:

- After power-on or any reset only `:25#` (ping) and `:32#` (identify) are accepted. Every other
  command answers **`ERR,16`** and does nothing.
- An answered `:32#` unlocks all commands until the next reset. Opening or closing the port does not
  lock again: a port open cannot be told apart from a host changing the port settings (both send
  SET_LINE_CODING, e.g. pyserial on every timeout change; measured 2026-11-06).
- Hosts must identify every port before sending anything else and check the `ASTROUNIBOARD` prefix
  and the protocol version. After a reboot they reconnect to the same USB serial number, never to
  "the first 1209:0001 port".

Same rule and error number in OpenWheel (xlazam01/OpenWheel#16).

### Replies

**Every command is answered** with exactly one line ending in `#`, written in one USB transfer:

| Answer | Meaning |
|---|---|
| `<value>#` | query result: a number, text, or a tagged comma separated record (`S,...#`, `H,...#`) |
| `OK#` | set / action accepted |
| `ERR,<code>#` | rejected or failed, see [§6](#6-error-codes) |

A move answers `OK#` as soon as it has started; completion is polled with `:33#`.

### Units

| Quantity | Unit |
|---|---|
| focuser position | steps, signed 32 bit |
| rotator angle | centidegrees (0.01°), mechanical angle reported 0..35999 |
| temperature | 0.1 °C |
| humidity | 0.1 %RH |
| voltage | mV |

### Settings and EEPROM

Setters change the **running value only**. `:23#` writes all settings to the 24AA64 EEPROM
(record with magic + CRC, #17). This differs from OpenWheel, where some setters save at once.
Exception: focuser and rotator positions are saved by the firmware itself after 2 s idle (Q15) and
never need `:23#`.

## 3. Common block (20–49)

Numbers 00–19 are not used (`ERR,10`), so a host that probes the board as an OpenWheel fails cleanly.

| Cmd | Name | Params | Answer | fw |
|---|---|---|---|---|
| 20 | — (OpenWheel: home) | | `ERR,10` | |
| 21 | — (OpenWheel: move by steps) | | `ERR,10` | |
| 22 | reboot | | `OK#`, then resets | ✓ |
| 23 | write settings to EEPROM | | `OK#` / `ERR,4` | |
| 24 | reset settings to defaults (running values; `:23#` to keep) | | `OK#` / `ERR,8` | |
| 25 | ping | | `OK#` | ✓ |
| 26–29 | — | | `ERR,10` | |
| 30 | halt **both** axes (decelerate) | | `OK#` | |
| 31 | reboot into the USB DFU bootloader | | `OK#`, then resets | ✓ |
| 32 | identify | | see 3.1 | |
| 33 | status | | see 3.2 | |
| 34 | read / clear sticky error | none / `0` | `E,<code>#` / `OK#` | |
| 35–39 | — | | `ERR,10` | |
| 40 | housekeeping | | see 3.3 | |
| 41 | StallGuard readings of one axis | `F`/`R` | see 3.4 | |
| 42 | set StallGuard threshold | `F`/`R`,0..255 (0 off) | `OK#` | |
| 43 | get status LED brightness | | 0..100 | |
| 44 | set status LED brightness | 0..100 %, 0 = off | `OK#` | |
| 45 | — (OpenWheel: Hall sensor) | | `ERR,10` | |
| 46 | reset and safe-mode info | | see 3.5 | (as `:99#`) |
| 47 | motor driver config of one axis | `F`/`R` | see 3.6 | |
| 48 | set motor driver config | `F`/`R`,run,hold,usteps,mode,idle_off | `OK#` | |
| 49 | reserved for the common block | | `ERR,10` | |

`:22#` and `:31#` answer `OK#`, flush, wait 50 ms so the host can read the reply, then reset.
`:31#` stores a magic word in TAMP backup register 0 and resets; the boot code jumps to the
STM32 system bootloader before clocks and USB start (`fw/src/bootloader.cpp`). The board comes
back as `0483:DF11` (STM32 BOOTLOADER); after the download it starts the firmware again.

`:24#`, `:42`, `:44` and `:48` answer `ERR,8` while an axis moves.

### 3.1 Identify (`:32#`)

```
ASTROUNIBOARD,<model>,<firmware>,<protocol>,<serial>,<caps>#
ASTROUNIBOARD,REV2,FW2.0.0,1,3A00FF12ABCD,F#
```

| Field | Values |
|---|---|
| model | board family the firmware is built for: `REV2` |
| firmware | `FW<major>.<minor>.<patch>` |
| protocol | this document's protocol version (integer) |
| serial | MCU unique ID, same as the USB serial number |
| caps | hex bitmask of what this **firmware build** supports: bit0 focuser, bit1 rotator, bit2 IMU, bit3 environment (SHT31) |

Whether a sensor actually answered is in the status flags (3.2), not in caps. A host accepts a port
only if the USB VID/PID match **and** this line parses with a supported protocol version. Answering
`:32#` also unlocks the other commands (see "Identify first" in §2).

### 3.2 Status (`:33#`)

One atomic line with everything a driver polls:

```
S,<state>,<foc_pos>,<foc_target>,<rot_cdeg>,<rot_target_cdeg>,<error>,<flags>#
S,1,12500,-1,9000,-1,0,30#
```

| Field | Values |
|---|---|
| state | 0 init, 1 idle, 2 focuser moving, 3 rotator moving, 4 rotator IMU homing or IMU calibration, 5 halting |
| foc_pos | focuser position in steps |
| foc_target | target of the running focuser move, else `-1` |
| rot_cdeg | rotator mechanical angle 0..35999 |
| rot_target_cdeg | target of the running rotator move, else `-1` |
| error | sticky error code (0 none), same as `:34#` |
| flags | hex bitmask, below |

| Flag bit | Meaning |
|---|---|
| 0 | focuser position unverified (power lost while moving or never saved) |
| 1 | rotator angle unverified (not homed since boot, Q20) |
| 2 | EEPROM fault (missing, bad CRC → defaults in use) |
| 3 | housekeeping warning (see `:40#`) |
| 4 | on-board SHT31 has a valid reading |
| 5 | external SHT31 (J9) has a valid reading |
| 6 | IMU answering |
| 7 | IMU zero not calibrated |
| 8 | safe mode: started after a watchdog reset with sensors disabled (see `:46#`) |

Only one axis moves at a time (Q12): a move command while state is 2–5 answers `ERR,8`.

### 3.3 Housekeeping (`:40#`)

Same first six fields as OpenWheel, so the shared parser works; two per-driver fields are appended.

```
H,<mcu_temp>,<vdda>,<v5>,<v5_min>,<driver_temp>,<warnings>,<foc_driver_temp>,<rot_driver_temp>#
H,312,3297,-1,-1,0,0,0,0#
```

| Field | Values |
|---|---|
| mcu_temp | MCU die temperature, 0.1 °C (internal sensor, factory calibrated; reads above ambient) |
| vdda | MCU supply, mV, against the internal reference |
| v5, v5_min | always `-1`: rev 2.0 has no voltage-sense divider (Q24) |
| driver_temp | worst of the two TMC2209 temperature classes: `0` below 120 °C, else `120`/`143`/`150`/`157`; `-1` a driver does not answer |
| warnings | hex bitmask: bit1 TMC2209 overtemperature prewarning, bit2 a TMC2209 was reset by a supply dip and re-initialised (cleared with `:34,0#`), bit3 crystal not running, clocks from the trimmed HSI48. bit0 (+5V low) is never set |
| foc_driver_temp, rot_driver_temp | the same class per driver |

Driver temperatures are read while idle.

### 3.4 StallGuard (`:41`, `:42`)

`:41,F#` → `G,<threshold>,<last>,<min>,<max>#`: the axis' threshold and the `SG_RESULT` readings
(0..510, lower = more load) of its last move, `-1` if none. Only readings the stall check uses are
counted: after the first 200 ms of a move and above the StallGuard minimum speed of 100 full steps/s.
With a non-zero threshold, such a reading at or below `2 × threshold` stops the move with sticky
error 13. Default 0 (off) on both axes.
Tuning as OpenWheel: run a few normal moves, take the lowest `min`, set about a third of it.
Measured on the rev 1 test board (2026-11-06): the focuser at 900 steps/s reads 48..136 while
cruising, but a motor held by hand did not pull the reading below 46, so detection of a light block is
not reliable at the default speed and current. The rotator at its default 12°/s (16 microsteps)
runs below the minimum speed: StallGuard on the rotator needs 18°/s or more.

### 3.5 Reset info (`:46#`)

```
R,<reset_flags>,<last_marker>,<safe_mode>#
```

| Field | Values |
|---|---|
| reset_flags | `RCC_CSR >> 24` of the last reset, hex (bit7 low-power, bit6 window WDG, bit5 IWDG, bit4 software, bit3 power-on/BOR, bit2 pin, bit1 option-byte load) |
| last_marker | progress marker the firmware stored before the reset (where a watchdog reset hit) |
| safe_mode | `1` when this boot started without sensors after a watchdog reset, else `0` |

Replaces the bring-up command `:99#` (which also prints a debug note; that field is dropped).

### 3.6 Motor driver config (`:47`, `:48`)

Per-axis TMC2209 settings (Q13), same command for focuser and rotator:

```
:47,F#                    → D,<run_mA>,<hold_mA>,<microsteps>,<mode>,<idle_off_s>#
:48,F,900,450,4,1,30#     → OK#
```

| Field | Values |
|---|---|
| run_mA | RMS run current, mA; upper limit set by the board's sense resistors (#24) |
| hold_mA | RMS standstill current, mA, ≤ run |
| microsteps | 1, 2, 4, …, 256 |
| mode | 0 StealthChop (quiet; required for StallGuard), 1 SpreadCycle |
| idle_off_s | driver disabled this many seconds after a move; 0 = stays powered at hold current |

`:48` needs all five values. Changing **microsteps** rescales every value of that axis counted in
steps (focuser position, max, backlash, speed, acceleration; rotator steps/degree) so the physical
values stay the same; hosts re-read them afterwards. A StallGuard threshold on an axis in
SpreadCycle has no effect.

## 4. Device blocks

| Range | Block | Spec |
|---|---|---|
| 50–69 | focuser, [§4.1](#41-focuser-5069) | #8 |
| 70–89 | rotator, [§4.2](#42-rotator-7089) | #9 |
| 90–99 | environment and IMU, [§4.3](#43-environment-and-imu-9099) | #10 |

### 4.1 Focuser (50–69)

Position unit: one **step** = one microstep at the axis' current microstep setting (`:47,F#`).
Valid positions are `0..max` (soft limits, Q16); there is no homing and no limit switch.
Get/set pairs use even/odd numbers as OpenWheel does.

| Cmd | Name | Params | Answer |
|---|---|---|---|
| 50 | position | | steps |
| 51 | move to absolute position | steps, `0..max` | `OK#` / `ERR,7` / `ERR,8` |
| 52 | move relative | ±steps, target must be in `0..max` | `OK#` / `ERR,7` / `ERR,8` |
| 53 | halt the focuser (decelerate) | | `OK#` |
| 54 | get max position | | steps |
| 55 | set max position | 1..10000000, ≥ current position | `OK#` / `ERR,7` |
| 56 | set current position (sync, no motion) | `0..max` | `OK#` / `ERR,7` / `ERR,8` |
| 57 | — reserved | | `ERR,10` |
| 58 | get speed | | steps/s |
| 59 | set speed | 1..20000 steps/s | `OK#` / `ERR,7` |
| 60 | get acceleration | | steps/s² |
| 61 | set acceleration | 1..100000 steps/s² | `OK#` / `ERR,7` |
| 62 | get backlash | | steps |
| 63 | set backlash | 0..10000, 0 = off | `OK#` / `ERR,7` |
| 64 | get reverse | | `0` / `1` |
| 65 | set reverse | `0` / `1` | `OK#` / `ERR,8` |
| 66 | get step size | | 0.01 µm per step, `0` = unknown |
| 67 | set step size | 0..100000 | `OK#` / `ERR,7` |
| 68–69 | — reserved | | `ERR,10` |

Rules:
- **Moves.** `:51`/`:52` answer `OK#` when the move has started; status shows state 2 and
  `foc_target` until it ends. A target equal to the position answers `OK#` without motion. The
  firmware never clamps: a target outside `0..max` is `ERR,7` (ASCOM drivers clamp before sending,
  as `IFocuserV4.Move` allows). Any move while either axis is busy is `ERR,8` (Q12).
- **Halt.** `:53` (and `:30`) decelerate; state 5 until stopped. The position is exact afterwards.
- **Sync** `:56` sets the position counter without motion and clears status flag 0
  (position unverified). Use it after a power loss or to define a new zero.
- **Backlash** is taken up on a **direction change**: the firmware runs `backlash` extra steps first,
  which the position counter does not count. If the host compensates itself (NINA's overshoot
  method), leave it at 0 so the two do not add up.
- **Reverse** flips the motor direction only; the position and limits keep their meaning.
- **Step size** is not used by the firmware. It is stored so that `IFocuserV4.StepSize` and the
  guide-focuser plugin read the same value on any PC; 0 makes ASCOM report StepSize as not
  implemented.
- **Persistence (Q15).** The position is written to the EEPROM 2 s after the focuser stops. A move
  that is cut by a power loss leaves the last saved position and sets status flag 0 at the next boot.
  All other values here are saved with `:23#`.
- **Stall.** With a StallGuard threshold (`:42,F,<thr>#`) the focuser stops on a blocked drawtube with
  sticky error 13; the position stays counted but status flag 0 is set.

Defaults (from the V1 OnStepX setup on Triton; until changed and saved):

| Setting | Default |
|---|---|
| microsteps | 4 |
| run / hold current | 900 / 450 mA |
| mode | StealthChop (V1 ran SpreadCycle; StealthChop is needed for StallGuard) |
| idle_off | 30 s |
| max | 8100 steps (9 mm at 0.9 steps/µm) |
| speed | 900 steps/s (1 mm/s) |
| acceleration | 2000 steps/s² |
| backlash | 0 |
| step size | 111 (1.11 µm per step) |

Example session:

```
:50#          → 4000#
:51,4500#     → OK#
:33#          → S,2,4120,4500,9000,-1,0,30#
:33#          → S,1,4500,-1,9000,-1,0,30#
:52,-20000#   → ERR,7#        (target -15500 below 0)
:56,0#        → OK#           (sync; clears "position unverified")
```

### 4.2 Rotator (70–89)

**Angle convention** (from `fw-onstepx/TESTPLAN_RotatorHoming.md`):

| Mechanical angle | Reported (status, 0..35999) | Camera |
|---|---|---|
| 0 | 0 | landscape, upright — home |
| +90° | 9000 | portrait, rotated CCW |
| ±180° | 18000 | landscape, upside down |
| −90° | 27000 | portrait, rotated CW |

The firmware keeps a **signed mechanical angle** in centidegrees, limited by the cable-wrap range
`min..max` (default −18000..+18000, Q17). Status reports it as 0..35999 (ASCOM `MechanicalPosition`);
`:70#` gives the signed value. The sky angle (ASCOM `Sync` offset) is host-side only (Q18).

**Cable-wrap rule.** A move never leaves `min..max`. `:71` takes a target 0..35999 and picks the
mechanical angle `t` or `t − 36000` (or `t + 36000`) that lies inside the range; when more than one
does, the one nearest the current angle. So with the default range, 0° → 270° goes CW via −90°, and
from +170° to 190° goes through 0° to −170°. A target with no candidate inside the range is `ERR,7`.

| Cmd | Name | Params | Answer |
|---|---|---|---|
| 70 | mechanical angle, signed | | cdeg, e.g. `-9000` |
| 71 | move to angle (cable-wrap rule) | 0..35999 | `OK#` / `ERR,7` / `ERR,8` |
| 72 | move relative | ±cdeg, target inside `min..max` | `OK#` / `ERR,7` / `ERR,8` |
| 73 | halt the rotator (decelerate) | | `OK#` |
| 74 | get cable-wrap range | | `<min>,<max>` |
| 75 | set cable-wrap range | min −36000..0, max 0..36000, current angle inside | `OK#` / `ERR,7` / `ERR,8` |
| 76 | set mechanical angle (sync, no motion) | signed cdeg inside `min..max` | `OK#` / `ERR,7` / `ERR,8` |
| 77 | IMU home: rotate to the IMU zero, then set the angle to 0 | | `OK#` / `ERR,9` / `ERR,14` / `ERR,15` / `ERR,8` |
| 78 | get speed | | cdeg/s |
| 79 | set speed | 10..3600 cdeg/s | `OK#` / `ERR,7` |
| 80 | get acceleration | | cdeg/s² |
| 81 | set acceleration | 10..36000 cdeg/s² | `OK#` / `ERR,7` |
| 82 | get reverse | | `0` / `1` |
| 83 | set reverse | `0` / `1` | `OK#` / `ERR,8` |
| 84 | get steps per degree | | microsteps per 1000°, e.g. `88889` |
| 85 | set steps per degree | 1000..10000000 | `OK#` / `ERR,7` / `ERR,8` |
| 86 | get boot home | | `0` / `1` |
| 87 | set boot home (IMU home after power-on, Q20) | `0` / `1` | `OK#` |
| 88 | get home tolerance | | cdeg |
| 89 | set home tolerance | 5..500 cdeg | `OK#` / `ERR,7` |

Rules:
- **Moves** behave like focuser moves: `OK#` when started, state 3 and `rot_target_cdeg` in status
  until done, `ERR,8` while any axis is busy, no clamping.
- **Set home** is `:76,0#`: the current orientation becomes 0 and status flag 1 (angle unverified) clears.
  **Go home** is `:71,0#` — with the wrap rule it always unwinds.
- **IMU home** `:77` (state 4) needs the IMU zero calibration (`:95`, else `ERR,9`) and a usable
  gravity direction (`ERR,15` when the optical axis is within ~20° of vertical; homing works at any
  other altitude). It moves toward the IMU zero along the unwinding path, re-reads the IMU after the
  rotator settles and corrects, up to 3 passes, until within the home tolerance, then sets the angle
  to 0 and clears flag 1. More passes than that → sticky error 1.
- **Boot home** (default off): if on, the firmware runs `:77` once after power-on when the IMU is
  calibrated and usable; otherwise the angle stays at the saved value with flag 1 set.
- **Persistence** as the focuser: the angle is saved 2 s after a move; a power loss mid-move keeps
  the last saved angle and sets flag 1. Other values are saved with `:23#`.

Defaults (V1 OnStepX config, Triton rotator: 200-step motor, 10:1 belt):

| Setting | Default |
|---|---|
| microsteps | 16 |
| run / hold current | 650 / 325 mA |
| mode | StealthChop |
| idle_off | 0 (stays powered: holds the camera angle) |
| steps per degree | 88889 (88.889 /°) |
| speed | 1200 cdeg/s (12°/s) |
| acceleration | 2400 cdeg/s² |
| cable-wrap range | −18000..+18000 |
| boot home | 0 |
| home tolerance | 50 (0.5°) |

Example:

```
:70#          → 17000#                 (+170°)
:71,19000#    → OK#                    (190° = −170°, goes back through 0)
:33#          → S,3,4500,-1,12000,19000,0,30#
:77#          → OK#                    (IMU home)
:33#          → S,1,4500,-1,0,-1,0,30#
```

### 4.3 Environment and IMU (90–99)

| Cmd | Name | Params | Answer |
|---|---|---|---|
| 90 | environment (selected sensor) | | `W,<src>,<t>,<rh>,<dew>#` / `ERR,14` |
| 91 | both SHT31 sensors | | `V,<st>,<t>,<rh>,<dew>,<st>,<t>,<rh>,<dew>,<power_cycles>#` |
| 92 | get sensor selection | | `0` auto, `1` on-board, `2` external |
| 93 | set sensor selection | `0` / `1` / `2` | `OK#` / `ERR,7` |
| 94 | IMU reading | | `I,<roll>,<tilt>,<ax>,<ay>,<az>#` / `ERR,14` |
| 95 | IMU zero calibration (automatic, moves the rotator) | | `OK#` / `ERR,8` / `ERR,14` / `ERR,15` |
| 96 | IMU calibration data | | `C,<valid>,<g0x>,<g0y>,<g0z>,<nx>,<ny>,<nz>,<cx>,<cy>,<cz>#` |
| 97–99 | — reserved | | `ERR,10` |

**Environment.** `t` and `dew` in 0.1 °C, `rh` in 0.1 %RH. `:90` returns the reading the drivers
use (ASCOM ObservingConditions, INDI WEATHER, host temperature compensation): `src` 1 on-board, 2
external. Auto (default) prefers the external J9 sensor — the on-board one sits next to the motor
drivers and reads warm. `:91` is the detailed view for the debug app: on-board group first, then
external; `st` 0 valid, 1 no answer (absent, unpowered or bus fault), 2 CRC error, 3 no measurement yet since boot (`ShtStatus` in `fw/src/env_sht31.h`); values are the last good
reading; `power_cycles` counts supply restarts of the on-board sensor. Readings are refreshed every
2 s. No temperature compensation in the firmware (Q21).

**IMU** (MPU-6000 on I2C 0x68, on the board that rotates with the camera, Q19).
- `:94`: `roll` = camera roll relative to the IMU zero, signed cdeg (`-99999` when not calibrated or
  the optical axis is too steep); `tilt` = angle between the optical axis and vertical, cdeg
  (`-99999` when not calibrated); `ax ay az` = filtered acceleration in mg, chip axes.
- `:95` (Q19b) — set the camera **landscape, upright**, scope not near the zenith, then send `:95#`.
  The firmware records gravity `g0`, rotates the rotator +90° and records `g90`, then +180° and
  records `g180`, and returns to the start. Gravity moves on a circle around the rotation axis, so
  the axis is the normal of the plane through the three points, `n = (g90 − g0) × (g180 − g90)`
  (normalised). This is exact at **any elevation** of the optical axis (protocol 1 used `g0 × g90`,
  exact only with the scope horizontal) and works for any mounting of the board (Triton: upside
  down). The moves need 180° of cable wrap above the current angle, else `ERR,7` (sync with `:76`
  first if the angle is wrong). State 4 while it runs (about 35 s at the default speed). It fails
  with sticky error 3 when a step is not about 90° (70..110°: rotator slipped, IMU moved). On success
  `g0` and `n` are written to the EEPROM at once (not waiting for `:23#`), status flag 7 clears and
  the current angle is set to 0.
- The three captures also give the centre `c` of the gravity circle. Its part in the plane normal to
  `n` is the accelerometer offset in that plane (rejected above 300 mg). Roll and tilt are measured
  from `c`, so an offset no longer bends the roll away from zero. The offset along `n` cannot be seen
  by rotating and stays in the tilt (about 2° per 40 mg).
- Roll is the signed angle from `g0 − c` to the current gravity `− c`, both projected onto the plane
  normal to `n`, sign by `n`. It is meaningful only when gravity has a component in that plane: within ~20° of
  vertical, `:77` and `:95` answer `ERR,15`.
- `:96` returns the stored calibration (`valid` 0/1, `g0` in mg, `n` in 1/10000, `c` in mg; `c` is
  0 in a calibration stored by an older firmware).

Example:

```
:90#          → W,2,85,812,54#         (external: 8.5 °C, 81.2 %RH, dew point 5.4 °C)
:94#          → I,-120,6230,12,-998,41#
```

## 5. Typical host sequence

1. Find ports with VID 0x1209 / PID 0x0001 (later the assigned PID).
2. `:32#` first, before any other command (a device that is not identified answers `ERR,16`) →
   check the `ASTROUNIBOARD` prefix and protocol version; read caps. Remember the USB serial number
   and reconnect only to that one after a reboot.
3. `:33#` → state, positions, flags. Warn the user on flags 0/1 (unverified position) and 2 (EEPROM).
4. Moves: send the move command, expect `OK#`, poll `:33#` every 100–250 ms until state is 1.
5. On `error` ≠ 0: show it, then `:34,0#` to clear.

## 6. Error codes

Source of truth: `fw/src/error_codes.h`. Numbers follow OpenWheel and are **never reused** for a
different meaning; codes OpenWheel uses for wheel-only faults are left unused here.

| Code | Name | Kind | AstroUniBoard use |
|---|---|---|---|
| 0 | none | | |
| 1 | home timeout | sticky fault | rotator IMU homing did not finish |
| 2 | move timeout | sticky fault | move did not finish in its expected time |
| 3 | calibration failed | sticky fault | IMU zero calibration failed |
| 4 | EEPROM missing or write failed | sticky fault | |
| 5 | motor driver not answering / overtemp / short | sticky fault | |
| 6 | (OpenWheel: no Hall pulse) | — | not used |
| 7 | bad parameter | rejection | also: target outside soft limits or cable-wrap range |
| 8 | busy | rejection | an axis moves or homes |
| 9 | not calibrated | rejection | IMU home requested without IMU zero calibration |
| 10 | unknown command | rejection | also: axis or block not in this build's caps |
| 11 | bad format / line too long | rejection | |
| 12 | not changeable at runtime | rejection | |
| 13 | motor stalled (StallGuard) | sticky fault | |
| 14 | sensor missing or no valid reading | rejection | SHT31 / IMU query without a valid value |
| 15 | IMU: optical axis too close to vertical | rejection | `:77` / `:95` near the zenith (roll undefined) |
| 16 | not identified | rejection | any command but `:25` / `:32` before `:32#` since the last reset |

A sticky fault stops the moving axis, returns state to idle and keeps the code in `:33#` / `:34#`
until `:34,0#`. Moves stay allowed (the host decides). Rejections appear only in the reply.

## 7. Differences from OpenWheel

| Topic | OpenWheel (protocol 2) | AstroUniBoard (protocol 1) |
|---|---|---|
| identify | `OPENWHEEL,<model>,<fw>,<proto>,<serial>#` | prefix `ASTROUNIBOARD`, extra `caps` field |
| status | wheel fields | own fields (3.2); same `S,` tag and `<error>,<flags>` meaning in spirit |
| housekeeping | 6 fields | same 6 + two per-driver fields; `v5` always `-1` |
| StallGuard | one motor, `:41#` / `:42,<thr>#` | axis selector: `:41,F#` / `:42,F,<thr>#` |
| EEPROM | some setters save at once | setters change running values; `:23#` saves |
| 00–21, 24–29, 35–39, 45 | wheel commands | unused except 22, 23, 24, 25 |
| 46 | — | reset / safe-mode info |
| 47 / 48 | — | per-axis motor driver config (current, microsteps, chopper mode, idle off) |
| sticky fault | 10 s error state, LEDs alternate | no lock-out; code stays until cleared |

## 8. Review decisions (2026-11-06)

- Identify `model` is the **board revision** (`REV2`), not the MCU.
- `:24#` reset to defaults **stays**.
- Status state 5 **halting** stays: a halted axis reports 5 while it decelerates, then 1.
