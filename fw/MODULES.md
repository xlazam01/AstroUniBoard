# Firmware V2 — module map

Protocol: [`docs/PROTOCOL.md`](../docs/PROTOCOL.md) (1.0). Decisions: [`docs/V2_PLAN.md`](../docs/V2_PLAN.md) §9.
Build: `"$USERPROFILE/.platformio/penv/Scripts/pio.exe" run` in `fw/` (`pio` is not on PATH).
Host unit tests: `pio test -e native` (pure code only, needs a host gcc).

The headers are the contracts between modules. Code that needs another module uses only its header.

| Module | Files | Issues | Notes |
|---|---|---|---|
| Superloop | `main.cpp` | | begin() + update() of every module, diag markers |
| Status | `status.*` | #16 | state + flags of `:33#`, sticky error |
| Settings | `settings.*` | #13 | running values, compiled-in defaults (§4.1 / §4.2) |
| Framing + dispatch | `protocol.*` | #16 | replies, argument parsing, routing by range |
| Common block 20–49 | `protocol_common.cpp` | #16 | |
| Focuser block 50–69 | `protocol_focuser.cpp` | #25 | |
| Rotator block 70–89 | `protocol_rotator.cpp` | #29 #30 #32 | |
| Env / IMU block 90–99 | `protocol_env.cpp` | #31 #32 #34 | |
| Step timer | `step_timer.*` | #23 | TIM3, one axis at a time |
| Axis | `axis.*` | #23 | position, target, trapezoid ramp, halt |
| TMC2209 + StallGuard | `tmc2209.*` | #24 #27 | USART2 460800, focuser addr 0, rotator addr 1 |
| Focuser | `focuser.*` | #25 #26 | soft limits, backlash, position persistence |
| Rotator | `rotator.*`, `rotator_wrap.*` | #29 #30 #32 #74 #75 | angle, cable wrap, IMU home / calibration sequence, suspect angle |
| Move guard | `move_guard.*` | #75 #77 | pure: suspect angle / safe-mode move rules (native test) |
| IMU | `imu_mpu6000.*` | #31 #32 | MPU-6000 on I2C 0x68, roll / tilt, calibration math |
| SHT3x / SHT4x | `env_sht31.*` | #34 #90 | on-board 0x45, J9 0x44, each SHT3x or SHT4x (auto-detected), selection |
| NV store | `nv_store.*` | #17 | 24AA64: settings, position, IMU calibration records |
| Housekeeping | `housekeeping.*` | #18 #74 | die temp, VDDA, driver temp classes, +12 V sense (rev 2.1 build) |
| LEDs | `leds.*` | #20 | TIM16 soft PWM |
| Diagnostics | `diag.*`, `bootloader.*`, `clock_config.*` | #19 | IWDG, safe mode, DFU |

Timers: TIM3 step generator, TIM16 LED PWM. ISRs touch only the step timer and the active axis.

Start-up order (`setup()`): diag → status → settings defaults → axis pins → USB → (not in safe mode:
Wire → nvBegin → imuBegin → envBegin) → step timer → tmcBegin → focuserBegin → rotatorBegin → hkBegin.
Safe mode (after a watchdog reset) skips every I2C device: defaults in use, EEPROM fault flag set.
Moves in safe mode: absolute refused, small relative jogs only, until a sync (PROTOCOL.md §4, #77).

Builds: `env:rev2` (rev 2.0, `HW_REV` 20) and `env:rev21` (rev 2.1, `-D HW_REV=21`: +12 V sense on PB10).
Image marker `ASTROUNIBOARD-IMG:REV2;` in `main.cpp` (PROTOCOL.md §9, #79).
