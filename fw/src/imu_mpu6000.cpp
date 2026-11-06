// MPU-6000 accelerometer on the shared I2C bus (#31) + IMU zero calibration glue (#32).
// Datasheet / register map: InvenSense PS-MPU-6000A rev 3.4, RM-MPU-6000A rev 4.2.
//
// HARDWARE CAVEAT (rev 1): the MPU-6000 selects SPI when /CS is low at power-up and then ignores I2C.
// Rev 2 ties /CS to +3.3V; on rev 1 it floats, so I2C mode is luck and must be verified on the board
// (:33# flag 6, :94#). The driver re-probes every PROBE_RETRY_MS, so a chip that answers late is
// picked up without a reboot.
//
// Non-blocking: imuUpdate() does at most one short I2C transaction per call (a 6-byte burst every
// 20 ms, ~1 ms at 100 kHz), except a (re)configuration (7 short register transfers).
// The gyro is put in standby (not needed) and the temperature sensor is off.
#include <Arduino.h>
#include <Wire.h>

#include "config.h"
#include "i2c_bus.h"
#include "imu_math.h"
#include "imu_mpu6000.h"

// Registers
static const uint8_t REG_SMPLRT_DIV   = 0x19;
static const uint8_t REG_CONFIG       = 0x1A;
static const uint8_t REG_ACCEL_CONFIG = 0x1C;
static const uint8_t REG_ACCEL_XOUT_H = 0x3B;
static const uint8_t REG_PWR_MGMT_1   = 0x6B;
static const uint8_t REG_PWR_MGMT_2   = 0x6C;
static const uint8_t REG_WHO_AM_I     = 0x75;

static const uint8_t WHO_AM_I_VALUE   = 0x68;
static const uint8_t PWR1_RUN         = 0x08;   // SLEEP = 0, CYCLE = 0, TEMP_DIS = 1, CLKSEL = 0 (internal 8 MHz)
static const uint8_t PWR2_GYRO_STBY   = 0x07;   // STBY_XG | STBY_YG | STBY_ZG, accelerometer on
static const uint8_t CONFIG_DLPF_10HZ = 0x05;   // DLPF_CFG 5: accel 10 Hz bandwidth, 1 kHz internal rate
static const uint8_t SMPLRT_DIV_50HZ  = 19;     // 1 kHz / (1 + 19) = 50 Hz
static const uint8_t ACCEL_2G         = 0x00;   // AFS_SEL 0: +-2 g, 16384 LSB/g

static const float    MG_PER_LSB      = 1000.0f / 16384.0f;

static const uint32_t SAMPLE_MS       = 20;     // read period (matches the 50 Hz output rate)
static const uint32_t PROBE_RETRY_MS  = 1000;   // re-probe while not answering
static const uint32_t CHECK_MS        = 1000;   // check PWR_MGMT_1 (chip reset / fell asleep -> reconfigure)
// Not answering only after this long without a good transfer: the SHT31 supply power cycle
// (env_sht31.cpp, 100 ms) can hold the shared bus low for a moment.
static const uint32_t LOST_MS         = 500;
static const float    FILTER_ALPHA    = 0.25f;  // EMA at 50 Hz: time constant ~70 ms
static const uint16_t SETTLE_SAMPLES  = 40;     // 0.8 s after imuRestartFilter() (> 10 time constants)

static ImuCalibration cal;
static bool     begun;            // imuBegin() ran (not in safe mode)
static bool     answering;
static bool     haveSample;       // filt holds a value since the chip (re)started answering
static ImuV3    filt;             // low-pass filtered acceleration, mg
static uint16_t freshSamples;     // samples since imuRestartFilter()
static uint32_t lastSample, lastGood, lastProbe, lastCheck;
static bool     cap0Ok, cap90Ok, cap180Ok;
static ImuV3    cap0, cap90, cap180;

// STM32duino Wire (G0): after a failed transfer the next one can hang; restart the peripheral
// (found on the board 2026-11-06, see i2c_bus.h).
static void busRestart() { i2cRestart(); }

static bool writeReg(uint8_t reg, uint8_t value) {
    Wire.beginTransmission(MPU6000_ADDR);
    Wire.write(reg);
    Wire.write(value);
    if (Wire.endTransmission() != 0) { busRestart(); return false; }
    return true;
}

static bool readRegs(uint8_t reg, uint8_t* buf, uint8_t n) {
    Wire.beginTransmission(MPU6000_ADDR);
    Wire.write(reg);
    if (Wire.endTransmission(false) != 0) { busRestart(); return false; }   // repeated start
    if (Wire.requestFrom((uint8_t)MPU6000_ADDR, n) != n) { busRestart(); return false; }
    for (uint8_t i = 0; i < n; i++) buf[i] = (uint8_t)Wire.read();
    return true;
}

// Probe WHO_AM_I and configure. No DEVICE_RESET (it would need a 100 ms wait): every register
// that matters is written explicitly, PWR_MGMT_1 is read back.
static bool configure() {
    uint8_t v;
    if (!readRegs(REG_WHO_AM_I, &v, 1) || v != WHO_AM_I_VALUE) return false;
    if (!writeReg(REG_PWR_MGMT_1, PWR1_RUN)) return false;          // wake first
    if (!writeReg(REG_PWR_MGMT_2, PWR2_GYRO_STBY)) return false;
    if (!writeReg(REG_CONFIG, CONFIG_DLPF_10HZ)) return false;
    if (!writeReg(REG_SMPLRT_DIV, SMPLRT_DIV_50HZ)) return false;
    if (!writeReg(REG_ACCEL_CONFIG, ACCEL_2G)) return false;
    if (!readRegs(REG_PWR_MGMT_1, &v, 1) || v != PWR1_RUN) return false;
    return true;
}

