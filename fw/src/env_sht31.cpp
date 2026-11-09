// SHT3x / SHT4x driver. Both sensors are auto-detected: on-board U8 (0x45) is an SHT31 on rev 2.0 / 2.1 and an
// SHT40-BD1B on rev 2.2; the external J9 sensor (0x44) is an SHT31 module or the rev 2.2 snap-off tab
// (SHT40-AD1B). Recovery of a hung sensor: U8 (either variant) runs from PB9 (SHT_PWR) and gets a supply
// power cycle; the J9 sensor runs from the plain 3V3 rail and gets a soft reset.
// SHT3x-DIS datasheet: single shot 0x2400, high repeatability, no clock stretching.
// SHT4x datasheet: one-byte commands, 0xFD measure high precision (8.3 ms max), 0x89 serial number.
// Both answer with 6 bytes T(2)+CRC, RH(2)+CRC and use the same CRC-8.
#include <Arduino.h>
#include <Wire.h>
#include <math.h>

#include "config.h"
#include "diag.h"
#include "env_sht31.h"
#include "i2c_bus.h"
#include "settings.h"

static const uint32_t ENV_PERIOD_MS     = 2000;  // measurement interval
static const uint32_t MEAS_WAIT_MS      = 20;    // SHT3x high repeatability 15.5 ms max, SHT4x high precision 8.3 ms
static const uint32_t PROBE_WAIT_MS     = 10;    // SHT4x serial number read (0x89); Sensirion's driver waits 10 ms
static const uint32_t POWER_OFF_MS      = 100;   // supply off time when recovering the sensor
static const uint32_t POWER_UP_MS       = 2;     // datasheets: 1 ms max to idle after VDD rises (both families)
static const uint8_t  FAILS_BEFORE_CYCLE = 3;    // consecutive failures of a seen sensor before a recovery

static const uint8_t ADDRS[ENV_SENSOR_COUNT] = { SHT31_ADDR_ONBOARD, SHT31_ADDR_EXTERNAL };

enum EnvState : uint8_t { ST_POWER_UP, ST_IDLE, ST_PROBING, ST_MEASURING, ST_POWER_OFF };

// Sensor family. Unknown = the sensor is probed before its next measurement: at boot, after a supply
// power cycle and after every failed measurement (the sensor on J9 may have been swapped).
enum ShtFamily : uint8_t { FAM_UNKNOWN, FAM_SHT3X, FAM_SHT4X };

static ShtReading readings[ENV_SENSOR_COUNT] = { { SHT_NO_DATA, 0, 0, 0 }, { SHT_NO_DATA, 0, 0, 0 } };
static bool       triggered[ENV_SENSOR_COUNT];
static EnvState   state;
static uint32_t   stateSince;
static uint32_t   lastStart;
static bool       seen[ENV_SENSOR_COUNT];    // answered at least once since boot
static uint8_t    fails[ENV_SENSOR_COUNT];   // consecutive failures after it was seen
static uint16_t   powerCycles;
static ShtFamily  family[ENV_SENSOR_COUNT];
static bool       probing[ENV_SENSOR_COUNT];   // probe started in this cycle, answer still to be read
static ShtFamily  lastFamily[ENV_SENSOR_COUNT];   // family of the last good reading (kept while re-detecting)

static void enter(EnvState s) { state = s; stateSince = millis(); }

static uint8_t crc8(const uint8_t* data, uint8_t len) {
    uint8_t crc = 0xFF;   // polynomial 0x31, init 0xFF
    for (uint8_t i = 0; i < len; i++) {
        crc ^= data[i];
        for (uint8_t b = 0; b < 8; b++) crc = (crc & 0x80) ? (uint8_t)((crc << 1) ^ 0x31) : (uint8_t)(crc << 1);
    }
    return crc;
}

// Writes a command; on failure records the Wire error in the diag note (on-board bits 16.., external 20..).
static bool writeCommand(uint8_t addr, const uint8_t* cmd, uint8_t len) {
    Wire.beginTransmission(addr);
    for (uint8_t i = 0; i < len; i++) Wire.write(cmd[i]);
    const uint8_t err = Wire.endTransmission();
    if (err == 0) return true;
    // STM32duino Wire (core 2.12, G0, interrupt mode): after a NACK from an absent device the next transfer
    // to a present one never completes and starves the main loop (found on the board 2026-11-06,
    // on-board U8 not fitted). Restarting the peripheral after every failed transfer fixes it.
    diagNoteOr((uint32_t)err << (addr == SHT31_ADDR_ONBOARD ? 16 : 20));
    i2cRestart();
    return false;
}

// Reads the 6-byte answer (word + CRC, word + CRC) into w[2].
static ShtStatus readWords(uint8_t addr, uint16_t w[2]) {
    uint8_t buf[6];
    if (Wire.requestFrom(addr, (uint8_t)6) != 6) {
        i2cRestart();   // same STM32duino Wire workaround as in writeCommand()
        return SHT_NO_ACK;
    }
    for (uint8_t i = 0; i < 6; i++) buf[i] = (uint8_t)Wire.read();
    if (crc8(buf, 2) != buf[2] || crc8(buf + 3, 2) != buf[5]) return SHT_BAD_CRC;
    w[0] = (uint16_t)((buf[0] << 8) | buf[1]);
    w[1] = (uint16_t)((buf[3] << 8) | buf[4]);
    return SHT_OK;
}

static bool startMeasurement(EnvSensor s) {
    static const uint8_t CMD_SHT3X[2] = { 0x24, 0x00 };   // single shot, high repeatability, no clock stretching
    static const uint8_t CMD_SHT4X[1] = { 0xFD };         // measure T + RH, high precision
    if (family[s] == FAM_SHT4X) return writeCommand(ADDRS[s], CMD_SHT4X, 1);
    return writeCommand(ADDRS[s], CMD_SHT3X, 2);
}

