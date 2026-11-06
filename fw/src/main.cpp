// AstroUniBoard firmware V2 — superloop: protocol -> sensors. Plan: docs/V2_PLAN.md
#include <Arduino.h>
#include <Wire.h>

#include "config.h"
#include "env_sht31.h"
#include "protocol.h"

// Answer even when the host opens the port with DTR = false (same as OpenWheel).
extern "C" void CDC_enableDTR(bool enable);

static Protocol protocol;

void setup() {
    // Motor drivers off until the axis code exists (EN is active LOW).
    pinMode(PIN_ROT_EN, OUTPUT);
    digitalWrite(PIN_ROT_EN, HIGH);
    pinMode(PIN_FOC_EN, OUTPUT);
    digitalWrite(PIN_FOC_EN, HIGH);

    pinMode(PIN_STATUS_LED, OUTPUT);
    digitalWrite(PIN_STATUS_LED, LOW);

    CDC_enableDTR(false);
    PROTOCOL_SERIAL.begin(115200);

    Wire.setSDA(PIN_I2C_SDA);
    Wire.setSCL(PIN_I2C_SCL);
    Wire.begin();

    envBegin();
}

void loop() {
    protocol.process();
    envUpdate();
}
