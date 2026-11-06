#include <Arduino.h>
#include <Wire.h>

#include "config.h"
#include "i2c_bus.h"

void i2cBegin() {
    Wire.setSDA(PIN_I2C_SDA);
    Wire.setSCL(PIN_I2C_SCL);
    Wire.begin();
    Wire.setClock(I2C_CLOCK_HZ);
}

void i2cRestart() {
    Wire.end();
    Wire.begin();
    Wire.setClock(I2C_CLOCK_HZ);
}
