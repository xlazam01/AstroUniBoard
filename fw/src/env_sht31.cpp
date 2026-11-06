// SHT31 driver (Sensirion SHT3x-DIS datasheet: single shot, high repeatability, no clock stretching).
#include <Arduino.h>
#include <Wire.h>
#include <math.h>

#include "config.h"
#include "diag.h"
#include "env_sht31.h"
#include "i2c_bus.h"
#include "settings.h"

static const uint32_t ENV_PERIOD_MS     = 2000;  // measurement interval
static const uint32_t MEAS_WAIT_MS      = 20;    // high repeatability: 15.5 ms max
static const uint32_t POWER_OFF_MS      = 100;   // supply off time when recovering the sensor
static const uint32_t POWER_UP_MS       = 2;     // datasheet: 1 ms max to idle after VDD rises
static const uint8_t  FAILS_BEFORE_CYCLE = 3;    // consecutive failures of a seen sensor before a power cycle

static const uint8_t ADDRS[ENV_SENSOR_COUNT] = { SHT31_ADDR_ONBOARD, SHT31_ADDR_EXTERNAL };

enum EnvState : uint8_t { ST_POWER_UP, ST_IDLE, ST_MEASURING, ST_POWER_OFF };

static ShtReading readings[ENV_SENSOR_COUNT] = { { SHT_NO_DATA, 0, 0, 0 }, { SHT_NO_DATA, 0, 0, 0 } };
static bool       triggered[ENV_SENSOR_COUNT];
static EnvState   state;
static uint32_t   stateSince;
static uint32_t   lastStart;
static bool       seen[ENV_SENSOR_COUNT];    // answered at least once since boot
static uint8_t    fails[ENV_SENSOR_COUNT];   // consecutive failures after it was seen
static uint16_t   powerCycles;

static void enter(EnvState s) { state = s; stateSince = millis(); }

static uint8_t crc8(const uint8_t* data, uint8_t len) {
    uint8_t crc = 0xFF;   // polynomial 0x31, init 0xFF
    for (uint8_t i = 0; i < len; i++) {
        crc ^= data[i];
        for (uint8_t b = 0; b < 8; b++) crc = (crc & 0x80) ? (uint8_t)((crc << 1) ^ 0x31) : (uint8_t)(crc << 1);
    }
    return crc;
}

static bool startMeasurement(uint8_t addr) {
    Wire.beginTransmission(addr);
    Wire.write(0x24);   // single shot, high repeatability, clock stretching disabled
    Wire.write(0x00);
    const uint8_t err = Wire.endTransmission();
    if (err == 0) return true;
    // STM32duino Wire (core 2.12, G0, interrupt mode): after a NACK from an absent device the next transfer
    // to a present one never completes and starves the main loop (found on the board 2026-11-06,
    // on-board U8 not fitted). Restarting the peripheral after every failed transfer fixes it.
    diagNoteOr((uint32_t)err << (addr == SHT31_ADDR_ONBOARD ? 16 : 20));
    i2cRestart();
    return false;
}

static void readMeasurement(EnvSensor s) {
    ShtReading& r = readings[s];
    uint8_t buf[6];
    if (Wire.requestFrom(ADDRS[s], (uint8_t)6) != 6) {
        r.status = SHT_NO_ACK;
        i2cRestart();   // same STM32duino Wire workaround as in startMeasurement()
        return;
    }
    for (uint8_t i = 0; i < 6; i++) buf[i] = (uint8_t)Wire.read();
    if (crc8(buf, 2) != buf[2] || crc8(buf + 3, 2) != buf[5]) { r.status = SHT_BAD_CRC; return; }

    const float t  = -45.0f + 175.0f * (float)((buf[0] << 8) | buf[1]) / 65535.0f;
    const float rh = 100.0f * (float)((buf[3] << 8) | buf[4]) / 65535.0f;
    const float g  = logf(fmaxf(rh, 0.1f) / 100.0f) + 17.62f * t / (243.12f + t);
    const float dp = 243.12f * g / (17.62f - g);

    r.tDeciC   = (int16_t)lroundf(t * 10.0f);
    r.rhDeci   = (uint16_t)lroundf(rh * 10.0f);
    r.dewDeciC = (int16_t)lroundf(dp * 10.0f);
    r.status   = SHT_OK;
}

