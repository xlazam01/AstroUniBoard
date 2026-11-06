// AstroUniBoard firmware V2 — superloop. Plan: docs/V2_PLAN.md, protocol: docs/PROTOCOL.md
// Owner: orchestrator. Each module has begin() + update(); nothing blocks the loop for long
// (IWDG 4 s, diag.h). ISRs only touch the step timer (step_timer.h).
#include <Arduino.h>
#include <Wire.h>

#include "axis.h"
#include "config.h"
#include "diag.h"
#include "env_sht31.h"
#include "focuser.h"
#include "housekeeping.h"
#include "imu_mpu6000.h"
#include "leds.h"
#include "nv_store.h"
#include "protocol.h"
#include "rotator.h"
#include "settings.h"
#include "status.h"
#include "step_timer.h"
#include "tmc2209.h"

// Answer even when the host opens the port with DTR = false (same as OpenWheel).
extern "C" void CDC_enableDTR(bool enable);

static Protocol protocol;
static bool safeMode;   // last reset came from the watchdog: run without the I2C devices

void setup() {
    diagBoot();         // reset cause + last progress marker, starts the watchdog
    safeMode = diagWatchdogReset();
    statusBegin(safeMode);
    diagMark(DIAG_SETUP);

    settingsDefaults(settings);

    // Motor drivers off until an axis moves (EN is active LOW).
    axisFoc.begin();
    axisRot.begin();
    ledsBegin();

    CDC_enableDTR(false);
    PROTOCOL_SERIAL.begin(115200);

    if (!safeMode) {
        diagMark(DIAG_WIRE_BEGIN);
        Wire.setSDA(PIN_I2C_SDA);
        Wire.setSCL(PIN_I2C_SCL);
        Wire.begin();
        diagMark(DIAG_NV_BEGIN);
        nvBegin();                      // loads `settings` (defaults when missing or bad)
        diagMark(DIAG_IMU_BEGIN);
        imuBegin();
        diagMark(DIAG_ENV_BEGIN);
        envBegin();
    }
    ledsSetBrightness(settings.ledBrightness);

    diagMark(DIAG_TMC_BEGIN);
    stepTimerBegin();
    tmcBegin();
    diagMark(DIAG_AXES_BEGIN);
    focuserBegin();
    rotatorBegin();
    hkBegin();
    statusSetReady();
}

void loop() {
    diagFeed();

    diagMark(DIAG_PROTOCOL);
    protocol.process();

    diagMark(DIAG_FOC_UPDATE);
    axisFoc.update();
    focuserUpdate();
    diagMark(DIAG_ROT_UPDATE);
    axisRot.update();
    rotatorUpdate();
    diagMark(DIAG_TMC_UPDATE);
    tmcUpdate();

    if (!safeMode) {
        diagMark(DIAG_IMU_UPDATE);
        imuUpdate();
        diagMark(DIAG_ENV_UPDATE);
        envUpdate();
        diagMark(DIAG_NV_UPDATE);
        nvUpdate();
    }
    diagMark(DIAG_HK_UPDATE);
    hkUpdate();
    diagMark(DIAG_LED_UPDATE);
    ledsUpdate();
}
