# AstroUniBoard Focuser/Rotator - Pin Assignment

MCU: STM32G0B1CBT6 (48-pin LQFP, 128 KB Flash, 144 KB SRAM, ARM Cortex-M0+ @ 64 MHz)

## Motor Control

| Function       | Pin  | Notes                          |
|----------------|------|--------------------------------|
| AXIS3 ENABLE   | PB7  | Rotator (OnStepX convention)   |
| AXIS3 DIR      | PB8  | Rotator                        |
| AXIS3 STEP     | PB9  | Rotator                        |
| AXIS3 DIAG     | PA7  | Not used                       |
| AXIS4 ENABLE   | PB0  | Focuser (OnStepX convention)   |
| AXIS4 DIR      | PB1  | Focuser                        |
| AXIS4 STEP     | PB2  | Focuser                        |

## TMC2209 UART (shared bus, USART2)

| Function | Pin  | Notes                          |
|----------|------|--------------------------------|
| TX       | PA2  | USART2 TX, shared by both TMCs |
| RX       | PA3  | USART2 RX, shared by both TMCs |

TMC2209 addresses: AXIS3=1, AXIS4=0 (hardwired via MS1/MS2)

## I2C Bus (24AA64 NV EEPROM + SHT31 sensor)

| Function | Pin  | Notes                          |
|----------|------|--------------------------------|
| SCL      | PA9  |                                |
| SDA      | PA10 |                                |

## UART6 (reserved, not used)

| Function | Pin  | Notes                          |
|----------|------|--------------------------------|
| TX6      | PA4  | Reserved for future use        |
| RX6      | PA5  | Reserved for future use        |

## Status & UI

| Function      | Pin  | Notes                          |
|---------------|------|--------------------------------|
| STATUS1 LED   | PB12 |                                |
| STATUS2 LED   | PB4  |                                |
| BUTTON LEFT   | PA14 | Not used                       |
| BUTTON RIGHT  | PB6  | Not used                       |

## Misc

| Function | Pin  | Notes                          |
|----------|------|--------------------------------|
| LIMIT    | PA1  | Not used                       |
| AUX1     | PC13 | Not used                       |
| AUX2     | PC14 | Not used                       |
