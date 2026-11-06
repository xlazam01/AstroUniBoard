# AstroUniBoard Firmware V2 — Plan (DRAFT 0.1, 2026-11-05)

Status: **draft for discussion.** Decisions marked **[Qnn]** are open — see section 9.
Nothing here is implemented. V1 (OnStepX port) stays usable until V2 reaches feature parity.

## 1. Goals

1. **Replace OnStepX** with a small purpose-built firmware for a focuser + rotator board
   (STM32G0B1, 2× TMC2209, 24AA64, SHT31, MPU-6000). Gets rid of the 93 % flash wall, the LX200
   dialect and the mount code we never use.
2. **Protocol in the OpenWheel family**: `:NN[params]#`, every command answered (`value#`,
   `OK#`, `ERR,<code>#`), same framing, error-code style, identify/status/housekeeping/DFU commands,
   so host code (detection, transport, watchdog) is shared with OpenWheel.
3. **USB identity**: VID 0x1209 (pid.codes) + own PID, product string, MCU-UID serial.
   Apply at pid.codes **as soon as the repo is in a representative state** (gate defined in §7).
4. **ASCOM**: Focuser + Rotator (+ ObservingConditions?) drivers with a **control & debug dialog**
   (live status, jog, raw console, StallGuard/IMU/housekeeping readouts, log). ConformU clean.
5. **INDI** driver (focuser + rotator interfaces, simulator, udev rule), same structure as `indi_openwheel`.
6. **N.I.N.A. plugin**: autofocus of the **guide camera** (OAG camera or guide-scope camera) with a
   **second focuser** — the AstroUniBoard focuser — while NINA keeps driving the main focuser; plus
   automatic compensation of main-focuser filter offsets on the OAG focuser.
7. All of it tracked as **GitHub issues + milestones** in `xlazam01/AstroUniBoard`.

Non-goals (V2.0): mount control, LX200 compatibility, Alpaca server on the device, WiFi/ESP.

## 2. What we reuse

| From | What | How |
|---|---|---|
| OpenWheel `fw/` | protocol parser, error codes, identify/status, housekeeping (VDDA, +5V, die temp, TMC temp), EEPROM record with magic+CRC, DFU jump, LED PWM, TMC2209 init + StallGuard, USB descriptor flags | copy → adapt, or extract to a shared lib **[Q04]** |
| OpenWheel `driver/ASCOM` | `OpenWheel.Core` (worker thread, framing, circuit breaker, detection by VID/PID + identify, fake device tests), LocalServer, Inno Setup installer, ConformU workflow | same split: `AstroUniBoard.Core` + thin ASCOM layer |
| OpenWheel `driver/INDI` | serial_link, detect, protocol, simulator, tests, docker build | same split |
| V1 (this repo) | pinmap (`docs/HARDWARE.md`), MPU-6000 gravity homing idea (commit f313306), rotator angle convention + cable-wrap rules (`fw-onstepx/TESTPLAN_RotatorHoming.md`), `firmware_manager` MCP | rewrite (OnStepX code is GPL-3 and must not be copied verbatim if the license changes **[Q05]**) |

## 3. Firmware architecture (proposal)

```
fw/
  platformio.ini          one env: HW rev2
  src/
    main.cpp              superloop: usb → protocol → axes → sensors → housekeeping → nv
    protocol.*            :NN# parser + dispatch table (common 20–49, focuser 50–69, rotator 70–89, env/IMU 90–99)  [Q10]
    axis.*                generic stepper axis: position (int32 steps), target, trapezoid ramp,
                          backlash, limits, TMC2209 config (current, microsteps, StealthChop/SpreadCycle)
    step_timer.*          per-axis hardware timer step generation (ISR only toggles STEP + counts)
    focuser.*             axis + temp compensation + max travel + optional home (stall/limit pin)
    rotator.*             axis + angle math (mech ↔ sky offset), cable-wrap rule, IMU gravity homing
    imu_mpu6000.*         I2C, gravity vector → roll angle, filtering
    env_sht31.*           temperature, humidity, dew point
    housekeeping.*        from OpenWheel
    nv_store.*            24AA64 records (config / positions / names) with CRC + wear strategy
    leds.* buttons.*      PB12 status, PB4 activity, PA14/PB6 buttons (manual jog?)  [Q22]
    error_codes.h
```

