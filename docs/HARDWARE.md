# AstroUniBoard — Hardware Reference

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
| AXIS3 ENABLE | PB7 | Rotator — active LOW |
| AXIS3 DIR | PB8 | Rotator |
| AXIS3 STEP | PB9 | Rotator |
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
| 24AA64 | 0x50 | 8 KB NV storage (focuser/rotator position, settings) |
| SHT31 | 0x44 | Temperature + humidity sensor |

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