static void startAnswering(uint32_t now) {
    answering  = true;
    haveSample = false;
    freshSamples = 0;
    lastGood = lastSample = lastCheck = now;
}

static void lose(uint32_t now) {
    answering  = false;
    haveSample = false;
    freshSamples = 0;
    cap0Ok = cap90Ok = cap180Ok = false;
    lastProbe = now;
}

bool imuBegin() {
    begun = true;
    if (!nvLoadImuCal(cal) || !imuMathCalValid(cal)) cal = ImuCalibration{};
    const uint32_t now = millis();
    lastProbe = now;
    if (configure()) startAnswering(now);
    else lose(now);
    return answering;
}

void imuUpdate() {
    if (!begun) return;
    const uint32_t now = millis();

    if (!answering) {
        if (now - lastProbe < PROBE_RETRY_MS) return;
        lastProbe = now;
        if (configure()) startAnswering(now);
        return;
    }

    if (now - lastCheck >= CHECK_MS) {          // at most one transfer per call
        lastCheck = now;
        uint8_t v;
        if (readRegs(REG_PWR_MGMT_1, &v, 1)) {
            lastGood = now;
            if (v != PWR1_RUN) {                // chip was reset (brown-out) or put to sleep
                if (configure()) freshSamples = 0;
            }
        } else if (now - lastGood >= LOST_MS) {
            lose(now);
        }
        return;
    }

    if (now - lastSample < SAMPLE_MS) return;
    lastSample = now;
    uint8_t b[6];
    if (!readRegs(REG_ACCEL_XOUT_H, b, 6)) {
        if (now - lastGood >= LOST_MS) lose(now);
        return;
    }
    lastGood = now;
    const ImuV3 s{ (float)(int16_t)((b[0] << 8) | b[1]) * MG_PER_LSB,
                   (float)(int16_t)((b[2] << 8) | b[3]) * MG_PER_LSB,
                   (float)(int16_t)((b[4] << 8) | b[5]) * MG_PER_LSB };
    if (!haveSample || freshSamples == 0) {
        filt = s;                               // seed after (re)start or imuRestartFilter()
        haveSample = true;
    } else {
        filt.x += FILTER_ALPHA * (s.x - filt.x);
        filt.y += FILTER_ALPHA * (s.y - filt.y);
        filt.z += FILTER_ALPHA * (s.z - filt.z);
    }
    if (freshSamples < 0xFFFF) freshSamples++;
}

bool imuAnswering() { return answering; }

bool imuCalibrated() { return cal.valid == 1; }

static bool live() { return answering && haveSample; }

bool imuAccel(ImuVec& mg) {
    if (!live()) { mg = ImuVec{ 0, 0, 0 }; return false; }
    mg.x = (int16_t)lroundf(filt.x);
    mg.y = (int16_t)lroundf(filt.y);
    mg.z = (int16_t)lroundf(filt.z);
    return true;
}

int32_t imuRollCdeg() {
    if (!live() || !imuCalibrated()) return IMU_INVALID;
    const int32_t r = imuMathRollCdeg(cal, filt);
    return r == IMU_MATH_INVALID ? IMU_INVALID : r;
}

int32_t imuTiltCdeg() {
    if (!live() || !imuCalibrated()) return IMU_INVALID;
    const int32_t t = imuMathTiltCdeg(cal, filt);
    return t == IMU_MATH_INVALID ? IMU_INVALID : t;
}

bool imuNearVertical() {
    return live() && imuCalibrated() && imuMathNearVertical(cal, filt);
}

void imuRestartFilter() { freshSamples = 0; }

bool imuSettled() { return live() && freshSamples >= SETTLE_SAMPLES; }

bool imuCalCaptureG0() {
    cap0Ok = cap90Ok = cap180Ok = false;        // a new sequence: drop old captures
    if (!imuSettled()) return false;
    cap0 = filt;
    cap0Ok = true;
    return true;
}

bool imuCalCaptureG90() {
    cap90Ok = false;
    if (!cap0Ok || !imuSettled()) return false;
    cap90 = filt;
    cap90Ok = true;
    return true;
}

bool imuCalCaptureG180() {
    cap180Ok = false;
    if (!cap0Ok || !cap90Ok || !imuSettled()) return false;
    cap180 = filt;
    cap180Ok = true;
    return true;
}

// On success the new calibration is used at once, also when the EEPROM write fails (the error is
// still returned, so the rotator can report it; the calibration is then lost at the next reset).
FwError imuCalFinish() {
    if (!cap0Ok || !cap90Ok || !cap180Ok) return FW_ERR_CALIB_FAILED;
    cap0Ok = cap90Ok = cap180Ok = false;
    ImuCalibration c;
    const FwError e = imuMathCalibrate(cap0, cap90, cap180, c);
    if (e != FW_OK) return e;
    cal = c;
    return nvSaveImuCal(cal);
}

const ImuCalibration& imuCalData() { return cal; }