Key behaviours to define:
- **Position persistence**: focuser position written to EEPROM after the axis has been idle N s,
  not on every move (wear). Power loss mid-move → position flagged "unknown" at boot. **[Q15]**
- **Rotator**: mechanical angle −180…+180 internally, reported 0…360; moves never cross the
  ±180° wrap (cable safety), so 270° from 0° goes CW via −90°. IMU finds gravity zero; `sync`
  sets the sky offset only (host side). **[Q17][Q18]**
- **One move at a time** across both axes (single step timer); busy → `ERR,8`. **[Q12 decided]**
- Step generation: hardware timers + STEP pins (keeps exact step count). TMC2209 `VACTUAL`
  internal generator rejected because it loses position. **[Q11]**

## 4. Protocol V2 (sketch — not a spec yet)

Common block — **same numbers and replies as OpenWheel** so `*.Core` libraries share code:

| Cmd | OpenWheel meaning | AstroUniBoard |
|---|---|---|
| 22 | reboot | same |
| 23 | write settings to EEPROM | same |
| 25 | ping | same |
| 30 | halt | halt **all** axes |
| 31 | DFU | same |
| 32 | identify | `ASTROUNIBOARD,<model>,<fw>,<proto>,<serial>,<caps>#` (caps = bitmask focuser/rotator/imu/env) **[Q09]** |
| 33 | status | `S,<state>,<foc_pos>,<foc_moving>,<rot_mech_cdeg>,<rot_moving>,<err>,<flags>#` — one atomic line |
| 34 | read / clear sticky error | same |
| 40 | housekeeping | same fields + VIN 12 V? **[Q24]** |
| 41/42 | StallGuard read / set | per axis param |
| 43/44 | LED brightness | same |

Device block (draft numbering): focuser 50–69 (pos, move abs/rel, max, speed, backlash, temp-comp
coeff, reverse, current, halt-axis…), rotator 70–89 (mech angle, move, reverse, steps/deg, home via
IMU, set home, wrap limits…), env/IMU 90–99 (temp, RH, dew point, raw gravity vector, IMU cal).

Units: focuser in steps (int32), rotator in centidegrees (int), temperature in 0.1 °C.
Every move answers `OK#` when started; completion is polled with `:33#` (OpenWheel rule).

## 5. Host software

```
driver/
  ASCOM/
    AstroUniBoard.Core/          net48, no ASCOM dep: transport, detection, worker, cache, sim device
    AstroUniBoard.Core.Tests/
    AstroUniBoard.Ascom/         LocalServer exposing Focuser + Rotator (+ ObservingConditions) on ONE port
    AstroUniBoard.Control/       control & debug dialog (also reachable from SetupDialog)   [Q28]
    installer/  conformance/
  INDI/                          indi_astrouniboard: FOCUSER_INTERFACE | ROTATOR_INTERFACE (| WEATHER?)  [Q31]
  (NINA "Secondary Focuser" plugin lives in its own repo Astro\software\SecondaryFocuser-NINA — C#, .NET 8, NINA 3.x)
tools/
  hwtest/                        pytest acceptance on real board (as in OpenWheel)
  sim/                           device simulator shared by ASCOM/INDI/NINA tests  [Q08]
```

ASCOM: one LocalServer process owns the COM port; Focuser and Rotator COM objects are clients of
the same `Core` worker, so NINA can connect both at once (this is the main reason for a LocalServer,
not an in-proc DLL). Interfaces: `IFocuserV4`, `IRotatorV4` (Platform 7: `Connect`/`DeviceState`).

### 5.1 N.I.N.A. plugin — "Secondary Focuser" (separate repo, generic)

Two focusers on Triton:

