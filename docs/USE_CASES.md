# AstroUniBoard — Possible Use Cases

Beyond the current focuser (AXIS4) + rotator (AXIS3) role, the board's hardware supports a wider range of applications.

## What the hardware actually offers

| Resource | Notes |
|----------|-------|
| 2× TMC2209 stepper channels | UART-configured, ~2 A peak each |
| 12 V input on an XT30 (J6) | Motor supply; rev 2.1 also senses it on PB10 |
| CH334P USB hub | Downstream USB port for a camera / other device |
| MPU-6000 IMU | Tilt, orientation, vibration |
| SHT31 (on board, U8) | Temperature + humidity (dew point); optional second SHT31 on J9 |
| HALL input (J4, DNP) | Index / home sensor |
| UART6 (PA4/PA5, J10, DNP) | Spare serial |
| AUX1 (PC13), AUX2 (PC14), LIMIT (PA1) | Spare GPIO (J8 for AUX1/AUX2, DNP) |
| 2 buttons (PA14, PB6) | Currently unused |
| I²C bus (J9, DNP) | Expandable. On board: EEPROM 0x50, SHT31 0x45 (rev 2.0+; 0x44 on rev 1), MPU-6000 0x68. An external SHT31 on J9 uses 0x44 |

In short: two smart stepper channels + tilt/vibration sensor + environment sensor + USB hub, all on 12 V.

> **Constraint:** the V1 firmware (OnStepX port, `fw-onstepx/`) fills about 93 % of the 128 KB flash, so any idea that adds features to OnStepX hits that limit. The V2 firmware (`fw/`) uses about 65 %, which leaves room for small additions. See [Rev 3 suggestions](#rev-3-suggestions).

## 1. Firmware-only (same PCB)

- **Filter wheel** — one axis drives the wheel, HALL input is the index/home sensor. Not built into OnStepX; needs a small custom command set or a native ASCOM driver. (An earlier `filter_wheel` design exists in HW backups.)
- **Dual focuser** — both axes as focusers: main scope + guide scope, or OAG with a separate guide-camera focus. OnStepX already supports multiple focusers.
- **Motorized flat panel / dust cap** — one axis opens/closes the cap, HALL or LIMIT as end stop. The panel's EL/LED dimming needs a PWM output on AUX (see section 2).
- **Small mount tracking** — enable AXIS1/2 in OnStepX on the two TMC2209s for a star tracker, barn-door or camera-lens mount. The MPU-6000 can assist initial alignment (level/tilt). In OnStepX this requires compiling out the focuser/rotator code to fit in the V1 flash.
- **Telescope cover / roof / small dome shutter** — light loads only (TMC2209 ~2 A peak).
- **Mount / observatory monitor** — MPU-6000 for vibration and wind-shake logging, mount-tilt alarms, "scope moved/bumped" detection; SHT31 dew-point alerts.
- **Pier/flip-aware rotator** — use the IMU for absolute orientation (extends the existing IMU homing).

## 2. Small add-ons via spare pins (AUX1/2, UART6, I²C)

- **Dew heater control** — PWM on AUX1/AUX2 driving an external MOSFET module, closed loop on SHT31 dew point. The most requested astro-rig accessory; the sensor half is already on board.
- **GPS module on UART6** — time and location for a mount or plate-solving setup.
- **Sky quality meter (TSL2591)** or **cloud sensor (MLX90614 IR)** on I²C — turns the board into a mini ASCOM SafetyMonitor / ObservingConditions device.
- **Hand controller** — the two reserved buttons as manual focus in/out.

## 3. Non-astro uses

- Camera slider or pan/tilt motion-control rig for timelapse (IMU for levelling).
- Microscope focus stacking (Z-stage + sample rotation).
- Turntable for 3D scanning or product photography.
- Lab syringe pump or slow precision linear stage.

## Rev 3 suggestions

- **Power-box features:** 2–3 MOSFET PWM outputs (dew heaters), switched 12 V outputs, INA219 current/voltage monitor. Board would then cover focuser + rotator + USB hub + dew control + power distribution in one box.
- **Bigger flash:** STM32G0B1**CC** (256 KB) or **CE** (512 KB), same LQFP-48 pinout and a drop-in replacement (avoid the `…N` variants, they have a different pinout). V2 fits in 128 KB; the larger parts matter for OnStepX-based ideas such as mount tracking.
