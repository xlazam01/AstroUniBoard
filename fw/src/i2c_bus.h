// Shared I2C bus (PA9/PA10): 24AA64 EEPROM, SHT31 sensors, MPU-6000.
#pragma once

#define I2C_CLOCK_HZ  400000UL   // all devices on the bus support fast mode

void i2cBegin();     // pins, Wire.begin(), clock
// STM32duino Wire (core 2.12, G0): after a NACK from an absent device the next transfer can hang.
// Call after every failed transfer; restarts the peripheral and restores the bus clock
// (Wire.begin() alone falls back to 100 kHz).
void i2cRestart();
