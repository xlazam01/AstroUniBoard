// Housekeeping (#18): MCU die temperature and VDDA from the internal ADC channels, using the factory
// calibration (VREFINT_CAL, TS_CAL1/TS_CAL2 taken at VDDA = 3.0 V), as OpenWheel. Sampled only while
// no axis moves (the ADC reads take a few ms), every HK_INTERVAL_MS.
#include <Arduino.h>

#include "clock_config.h"
#include "housekeeping.h"
#include "status.h"
#include "tmc2209.h"

#define HK_INTERVAL_MS  1500
#define HK_SAMPLES      16

static const uint32_t CAL_VDDA_MV = 3000;   // VREFINT_CAL / TS_CAL are taken at VDDA = 3.0 V

static int16_t  mcuTemp = 0;
static uint16_t vdda = 0;
static uint32_t lastSample = 0;

static void sampleAll() {
    // 16 conversions of each internal channel (single readings jitter by about +-1 %)
    uint32_t ref = 0;
    for (uint8_t i = 0; i < HK_SAMPLES; i++) ref += analogRead(AVREF);
    if (ref == 0) return;
    vdda = (uint16_t)((uint32_t)(*VREFINT_CAL_ADDR) * CAL_VDDA_MV * HK_SAMPLES / ref);

    // scale the temperature reading to the 3.0 V calibration conditions
    uint32_t raw = 0;
    for (uint8_t i = 0; i < HK_SAMPLES; i++) raw += analogRead(ATEMP);
    const int32_t ts = (int32_t)(raw * vdda / CAL_VDDA_MV / HK_SAMPLES);
    const int32_t cal1 = *TEMPSENSOR_CAL1_ADDR;
    const int32_t cal2 = *TEMPSENSOR_CAL2_ADDR;
    if (cal2 != cal1) {
        mcuTemp = (int16_t)((ts - cal1) * (TEMPSENSOR_CAL2_TEMP - TEMPSENSOR_CAL1_TEMP) * 10 / (cal2 - cal1) +
                            TEMPSENSOR_CAL1_TEMP * 10);
    }
}

void hkBegin() {
    analogReadResolution(12);
    sampleAll();
    lastSample = millis();
}

void hkUpdate() {
    if (statusAnyBusy()) return;
    const uint32_t now = millis();
    if (now - lastSample < HK_INTERVAL_MS) return;
    lastSample = now;
    sampleAll();
}

int16_t hkMcuTempDeciC() { return mcuTemp; }
uint16_t hkVddaMv() { return vdda; }

uint8_t hkWarnings() {
    return (uint8_t)((tmcWarnings() & (HK_WARN_DRV_OTPW | HK_WARN_DRV_RESET)) |
                     (clockFromCrystal() ? 0 : HK_WARN_NO_CRYSTAL));
}
