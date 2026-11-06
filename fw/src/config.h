// AstroUniBoard V2 — build configuration and pin map (HW rev 2.0, docs/HARDWARE.md)
// Owner: orchestrator.
#pragma once

#define FIRMWARE_VERSION     "2.0.0-dev"
#define FIRMWARE_ID          "FW2.0.0"     // identify field (PROTOCOL.md §3.1)
#define PROTOCOL_VERSION     2   // 2: identify gate (ERR,16), IMU calibration with three captures
#define BOARD_MODEL          "REV2"

// identify caps bits (PROTOCOL.md §3.1): what this firmware build supports
#define CAP_FOCUSER          0x1
#define CAP_ROTATOR          0x2
#define CAP_IMU              0x4
#define CAP_ENV              0x8
#define FIRMWARE_CAPS        (CAP_FOCUSER | CAP_ROTATOR | CAP_IMU | CAP_ENV)

#define PROTOCOL_SERIAL      SerialUSB

// I2C bus: 24AA64 EEPROM, SHT31 (on-board + external J9), MPU-6000 (0x68)
#define PIN_I2C_SCL          PA9
#define PIN_I2C_SDA          PA10

// SHT31 supply. Also drives the ADDR pin of the on-board sensor, so it answers at 0x45 only while powered.
#define PIN_SHT_PWR          PB9
#define SHT31_ADDR_ONBOARD   0x45
#define SHT31_ADDR_EXTERNAL  0x44   // optional sensor on J9

#define MPU6000_ADDR         0x68

// TMC2209 drivers: shared single-wire UART on USART2, addresses focuser 0 / rotator 1
#define PIN_TMC_TX           PA2
#define PIN_TMC_RX           PA3
#define TMC_BAUD             460800
#define TMC_ADDR_FOC         0
#define TMC_ADDR_ROT         1

// Rotator = AXIS3, focuser = AXIS4. EN is active LOW.
#define PIN_ROT_EN           PB13
#define PIN_ROT_DIR          PB14
#define PIN_ROT_STEP         PB15
#define PIN_FOC_EN           PB0
#define PIN_FOC_DIR          PB1
#define PIN_FOC_STEP         PB2

#define PIN_STATUS_LED       PB12   // active LOW
#define PIN_ACT_LED          PB4    // active LOW

// Q15: positions are written to the EEPROM this long after an axis stops
#define POSITION_SAVE_DELAY_MS  2000
