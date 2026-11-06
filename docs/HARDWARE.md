# AstroUniBoard — Hardware Reference

## Board revisions

| Rev | Status | Git |
|-----|--------|-----|
| 1.x | Prototype boards (OnStepX firmware) | `f313306` |
| **2.0** | **Frozen 2026-11-06 for bring-up testing.** Change only through a new revision. | tag `hw-rev2.0` |

Rev 2.0 is pin-compatible with rev 1 for firmware: every MCU pin has the same function.
Differences the firmware can see:

| Item | Rev 1 | Rev 2.0 |
|------|-------|---------|
| On-board SHT31 (U8) address | 0x44 (ADDR = GND) | **0x45** (ADDR = PB9 supply) |
| External SHT31 on J9 (I2C) | not possible (address clash) | **0x44** |
| MPU-6000 /CS | floating | tied to +3.3V (I2C mode) |

Other rev 2.0 changes (no firmware impact): schematic reorganised, +5V3 buck 5.3 V, USBLC6 ESD
and 56k Rp on the downstream USB-C, JP5 (+12V motors) bridged by default, TMC2209 SPREAD/STDBY to GND,
EEPROM WP to GND, SHT31 supply decoupled (C69), dimmer LEDs, hub crystal load caps DNP, track clean-up.

## MCU

| Property | Value |
|----------|-------|
| Part | STM32G0B1CBT6 |
| Architecture | ARM Cortex-M0+ |
| Clock | 64 MHz |
| Flash | 128 KB |
| RAM | 144 KB SRAM |
| Package | 48-pin LQFP |

## Motor Control

| Function | Pin | Notes |
|----------|-----|-------|
| AXIS3 ENABLE | PB13 | Rotator — active LOW |
| AXIS3 DIR | PB14 | Rotator |
| AXIS3 STEP | PB15 | Rotator |
| AXIS4 ENABLE | PB0 | Focuser — active LOW |
| AXIS4 DIR | PB1 | Focuser |
| AXIS4 STEP | PB2 | Focuser |

OnStepX axis convention: AXIS3 = Rotator, AXIS4 = Focuser. AXIS1/AXIS2 (mount) are disabled.

## TMC2209 UART (shared bus, USART2)

| Function | Pin | Notes |
|----------|-----|-------|
| TX | PA2 | USART2 TX, shared by both drivers |
| RX | PA3 | USART2 RX, shared by both drivers |

Baud rate: 460800. TMC2209 addresses hardwired via MS1/MS2: AXIS3 = addr 1, AXIS4 = addr 0.

## I2C Bus

| Function | Pin | Notes |
|----------|-----|-------|
| SCL | PA9 | 24AA64 EEPROM + SHT31 sensor |
| SDA | PA10 | 24AA64 EEPROM + SHT31 sensor |

| Device | Address | Purpose |
|--------|---------|---------|
| 24AA64 | 0x50 | 8 KB NV storage (focuser/rotator position, settings). The fw-onstepx pinmap comment says 0x57 — confirm with an I2C bus scan |
| SHT31 on-board (U8) | 0x45 (rev 2.0), 0x44 (rev 1) | Temperature + humidity, ADDR tied to the PB9 supply |
| SHT31 external (J9) | 0x44 | Optional, rev 2.0 only |
| MPU-6000 | 0x68 | IMU (rotator gravity homing) |

| Function | Pin | Notes |
|----------|-----|-------|
| SHT_PWR | PB9 | SHT31 supply, active HIGH. On rev 2.0 the on-board sensor answers only while it is on |

## USB (Native USB CDC)

| Function | Pin | Notes |
|----------|-----|-------|
| USB D- | PA11 | |
| USB D+ | PA12 | |

Appears as virtual COM port. Baud rate: 115200.

## Status & UI

| Function | Pin | Active | Notes |
|----------|-----|--------|-------|
| Status LED | PB12 | LOW | Power/status indicator |
| Activity LED | PB4 | LOW | Blinks during focuser/rotator movement |
| Button LEFT | PA14 | — | Reserved, not used |
| Button RIGHT | PB6 | — | Reserved, not used |

## Auxiliary / Reserved

| Function | Pin | Notes |
|----------|-----|-------|
| UART6 TX | PA4 | Reserved for future use |
| UART6 RX | PA5 | Reserved for future use |
| LIMIT | PA1 | Not used |
| AUX1 | PC13 | Not used |
| AUX2 | PC14 | Not used |
| AXIS3 DIAG | PA7 | TMC2209 diag pin, not used |

## PlatformIO Board Mapping

The board is built using the `btt_ebb42_v1_1` PlatformIO target — this board uses the same STM32G0B1CBT6 chip and provides the correct STM32duino support. DFU upload targets flash address `0x08000000`.