| Focuser | Moves | Driven by |
|---|---|---|
| **Main focuser** (commercial ASCOM focuser on the Esprit) | whole imaging train: main camera, filter wheel, OAG | NINA (its normal Focuser device, built-in AF, filter offsets) |
| **Guide focuser** (AstroUniBoard focuser axis) | only the OAG guide camera (or the guide-scope camera on another rig) | **this plugin**, connected as a second ASCOM focuser |

NINA's built-in AF knows one camera and one focuser, so it can't focus the guide camera. The plugin adds:

1. **Guide-camera autofocus** — frames come **through PHD2** (PHD2 keeps the camera): stop guiding →
   `capture_single_frame` / `save_image` over JSON-RPC → NINA star detection/HFR → move the guide
   focuser → fit the V-curve → go to best focus → resume guiding and wait for settle.
   Available as an **Advanced Sequencer instruction** ("Autofocus guide camera") and a button in the
   plugin's panel.
2. **Filter-offset compensation** — the OAG pick-off sits **in front of the filters**, so when NINA's
   main focuser applies a filter focus offset Δ (moving the whole train), the main camera stays in
   focus but the OAG camera goes out of focus by Δ. The plugin watches main-focuser moves caused by a
   filter change and moves the guide focuser by **−k·Δ** (k calibrated once; ideally 1 when both
   focusers have the same step size, else steps-per-µm ratio). Main-AF moves are *not* compensated
   (they correct real defocus that hits both cameras), only filter-offset moves. Δ is best taken from
   NINA's own filter list (offset of new filter − offset of old filter, on NINA's filter-changed event)
   rather than guessed from focuser motion. **[Q43]**
3. Generic: any ASCOM focuser can be the guide focuser; AstroUniBoard just needs to allow the plugin
   and other clients to connect at the same time (LocalServer).

## 6. Repository and process

- Tag current main `v1.0-onstepx`, then `git mv FW fw-onstepx` (kept, maintained lightly for rev1 /
  "just give me OnStepX" users) and start V2 in a new `fw/`. **[Q02 decided]**
- Layout aligned to `Astro/templates/device`: `fw/ fw-onstepx/ hw/ cad/ driver/ tools/ docs/ release/`
  (`HW/` → `hw/`).
- CI (GitHub Actions): `pio run` both envs + flash/RAM size report, Core unit tests, INDI build in
  docker, plugin build. Release workflow attaches `.bin`, `Setup.exe`, plugin zip. **[Q40]**
- Issues: one per deliverable, labelled `fw` `protocol` `ascom` `indi` `nina` `hw` `docs` `ci`
  `pidcodes`, grouped by milestones below. Draft list: [V2_ISSUES.md](V2_ISSUES.md).

## 7. Milestones

| # | Milestone | Exit criteria |
|---|---|---|
| M0 | Decisions & repo prep | this plan's open questions closed; v1 tagged; layout + license + CI skeleton |
| M1 | Protocol spec | `docs/PROTOCOL.md` V2 complete, reviewed against OpenWheel common block |
| M2 | FW core | USB 1209:test-PID, identify/status/HK/DFU/EEPROM on hardware; hwtest passes |
| M3 | FW focuser | moves, backlash, persistence, limits, StallGuard; hwtest |
| M4 | FW rotator + IMU | angle convention, cable-wrap, gravity homing (TESTPLAN_RotatorHoming) |
| M5 | FW env + temp comp | SHT31, dew point, temp-comp coefficient |
| **G1** | **pid.codes gate** | repo public with: license files, schematic PDF + KiCad, working fw (M2–M4), PROTOCOL.md, README with photos → open PR at pidcodes **[Q07]** |
| M6 | Core lib + simulator | detection, transport, fake-device tests green |
| M7 | ASCOM | Focuser + Rotator via LocalServer, control/debug dialog, ConformU 0 errors, installer, tested in NINA on astrobox |
| M8 | INDI | driver + sim + tests, tested in KStars/Ekos |
| M9 | NINA plugin | guide-camera AF with the AstroUniBoard focuser + filter-offset compensation working on Triton with OAG |
| M10 | Release 2.0 | final PID in firmware, docs, release binaries, v1 → v2 migration note |

## 8. Risks

