# V2 — issue index

GitHub is the source of truth: <https://github.com/xlazam01/AstroUniBoard/issues> (created 2026-11-05).
This file is only an index; edit issues on GitHub. Plan: [V2_PLAN.md](V2_PLAN.md).


## M0 Decisions & repo prep
- [#1](https://github.com/xlazam01/AstroUniBoard/issues/1) Retire the old F:\Projekty\AstroUniBoard checkout
- [#2](https://github.com/xlazam01/AstroUniBoard/issues/2) Tag v1.0-onstepx
- [#3](https://github.com/xlazam01/AstroUniBoard/issues/3) Restructure repo: fw-onstepx/, fw/, hw/, cad/, driver/, tools/, release/
- [#4](https://github.com/xlazam01/AstroUniBoard/issues/4) Add LICENSE (GPL-3) and LICENSE-HARDWARE (CERN-OHL-S)
- [#5](https://github.com/xlazam01/AstroUniBoard/issues/5) CI skeleton: PlatformIO build of fw/ and fw-onstepx/ with flash/RAM size report
- [#6](https://github.com/xlazam01/AstroUniBoard/issues/6) Record shared-code decision with OpenWheel (copy & adapt)

## M1 Protocol spec
- [#7](https://github.com/xlazam01/AstroUniBoard/issues/7) PROTOCOL.md V2: framing and common block (OpenWheel-compatible numbers)
- [#8](https://github.com/xlazam01/AstroUniBoard/issues/8) Protocol: focuser command block 50-69
- [#9](https://github.com/xlazam01/AstroUniBoard/issues/9) Protocol: rotator command block 70-89
- [#10](https://github.com/xlazam01/AstroUniBoard/issues/10) Protocol: environment / IMU block 90-99
- [#11](https://github.com/xlazam01/AstroUniBoard/issues/11) Error codes table (error_codes.h is source of truth)
- [#12](https://github.com/xlazam01/AstroUniBoard/issues/12) docs/USB_IDS.md: VID 0x1209, test PID, planned PID
- [#13](https://github.com/xlazam01/AstroUniBoard/issues/13) Resolve remaining protocol questions

## M2 Firmware core
- [#14](https://github.com/xlazam01/AstroUniBoard/issues/14) New PlatformIO project in fw/ (HW rev2 only)
- [#15](https://github.com/xlazam01/AstroUniBoard/issues/15) USB descriptor: VID 0x1209 / test PID, product string, MCU-UID serial
- [#16](https://github.com/xlazam01/AstroUniBoard/issues/16) Protocol parser + dispatch: ping, identify, status, error
- [#17](https://github.com/xlazam01/AstroUniBoard/issues/17) NV store on 24AA64: records with magic + CRC, migration, wear policy
- [#18](https://github.com/xlazam01/AstroUniBoard/issues/18) Housekeeping: VDDA, +5V, die temp, TMC temp (+12 V if Q24)
- [#19](https://github.com/xlazam01/AstroUniBoard/issues/19) DFU :31# via magic word in TAMP backup register + reset, and reboot :22#; update tools/firmware_manager
- [#20](https://github.com/xlazam01/AstroUniBoard/issues/20) Status/activity LEDs
- [#21](https://github.com/xlazam01/AstroUniBoard/issues/21) tools/hwtest pytest skeleton against the real board
- [#22](https://github.com/xlazam01/AstroUniBoard/issues/22) Self-hosted HIL runner on the dev PC

## M3 Focuser
- [#23](https://github.com/xlazam01/AstroUniBoard/issues/23) Step timer + generic axis (trapezoid ramp, soft limits, halt)
- [#24](https://github.com/xlazam01/AstroUniBoard/issues/24) TMC2209 config per axis (current, microsteps, StealthChop) at runtime + EEPROM
- [#25](https://github.com/xlazam01/AstroUniBoard/issues/25) Focuser absolute/relative moves, backlash compensation, soft limits 0..max
- [#26](https://github.com/xlazam01/AstroUniBoard/issues/26) Focuser position persistence + power-loss flag
- [#27](https://github.com/xlazam01/AstroUniBoard/issues/27) StallGuard stall stop on focuser
- [#28](https://github.com/xlazam01/AstroUniBoard/issues/28) hwtest: focuser suite

## M4 Rotator + IMU
- [#29](https://github.com/xlazam01/AstroUniBoard/issues/29) Rotator axis: mechanical angle, reverse, steps/deg
- [#30](https://github.com/xlazam01/AstroUniBoard/issues/30) Cable-wrap move planner with configurable min/max (default +-180)
- [#31](https://github.com/xlazam01/AstroUniBoard/issues/31) MPU-6000 driver + gravity roll angle
- [#32](https://github.com/xlazam01/AstroUniBoard/issues/32) IMU zero calibration + gravity homing + boot-home setting
- [#33](https://github.com/xlazam01/AstroUniBoard/issues/33) hwtest: rotator suite

## M5 Environment
- [#34](https://github.com/xlazam01/AstroUniBoard/issues/34) SHT31 temperature / humidity / dew point

## G1 pid.codes
- [#35](https://github.com/xlazam01/AstroUniBoard/issues/35) Public README: photos, schematic PDF, BOM, build status
- [#36](https://github.com/xlazam01/AstroUniBoard/issues/36) Open pid.codes PR for AstroUniBoard PID
- [#37](https://github.com/xlazam01/AstroUniBoard/issues/37) Switch firmware and drivers to the assigned PID

## M6 Host core
- [#38](https://github.com/xlazam01/AstroUniBoard/issues/38) AstroUniBoard.Core: transport, worker, circuit breaker, detection
- [#39](https://github.com/xlazam01/AstroUniBoard/issues/39) Device simulator shared by ASCOM and INDI tests
- [#40](https://github.com/xlazam01/AstroUniBoard/issues/40) Core unit tests with a misbehaving fake device

## M7 ASCOM
- [#41](https://github.com/xlazam01/AstroUniBoard/issues/41) ASCOM LocalServer + IFocuserV4
- [#42](https://github.com/xlazam01/AstroUniBoard/issues/42) ASCOM IRotatorV4 (Sync, MechanicalPosition, reverse)
- [#43](https://github.com/xlazam01/AstroUniBoard/issues/43) ASCOM ObservingConditions (temperature, humidity, dew point)
- [#44](https://github.com/xlazam01/AstroUniBoard/issues/44) Host-side temperature compensation support (Focuser.TempComp / temperature)
- [#45](https://github.com/xlazam01/AstroUniBoard/issues/45) Standalone control & debug app
- [#46](https://github.com/xlazam01/AstroUniBoard/issues/46) Installer (Inno Setup)
- [#47](https://github.com/xlazam01/AstroUniBoard/issues/47) ConformU clean for Focuser, Rotator, ObservingConditions
- [#48](https://github.com/xlazam01/AstroUniBoard/issues/48) ASCOM field test in NINA on astrobox

## M8 INDI
- [#49](https://github.com/xlazam01/AstroUniBoard/issues/49) indi_astrouniboard: one device, FOCUSER | ROTATOR | WEATHER interfaces
- [#50](https://github.com/xlazam01/AstroUniBoard/issues/50) INDI udev rule, docker build, simulator tests
- [#51](https://github.com/xlazam01/AstroUniBoard/issues/51) INDI field test in KStars/Ekos

## M10 Release 2.0
- [#52](https://github.com/xlazam01/AstroUniBoard/issues/52) User manual V2 + migration note from fw-onstepx
- [#53](https://github.com/xlazam01/AstroUniBoard/issues/53) Release workflow: fw .bin, ASCOM Setup.exe, INDI tarball
- [#54](https://github.com/xlazam01/AstroUniBoard/issues/54) Archive release binaries to A:\config\firmware\AstroUniBoard\

## NINA plugin

Tracked in the separate repo [xlazam01/SecondaryFocuser-NINA](https://github.com/xlazam01/SecondaryFocuser-NINA/issues) (issues #1–#10) (local: `F:\Projekty\Astro\software\SecondaryFocuser-NINA`),
milestones 0.1 Guide-camera autofocus, 0.2 Filter-offset compensation, 1.0 Release.
