# Rotator IMU Homing — Test Plan

**Firmware:** OnStepX / AstroUniBoard FW  
**Sensor:** MPU6000 (I2C 0x68, CS tied to VDDD, AD0 tied to GND)  
**Config:** `AXIS3_SENSE_HOME MPU6000`, `AXIS3_HOME_AUTO ON`

---

## Prerequisites & Config Changes Required

Before running these tests, verify the following config values in [Config.h](src/Config.h):

| Parameter | Current | Required | Reason |
|---|---|---|---|
| `AXIS3_LIMIT_MIN` | `0` | **`-180`** | Home at 0°, ±180° cable-safe range |
| `AXIS3_LIMIT_MAX` | `360` | **`+180`** | Home at 0°, ±180° cable-safe range |
| `AXIS3_STEPS_PER_DEGREE` | `88.889` | `88.889` | Correct for 200-step motor, 10:1 pulleys |
| `AXIS3_SENSE_HOME` | `MPU6000` | `MPU6000` | ✓ |
| `AXIS3_HOME_AUTO` | `ON` | `ON` | ✓ |

**Rebuild and flash after config changes** before starting tests.

---

## Orientation Reference

| Axis Position | Physical Orientation | Notes |
|---|---|---|
| `0°` | Landscape — base/home position | Camera in normal landscape |
| `+90°` | Portrait — rotated CCW | Motor drove CCW from 0° |
| `+180°` | Flipped horizontal | Hard limit (max CCW) |
| `-90°` | Portrait — rotated CW | Motor drove CW from 0° (= 270° in 0–360° notation) |
| `-180°` | Flipped horizontal | Hard limit (max CW) |

> **Cable safety:** The ±180° range means the rotator can make at most one half-turn in either direction from home. The firmware limits enforce this — no explicit untangle algorithm is needed. The shortest path always reverses the winding.

---

## Commands Reference

| Command | Description | Expected Response |
|---|---|---|
| `:rHA#` | Get current IMU angle | `d.d#` (degrees) or `NAN#` |
| `:rHS#` | Set home from current orientation (10-sample avg) | `1#` = success |
| `:rHG#` | Set home to gravity reference (0.0°) | `1#` = success |
| `:rC#` | Go to home position (iterative, up to 3 passes) | *(no response)* |
| `:rG#` | Get rotator axis position | `sDDD*MM#` |
| `:rT#` | Get rotator status | `M…` = moving, `S…` = stopped |
| `:r3#` | Set move rate 3 (1°/s) | *(no response)* |
| `:r>#` | Slew CW | *(no response)* |
| `:r<#` | Slew CCW | *(no response)* |
| `:rQ#` | Stop slew | *(no response)* |
| `:rS<angle>#` | Goto specific angle (DMS format) | `1#` = success |

---

## Test Steps

### Test 1 — Angle Range and Orientation

**Goal:** Confirm the axis reports the correct range and physical orientation at each reference angle.

**Procedure:**
1. Power on board, wait for auto-home to complete (motor stops, ~10–15 s).
2. Send `:rHA#` — note IMU angle, send `:rG#` — note axis position.
3. Manually command the rotator to each reference position using `:rS<angle>#` and verify the physical camera orientation matches the table above.
4. Verify the axis refuses to move beyond ±180° (returns error or clamps).

**Pass criteria:**
- `:rI#` returns `-180`, `:rM#` returns `180`.
- Commanding `:rS+180*00#` is accepted, `:rS+181*00#` is rejected or clamped.
- Physical orientation at +90° is portrait CCW, at -90° is portrait CW.

---

### Test 2 — Set Home at Current Position

**Goal:** Confirm `:rHS#` stores the current physical orientation as home in NV memory and persists across power cycles.

**Procedure:**
1. Position rotator at the desired home (landscape, camera upright).
2. Send `:rHS#` → expect `1#`.
3. Send `:rHA#` — record the stored home angle.
4. Power cycle the board.
5. After boot, send `:rHA#` — verify the home angle matches step 3.