- **Two checkouts**: `F:\Projekty\AstroUniBoard` and `F:\Projekty\Astro\devices\AstroUniBoard` are both
  clones at f313306. Pick one before V2 work starts, or commits will diverge. **[Q01]**
- **License**: V1 is GPL-3 (OnStepX). A clean V2 can be GPL-3 like OpenWheel (simplest) — copying
  OnStepX code is then legally fine, but keep it minimal.
- **pid.codes** review time is unknown (OpenWheel PR still pending) → keep test PID 0x0001 until merged.
- **NINA plugin** depends on PHD2 API behaviour with OAG cameras and on NINA exposing filter-change
  events and filter offsets to plugins.
- **IMU homing** accuracy depends on where the MPU-6000 sits (rotating vs fixed part) **[Q19]**.

## 9. Open questions

See the interview — answers get recorded here as decisions.

### Decided (2026-11-05)
- Q01 Canonical checkout: **`F:\Projekty\Astro\devices\AstroUniBoard`**; `F:\Projekty\AstroUniBoard` retires.
- Q02 V1 stays **in main as `fw-onstepx/`** (lightweight OnStepX build for users who don't want V2);
  V2 lives in **`fw/`**. Both are built in CI.
- Q03 V2 supports **HW rev2 only**; rev1 boards run `fw-onstepx/`.
- Q04 Shared code with OpenWheel: **copy & adapt**; extract a common lib only when a 3rd device needs it.
- Q05/Q06 **Public, GPL-3 (fw/sw) + CERN-OHL-S (hw)**, OpenWheel-level docs over time.
- Q10 Command numbering: **ranges** — common block = OpenWheel numbers, focuser 50–69, rotator 70–89, env/IMU 90–99.
- Q19 **The PCB rotates with the camera**; the on-board MPU-6000 reads camera roll directly.
- Q33 NINA plugin goal (corrected): **autofocus the guide camera (OAG / guide scope) with the
  AstroUniBoard focuser**; NINA keeps the main focuser (a commercial ASCOM focuser on Triton).
- Q35 Guide frames **through PHD2** (JSON-RPC); PHD2 keeps owning the guide camera.
- Q37 OAG sits **in front of the filters** → plugin **auto-compensates** main-focuser filter offsets
  on the guide focuser (−k·Δ).
- Q38 Guide AF runs **manually and as a NINA Advanced Sequencer instruction**; no automatic triggers in 1.0.
- Q43 Compensation factor k is **calibrated**: move the main focuser by N steps, refocus the guide
  camera, k = guide steps / N (wizard in the plugin).
- Q44 Settle after guide AF: **time-based, configurable** (seconds of guiding after resume).
- Q45 Compensate **filter-offset moves only**: temperature and AF moves of the main focuser refocus
  the OAG camera too (shared optics), so mirroring them would defocus it.
- Q46 Plugin = **"Secondary Focuser NINA Plugin"**, own repo at
  `F:\Projekty\Astro\software\SecondaryFocuser-NINA` (+ GitHub), GPL-3, own versions, NINA manifest.
- Q20 Rotator IMU home at boot: **configurable** (EEPROM flag, default off; angle flagged unverified until homed).
- Q21 Temperature compensation **host-side** (NINA/driver); firmware only reports SHT31 data.
- Q26 ASCOM types: **Focuser + Rotator + ObservingConditions** in one LocalServer; no Switch.
- Q28 Control & debug UI: **standalone app** talking through the LocalServer (works while NINA is connected).
- Q16 Focuser: **no homing**, soft limits 0..max, position persisted, manual "set position".
- Q40 **HIL CI from M2**: self-hosted runner on the dev PC with the rev2 board on USB, runs `tools/hwtest`.
  Public repo ⇒ runner labelled `hil`, used only by `push` to main / `workflow_dispatch` / PRs from
  collaborators; GitHub setting "require approval for all outside collaborators"; never on `pull_request_target`.
  Runner must not hold the COM port while NINA/ASCOM tests run (job takes an exclusive lock).
- Q31 INDI: **one device, multi-interface** (FOCUSER | ROTATOR | WEATHER).
- Q17 Cable wrap: **configurable min/max mechanical angle in EEPROM, default ±180°**.
- Q22 Buttons: **unused** (board is mounted upside down inside the Triton enclosure, not reachable).
- Q07 pid.codes gate: **FW core + focuser working** (+ licenses, rev2 schematic PDF, PROTOCOL.md).
- Q19b IMU frame: **calibrate once** — user sets landscape, `set IMU zero` stores gravity offset + axis
  mapping in EEPROM; works for any mounting (Triton: board upside down).
- Q15 Power loss: **keep last saved position + "position unverified" flag**; save after 2 s idle.
- Q34 NINA target: **3.x current release** (.NET 8 plugin API).
- Q30 Alpaca: **after 2.0**.
- Q36 NINA plugin is **generic**: any ASCOM focuser can be the guide focuser (the plugin connects
  it itself, next to NINA's main focuser).
- Q39 Plugin lives in **its own repo**, submitted to the NINA plugin manifest; M9 issues go there.
- Q12 **One axis moves at a time**; a second move while busy answers `ERR,8`.

### Scope & repo (open)
- Q07 pid.codes: what counts as "representative" for you — is a working focuser enough, or rotator too?
- Q08 Simulator: firmware compiled for host (native PlatformIO env) vs. separate Python/C# fake?

### Protocol
- Q09 Identify string prefix and model names; capability bitmask in identify?
- Q10 Command numbering: ranges per device (above) vs. axis selector (`:51,F,1000#`) vs. 3-digit numbers?
- Q11 Step generation: HW timers + STEP pins (recommended) vs. TMC2209 VACTUAL?
- Q12 Simultaneous focuser + rotator moves required?
- Q13 Max step rate / microstepping / currents per axis; set at runtime over protocol and saved?
- Q14 Focuser: absolute only, or also relative mode exposed to ASCOM?
- Q15 Position persistence strategy and behaviour after power loss mid-move?
- Q16 Focuser homing: none / StallGuard to end stop / limit switch on PA1?
- Q17 Rotator: is 0…360 with forced unwind the final rule, or allow a configurable wrap range (e.g. ±200°)?
- Q18 Rotator sky angle: host only (ASCOM `Sync` offset) or stored in the device?
- Q19 MPU-6000: on the board that rotates with the camera, or fixed? What accuracy do you need (0.1°, 1°)?
- Q20 Rotator homing at boot: automatic IMU home, or only on command?
- Q21 Temperature compensation in firmware (autonomous) or host-driven (NINA/driver)?
- Q22 Buttons PA14/PB6: manual focuser jog? anything else (rotator jog, LED off)?
- Q23 USB hub (CH334P) on the board: any firmware control (port power switching) or passive?
- Q24 Housekeeping: add 12 V input voltage and motor-driver supply sense?

### ASCOM
- Q25 Toolchain stays .NET Framework 4.8 + Platform 7 + Inno Setup like OpenWheel?
- Q26 Device types: Focuser + Rotator only, or also ObservingConditions (temp/RH/dew point) and Switch (hub ports)?
- Q27 One installer for all drivers, or separate?
- Q28 Control & debug dialog: inside SetupDialog (OpenWheel style) or a standalone app usable while NINA is connected (needs LocalServer client mode)?
- Q29 What must the debug dialog show — raw console, live plots (StallGuard/temp), log file export, firmware update (DFU) button?
- Q30 Alpaca (cross-platform) now or later?

### INDI
- Q31 One INDI device with focuser+rotator(+weather) interfaces, or separate devices?
- Q32 Target platforms: Raspberry Pi/StellarMate, x86 Linux; PPA/packaging or source build only?

### NINA plugin
(Q33–Q39 and Q43–Q46 decided, see above.)

### Process
- Q40 CI on GitHub Actions OK? Self-hosted hardware-in-the-loop runner (astrobox / Pi with a board)?
- Q41 Release cadence and versioning (fw 2.x.y, drivers own versions)?
- Q42 Should I create the GitHub milestones + issues now from `V2_ISSUES.md`?
