#include "status.h"
#include "env_sht31.h"
#include "focuser.h"
#include "housekeeping.h"
#include "imu_mpu6000.h"
#include "nv_store.h"
#include "rotator.h"
#include "rotator_wrap.h"
#include "tmc2209.h"

static bool    safe;
static bool    ready;
static FwError sticky = FW_OK;

void statusBegin(bool safeMode) { safe = safeMode; ready = false; sticky = FW_OK; }
void statusSetReady() { ready = true; }
bool statusSafeMode() { return safe; }

void statusRaise(FwError code) {
    if (sticky == FW_OK) sticky = code;
}
FwError statusError() { return sticky; }
void statusClearError() {
    sticky = FW_OK;
    tmcClearLatched();
}

bool statusAnyBusy() { return focuserBusy() || rotatorBusy(); }

DeviceStatus statusGet() {
    DeviceStatus s;
    if (!ready) s.state = DEV_INIT;
    else if (rotatorHoming()) s.state = DEV_ROT_HOMING;
    else if (focuserHalting() || rotatorHalting()) s.state = DEV_HALTING;
    else if (focuserBusy()) s.state = DEV_FOC_MOVING;
    else if (rotatorBusy()) s.state = DEV_ROT_MOVING;
    else s.state = DEV_IDLE;

    s.focPos = focuserPosition();
    s.focTarget = focuserTarget();
    s.rotCdeg = wrapReported(rotatorAngle());
    const int32_t rt = rotatorTarget();
    s.rotTarget = (rt == ROT_NO_TARGET) ? -1 : wrapReported(rt);
    s.error = (uint8_t)sticky;

    uint16_t f = 0;
    if (focuserUnverified()) f |= FLAG_FOC_UNVERIFIED;
    if (rotatorUnverified()) f |= FLAG_ROT_UNVERIFIED;
    if (rotatorSuspect()) f |= FLAG_ROT_SUSPECT;
    if (nvFault()) f |= FLAG_EEPROM_FAULT;
    if (hkWarnings()) f |= FLAG_HK_WARNING;
    if (safe) {
        f |= FLAG_SAFE_MODE | FLAG_IMU_UNCAL;
    } else {
        if (envReading(ENV_ONBOARD).status == SHT_OK) f |= FLAG_SHT_ONBOARD;
        if (envReading(ENV_EXTERNAL).status == SHT_OK) f |= FLAG_SHT_EXTERNAL;
        if (imuAnswering()) f |= FLAG_IMU_OK;
        if (!imuCalibrated()) f |= FLAG_IMU_UNCAL;
    }
    s.flags = f;
    return s;
}
