// AstroUniBoard V2 — build configuration and pin map (HW rev 2.0)
#pragma once

#define FIRMWARE_VERSION     "2.0.0-dev"

#define PROTOCOL_SERIAL      SerialUSB

// I2C bus: 24AA64 EEPROM, SHT31 (on-board + external J9), MPU-6000
#define PIN_I2C_SCL          PA9
#define PIN_I2C_SDA          PA10

// SHT31 supply. Also drives the ADDR pin of the on-board sensor, so it answers at 0x45 only while powered.
#define PIN_SHT_PWR          PB9
#define SHT31_ADDR_ONBOARD   0x45
#define SHT31_ADDR_EXTERNAL  0x44   // optional sensor on J9

// TMC2209 enable pins, active LOW. Held HIGH (driver off) until the axis code exists.
#define PIN_ROT_EN           PB13
#define PIN_FOC_EN           PB0

#define PIN_STATUS_LED       PB12   // active LOW
#define PIN_ACT_LED          PB4    // active LOW
