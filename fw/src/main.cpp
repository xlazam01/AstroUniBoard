// AstroUniBoard firmware V2 — superloop: protocol -> sensors. Plan: docs/V2_PLAN.md
#include <Arduino.h>
#include <Wire.h>

#include "config.h"
#include "diag.h"
#include "env_sht31.h"
#include "protocol.h"

// Answer even when the host opens the port with DTR = false (same as OpenWheel).
extern "C" void CDC_enableDTR(bool enable);

static Protocol protocol;
static bool safeMode;   // last reset came from the watchdog: run without the sensors

void setup() {
    diagBoot();         // reset cause + last progress marker, starts the watchdog
    safeMode = diagWatchdogReset();
    diagMark(DIAG_SETUP);

    // Motor drivers off until the axis code exists (EN is active LOW).
    pinMode(PIN_ROT_EN, OUTPUT);
    digitalWrite(PIN_ROT_EN, HIGH);
    pinMode(PIN_FOC_EN, OUTPUT);
    digitalWrite(PIN_FOC_EN, HIGH);

    pinMode(PIN_STATUS_LED, OUTPUT);
    digitalWrite(PIN_STATUS_LED, LOW);
    pinMode(PIN_ACT_LED, OUTPUT);

    CDC_enableDTR(false);
    PROTOCOL_SERIAL.begin(115200);

    if (!safeMode) {
        // Bring-up: idle I2C lines must read HIGH. Note = 0x100 | SDA,SCL with the SHT supply off (bits 0,1)
        // and on (bits 2,3); kept across a watchdog reset and reported by :99#.
        pinMode(PIN_I2C_SDA, INPUT);
        pinMode(PIN_I2C_SCL, INPUT);
        pinMode(PIN_SHT_PWR, OUTPUT);
        digitalWrite(PIN_SHT_PWR, LOW);
        delay(5);
        uint32_t note = 0x100 | digitalRead(PIN_I2C_SDA) | (digitalRead(PIN_I2C_SCL) << 1);
        digitalWrite(PIN_SHT_PWR, HIGH);
        delay(5);
        note |= (digitalRead(PIN_I2C_SDA) << 2) | (digitalRead(PIN_I2C_SCL) << 3);
        diagNote(note);

        diagMark(DIAG_WIRE_BEGIN);
        Wire.setSDA(PIN_I2C_SDA);
        Wire.setSCL(PIN_I2C_SCL);
        Wire.begin();
        diagMark(DIAG_ENV_BEGIN);
        envBegin();
    }
}

void loop() {
    diagFeed();
    // Heartbeat on the activity LED (active LOW): 1 Hz normally, 4 Hz in safe mode.
    digitalWrite(PIN_ACT_LED, (millis() / (safeMode ? 125 : 500)) & 1);

    diagMark(DIAG_PROTOCOL);
    protocol.process();
    if (!safeMode) {
        diagMark(DIAG_ENV_UPDATE);
        envUpdate();
    }
}