// Probe, step 1: ask for the SHT4x serial number. An SHT3x takes the single byte as an incomplete
// command and has no data to send, so it NACKs the read in step 2.
static bool startProbe(EnvSensor s) {
    static const uint8_t CMD_SERIAL[1] = { 0x89 };
    return writeCommand(ADDRS[s], CMD_SERIAL, 1);
}

// Step 2: 6 valid bytes = SHT4x, no answer = SHT3x. A CRC error leaves the family unknown (probe again).
static void finishProbe(EnvSensor s) {
    uint16_t w[2];
    const ShtStatus st = readWords(ADDRS[s], w);
    if (st == SHT_OK)          family[s] = FAM_SHT4X;
    else if (st == SHT_NO_ACK) family[s] = FAM_SHT3X;
    else                       readings[s].status = SHT_BAD_CRC;
}

// Soft reset (SHT4x 0x94, 1 ms; SHT3x 0x30A2, 1.5 ms), done long before the next cycle 2 s later.
static void softReset(EnvSensor s) {
    static const uint8_t CMD_SHT3X[2] = { 0x30, 0xA2 };
    static const uint8_t CMD_SHT4X[1] = { 0x94 };
    if (lastFamily[s] == FAM_SHT4X) writeCommand(ADDRS[s], CMD_SHT4X, 1);
    else                            writeCommand(ADDRS[s], CMD_SHT3X, 2);
}

// Triggers a measurement on every sensor whose family is known. A sensor still unknown was not detected
// in this cycle: no answer to the probe (status 1) or a CRC error in it (status 2, set by finishProbe()).
static void startAll() {
    for (uint8_t i = 0; i < ENV_SENSOR_COUNT; i++) {
        diagMark((DiagMark)(DIAG_ENV_START0 + i));
        const bool known = family[i] != FAM_UNKNOWN;
        triggered[i] = known && startMeasurement((EnvSensor)i);
        if (!triggered[i] && (known || readings[i].status != SHT_BAD_CRC)) readings[i].status = SHT_NO_ACK;
    }
    enter(ST_MEASURING);
}

static void readMeasurement(EnvSensor s) {
    ShtReading& r = readings[s];
    uint16_t w[2];
    r.status = readWords(ADDRS[s], w);
    if (r.status != SHT_OK) return;

    const float t = -45.0f + 175.0f * (float)w[0] / 65535.0f;   // same for both families
    const float rh = (family[s] == FAM_SHT4X)
                         ? fminf(fmaxf(-6.0f + 125.0f * (float)w[1] / 65535.0f, 0.0f), 100.0f)
                         : 100.0f * (float)w[1] / 65535.0f;
    const float g  = logf(fmaxf(rh, 0.1f) / 100.0f) + 17.62f * t / (243.12f + t);
    const float dp = 243.12f * g / (17.62f - g);

    r.tDeciC   = (int16_t)lroundf(t * 10.0f);
    r.rhDeci   = (uint16_t)lroundf(rh * 10.0f);
    r.dewDeciC = (int16_t)lroundf(dp * 10.0f);
}

void envBegin() {
    for (uint8_t i = 0; i < ENV_SENSOR_COUNT; i++) {
        readings[i] = { SHT_NO_DATA, 0, 0, 0 };
        family[i]   = FAM_UNKNOWN;
    }
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
            {
                bool any = false;
                for (uint8_t i = 0; i < ENV_SENSOR_COUNT; i++) {
                    // A sensor that does not answer the probe is absent: startAll() skips it this cycle.
                    if (family[i] == FAM_UNKNOWN) {
                        diagMark(DIAG_ENV_PROBE);
                        probing[i] = startProbe((EnvSensor)i);
                    } else {
                        probing[i] = false;
                    }
                    any = any || probing[i];
                }
                if (any) { enter(ST_PROBING); break; }
            }
            startAll();
            break;

        case ST_PROBING:
            if (now - stateSince < PROBE_WAIT_MS) break;
            for (uint8_t i = 0; i < ENV_SENSOR_COUNT; i++) {
                if (!probing[i]) continue;
                diagMark(DIAG_ENV_PROBE);
                finishProbe((EnvSensor)i);
            }
            startAll();
            break;

        case ST_MEASURING: {
            if (now - stateSince < MEAS_WAIT_MS) break;
            for (uint8_t i = 0; i < ENV_SENSOR_COUNT; i++) {
                diagMark((DiagMark)(DIAG_ENV_READ0 + i));
                if (triggered[i]) readMeasurement((EnvSensor)i);
            }
            // Either sensor may be absent (on-board U8 not fitted, nothing on J9). A sensor that answered
            // before and now fails repeatedly has hung.
            bool hung[ENV_SENSOR_COUNT] = { false, false };
            for (uint8_t i = 0; i < ENV_SENSOR_COUNT; i++) {
                if (readings[i].status == SHT_OK) { seen[i] = true; fails[i] = 0; lastFamily[i] = family[i]; }
                else if (seen[i] && ++fails[i] >= FAILS_BEFORE_CYCLE) { hung[i] = true; fails[i] = 0; }
                if (readings[i].status != SHT_OK) family[i] = FAM_UNKNOWN;   // re-detect before the next try
            }
            // J9 pin 3 is the plain 3V3 rail, not SHT_VDD: a PB9 power cycle does not reach the external
            // sensor. Soft-reset it instead, with the command of the family it last answered as.
            if (hung[ENV_EXTERNAL]) softReset(ENV_EXTERNAL);
            if (hung[ENV_ONBOARD]) {   // U8 runs from PB9 (SHT_VDD): cycle its supply
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