**Pass criteria:**
- `:rHS#` returns `1#`.
- Home angle survives power cycle (read from NV on boot).

---

### Test 3 — Homing Precision

**Goal:** Verify that `:rC#` returns the rotator to the stored home within ±2° after the iterative correction passes.

**Procedure:**
1. Ensure home is set (Test 2 complete or `:rHS#` run at known position).
2. Record home IMU angle: `:rHA#`.
3. Send `:rS+010*00#` — drive to +10°.
4. Wait for motor to stop (`:rT#` returns `S…`).
5. Confirm `:rHA#` reads approximately +10°.
6. Send `:rC#`.
7. Poll `:rT#` every second until `S…` (allow up to 60 s for up to 3 correction passes).
8. Record final `:rHA#`.
9. Repeat from step 3 for -10°.

**Pass criteria:**
- Final IMU angle within **±2°** of stored home.
- Motor stops cleanly (no oscillation, no limit fault).

---

### Test 4 — Gravity Reference Home

**Goal:** Verify `:rHG#` sets the home angle to the IMU gravitational zero (the physical position where the chip reads 0°), and `:rC#` drives the rotator to that position.

**Procedure:**
1. Send `:rHG#` → expect `1#`.
2. Record current `:rHA#` — this is the offset from gravity zero.
3. Send `:rC#`.
4. Poll `:rT#` until stopped.
5. Record final `:rHA#`.

**Pass criteria:**
- Final IMU angle within **±2°** of 0.0°.
- Motor moved in the correct direction (shortest path from current position toward 0°).

> **Note:** Gravity zero (0°) is the physical orientation where the chip's X-axis reads zero acceleration — this corresponds to the mounting angle baked into the PCB orientation, not necessarily the camera landscape position. Use `:rHS#` for camera-referenced homing.

---

### Test 5 — Cable Untangle via Axis Limits

**Goal:** Verify that the ±180° axis limits prevent cable tangling by ensuring the motor cannot exceed one half-turn in either direction from home, and that homing always takes the unwinding path.

**Sub-test 5a — CW limit protection:**
1. From home (0°), slew CW to +90°: `:rS+090*00#`, wait for stop.
2. Send `:rC#` — motor should return CCW (unwinding) to 0°. Confirm IMU decreases.
3. Attempt `:rS+181*00#` — expect rejection (error) or clamping at +180°.

**Sub-test 5b — CCW limit protection:**
1. From home (0°), slew CCW to -90°: `:rS-090*00#`, wait for stop.
2. Send `:rC#` — motor should return CW (unwinding) to 0°. Confirm IMU increases.
3. Attempt `:rS-181*00#` — expect rejection or clamping at -180°.

**Sub-test 5c — Full range excursion and home:**
1. From home (0°), slew CW to +170°: `:rS+170*00#`, wait for stop.
2. Slew CCW to -170°: `:rS-170*00#` — motor must route through 0° (not through ±180° limits).
3. Send `:rC#` — returns home from -170° CW.

**Pass criteria:**
- Motor never exceeds ±180° axis position.
- Homing from ±90° always returns via the shorter (unwinding) path.
- Attempting to exceed limits returns an error or is silently clamped.

---

## Known Limitations / Open Items

| # | Item | Status |
|---|---|---|
| 1 | Config must be updated to `-180/+180` range before tests | **Action required** |
| 2 | Auto-home on boot uses NV-stored home — first boot after flash uses 0.0° default | Known, test after setting home via `:rHS#` and power cycling |
| 3 | IMU measures tilt, not absolute rotation — homing accuracy degrades if rotation axis is not perpendicular to gravity | Known hardware limitation |
| 4 | Gravity zero (Test 4) may not correspond to camera landscape position | Use `:rHS#` for camera-referenced workflows |