void envBegin() {
    for (uint8_t i = 0; i < ENV_SENSOR_COUNT; i++) readings[i] = { SHT_NO_DATA, 0, 0, 0 };
    pinMode(PIN_SHT_PWR, OUTPUT);
    digitalWrite(PIN_SHT_PWR, HIGH);
    lastStart = millis() - ENV_PERIOD_MS;   // measure as soon as the sensor is up
    enter(ST_POWER_UP);
}

void envUpdate() {
    const uint32_t now = millis();
    switch (state) {
        case ST_POWER_UP:
            if (now - stateSince >= POWER_UP_MS) enter(ST_IDLE);
            break;

        case ST_IDLE:
            if (now - lastStart < ENV_PERIOD_MS) break;
            lastStart = now;
            for (uint8_t i = 0; i < ENV_SENSOR_COUNT; i++) {
                diagMark((DiagMark)(DIAG_ENV_START0 + i));
                triggered[i] = startMeasurement(ADDRS[i]);
                if (!triggered[i]) readings[i].status = SHT_NO_ACK;
            }
            enter(ST_MEASURING);
            break;

        case ST_MEASURING: {
            if (now - stateSince < MEAS_WAIT_MS) break;
            for (uint8_t i = 0; i < ENV_SENSOR_COUNT; i++) {
                diagMark((DiagMark)(DIAG_ENV_READ0 + i));
                if (triggered[i]) readMeasurement((EnvSensor)i);
            }
            // Either sensor may be absent (on-board U8 not fitted, nothing on J9). A sensor that answered
            // before and now fails repeatedly has hung: cycle the shared supply.
            bool hung = false;
            for (uint8_t i = 0; i < ENV_SENSOR_COUNT; i++) {
                if (readings[i].status == SHT_OK) { seen[i] = true; fails[i] = 0; }
                else if (seen[i] && ++fails[i] >= FAILS_BEFORE_CYCLE) hung = true;
            }
            if (hung) {
                for (uint8_t i = 0; i < ENV_SENSOR_COUNT; i++) fails[i] = 0;
                powerCycles++;
                digitalWrite(PIN_SHT_PWR, LOW);
                enter(ST_POWER_OFF);
            } else {
                enter(ST_IDLE);
            }
            break;
        }

        case ST_POWER_OFF:
            if (now - stateSince < POWER_OFF_MS) break;
            digitalWrite(PIN_SHT_PWR, HIGH);
            enter(ST_POWER_UP);
            break;
    }
}

const ShtReading& envReading(EnvSensor s) { return readings[s]; }

uint16_t envPowerCycles() { return powerCycles; }

// :90 (PROTOCOL.md §4.3): auto prefers the external J9 sensor (the on-board one sits next to the
// motor drivers and reads warm) and falls back to the on-board one; 1 / 2 use only that sensor.
int8_t envSelected() {
    const bool onb = readings[ENV_ONBOARD].status == SHT_OK;
    const bool ext = readings[ENV_EXTERNAL].status == SHT_OK;
    switch (settings.envSelect) {
        case ENV_SEL_ONBOARD:  return onb ? (int8_t)ENV_ONBOARD : (int8_t)-1;
        case ENV_SEL_EXTERNAL: return ext ? (int8_t)ENV_EXTERNAL : (int8_t)-1;
        default:
            if (ext) return (int8_t)ENV_EXTERNAL;
            return onb ? (int8_t)ENV_ONBOARD : (int8_t)-1;
    }
}
