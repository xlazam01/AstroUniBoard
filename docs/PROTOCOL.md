# Serial protocol — AstroUniBoard firmware V2

Status: **draft 0.2 (2026-11-06)**. Framing, USB identity, common block (20–49) and error codes
(#7), focuser block 50–69 (#8). Still placeholders: rotator 70–89 (#9), environment / IMU 90–99 (#10).

The protocol is a member of the **OpenWheel family**: same framing, same reply style, same numbers
and reply formats for the common commands, so the host `*.Core` libraries (transport, detection,
worker, circuit breaker) can share code. Reference: OpenWheel `docs/PROTOCOL.md` (protocol 2).
Differences from OpenWheel are listed in [§7](#7-differences-from-openwheel).

Protocol version reported by identify: **1** (raised on any incompatible change).

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
only if the USB VID/PID match **and** this line parses with a supported protocol version.

### 3.2 Status (`:33#`)

One atomic line with everything a driver polls:

```
S,<state>,<foc_pos>,<foc_target>,<rot_cdeg>,<rot_target_cdeg>,<error>,<flags>#
S,1,12500,-1,9000,-1,0,30#
```

| Field | Values |
|---|---|
| state | 0 init, 1 idle, 2 focuser moving, 3 rotator moving, 4 rotator homing (IMU), 5 halting |
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
(0..510, lower = more load) of its last move, `-1` if none. With a non-zero threshold, a reading at
or below `2 × threshold` while the axis runs faster than the axis' StallGuard minimum speed stops the
move with sticky error 13; the first 200 ms of a move are ignored. Default 0 (off) on both axes.
Tuning as OpenWheel: run a few normal moves, take the lowest `min`, set about a third of it.

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
| 70–89 | rotator: mechanical angle, move, reverse, steps/degree, cable-wrap min/max, IMU home, speed, current, microsteps | #9 |
| 90–99 | environment and IMU: SHT31 readings, dew point, gravity vector, IMU zero calibration | #10 |

Until #10 is written the firmware answers the bring-up command `:90#` with both SHT31 sensors:
`W,<st>,<t>,<rh>,<dew>,<st>,<t>,<rh>,<dew>,<power_cycles>#` (on-board first, then J9; values valid
only while `st` is 0). It will be renumbered or replaced.

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

## 5. Typical host sequence

1. Find ports with VID 0x1209 / PID 0x0001 (later the assigned PID).
2. `:32#` → check the `ASTROUNIBOARD` prefix and protocol version; read caps.
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
