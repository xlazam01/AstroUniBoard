// Common block 20-49 (PROTOCOL.md §3). Owner: session D.
#include <Arduino.h>
#include <stdio.h>

#include "bootloader.h"
#include "config.h"
#include "diag.h"
#include "focuser.h"
#include "housekeeping.h"
#include "leds.h"
#include "nv_store.h"
#include "protocol.h"
#include "rotator.h"
#include "settings.h"
#include "status.h"
#include "tmc2209.h"

#define DRV_IDLE_OFF_MAX_S  3600   // :48 idle_off upper limit (1 h; longer = just use 0, stay powered)

// Same string as the USB iSerialNumber of the stm32duino core (USBDevice/src/usbd_desc.c,
// Get_SerialNum): UID0 + UID2 as 8 hex digits, then the top 16 bits of UID1 as 4 hex digits.
static void deviceSerial(char* out, size_t len) {
    const uint32_t w0 = HAL_GetUIDw0() + HAL_GetUIDw2();
    const uint32_t w1 = HAL_GetUIDw1();
    snprintf(out, len, "%08lX%04lX", (unsigned long)w0, (unsigned long)(w1 >> 16));
}

static bool isPowerOfTwo(int32_t v) { return v > 0 && (v & (v - 1)) == 0; }

// Microstep change on one axis: rescale everything counted in steps first (it still sees the old value)
static void rescaleAxis(AxisId axis, uint16_t oldUsteps, uint16_t newUsteps) {
    if (oldUsteps == newUsteps) return;
    if (axis == AXIS_FOC) focuserRescale(oldUsteps, newUsteps);
    else rotatorRescale(oldUsteps, newUsteps);
}

static void applyAxis(AxisId axis) {
    tmcApply(axis);
    if (axis == AXIS_FOC) focuserApplySettings();
    else rotatorApplySettings();
}

// Parses "<axis>" as the only value. Answers ERR,7 itself and returns -1 on failure.
static int axisOnly(const ProtoArgs& a) {
    const int axis = (a.n == 1) ? protoAxis(a.v[0]) : -1;
    if (axis < 0) protoError(FW_ERR_BAD_PARAM);
    return axis;
}

static void cmdResetDefaults() {
    if (statusAnyBusy()) { protoError(FW_ERR_BUSY); return; }
    Settings d;
    settingsDefaults(d);
    rescaleAxis(AXIS_FOC, settings.drv[AXIS_FOC].microsteps, d.drv[AXIS_FOC].microsteps);
    rescaleAxis(AXIS_ROT, settings.drv[AXIS_ROT].microsteps, d.drv[AXIS_ROT].microsteps);
    settingsDefaults(settings);
    applyAxis(AXIS_FOC);
    applyAxis(AXIS_ROT);
    ledsSetBrightness(settings.ledBrightness);
    protoOk();
}

static void cmdHousekeeping() {
    const int16_t tf = tmcTempClass(AXIS_FOC);
    const int16_t tr = tmcTempClass(AXIS_ROT);
    const int16_t worst = (tf < 0 || tr < 0) ? -1 : (tf > tr ? tf : tr);
    protoPrintf("H,%d,%u,-1,-1,%d,%X,%d,%d#", (int)hkMcuTempDeciC(), (unsigned)hkVddaMv(), (int)worst,
                (unsigned)hkWarnings(), (int)tf, (int)tr);
}

// :48,<axis>,run,hold,usteps,mode,idle_off
static void cmdSetDriver(const ProtoArgs& a) {
    if (a.n != 6) { protoError(FW_ERR_BAD_PARAM); return; }
    const int axis = protoAxis(a.v[0]);
    int32_t run, hold, usteps, mode, idleOff;
    if (axis < 0 || !protoInt32(a.v[1], run) || !protoInt32(a.v[2], hold) || !protoInt32(a.v[3], usteps) ||
        !protoInt32(a.v[4], mode) || !protoInt32(a.v[5], idleOff)) {
        protoError(FW_ERR_BAD_PARAM);
        return;
    }
    if (run < 1 || run > TMC_MAX_RUN_MA || hold < 0 || hold > run || !isPowerOfTwo(usteps) || usteps > 256 ||
        (mode != DRV_STEALTHCHOP && mode != DRV_SPREADCYCLE) || idleOff < 0 || idleOff > DRV_IDLE_OFF_MAX_S) {
        protoError(FW_ERR_BAD_PARAM);
        return;
    }
    if (statusAnyBusy()) { protoError(FW_ERR_BUSY); return; }

    const AxisId ax = (AxisId)axis;
    DriverConfig& d = settings.drv[ax];
    rescaleAxis(ax, d.microsteps, (uint16_t)usteps);
    d.runMa = (uint16_t)run;
    d.holdMa = (uint16_t)hold;
    d.microsteps = (uint16_t)usteps;
    d.mode = (uint8_t)mode;
    d.idleOffS = (uint16_t)idleOff;
    applyAxis(ax);
    protoOk();
}

void protocolCommon(uint8_t cmd, char* params) {
    ProtoArgs a;
    if (!protoSplit(params, a)) { protoError(FW_ERR_BAD_PARAM); return; }

    switch (cmd) {
        case 22:  // reboot
        case 31:  // reboot into the USB DFU bootloader (magic word + reset)
            protoOk();
            PROTOCOL_SERIAL.flush();
            delay(50);   // let the USB host fetch the reply before the device drops off the bus
            if (cmd == 31) rebootToDfu();
            rebootNormal();
            break;

        case 23:  // write settings to the EEPROM
            if (a.n != 0) { protoError(FW_ERR_BAD_PARAM); break; }
            if (nvSaveSettings() == FW_OK) protoOk();
            else protoError(FW_ERR_EEPROM);
            break;

        case 24:  // running settings back to the compiled-in defaults
            if (a.n != 0) { protoError(FW_ERR_BAD_PARAM); break; }
            cmdResetDefaults();
            break;

        case 25:  // ping
            protoOk();
            break;

        case 30:  // halt both axes (decelerate)
            if (a.n != 0) { protoError(FW_ERR_BAD_PARAM); break; }
            focuserHalt();
            rotatorHalt();
            protoOk();
            break;

        case 32: {  // identify
            if (a.n != 0) { protoError(FW_ERR_BAD_PARAM); break; }
            char serial[16];
            deviceSerial(serial, sizeof(serial));
            protoPrintf("ASTROUNIBOARD,%s,%s,%d,%s,%X#", BOARD_MODEL, FIRMWARE_ID, PROTOCOL_VERSION, serial,
                        (unsigned)FIRMWARE_CAPS);
            protoSetIdentified();   // unlocks every other command (#58)
            break;
        }

        case 33: {  // status
            if (a.n != 0) { protoError(FW_ERR_BAD_PARAM); break; }
            const DeviceStatus s = statusGet();
            protoPrintf("S,%u,%ld,%ld,%ld,%ld,%u,%X#", (unsigned)s.state, (long)s.focPos, (long)s.focTarget,
                        (long)s.rotCdeg, (long)s.rotTarget, (unsigned)s.error, (unsigned)s.flags);
            break;
        }

        case 34: {  // read / clear the sticky error
            if (a.n == 0) { protoPrintf("E,%u#", (unsigned)statusError()); break; }
            int32_t v;
            if (a.n != 1 || !protoInt32(a.v[0], v) || v != 0) { protoError(FW_ERR_BAD_PARAM); break; }
            statusClearError();
            protoOk();
            break;
        }

        case 40:  // housekeeping
            if (a.n != 0) { protoError(FW_ERR_BAD_PARAM); break; }
            cmdHousekeeping();
            break;

        case 41: {  // StallGuard readings: G,<thr>,<last>,<min>,<max>#
            const int axis = axisOnly(a);
            if (axis < 0) break;
            StallStats st;
            tmcStallStats((AxisId)axis, st);
            protoPrintf("G,%u,%d,%d,%d#", (unsigned)st.threshold, (int)st.last, (int)st.min, (int)st.max);
            break;
        }

        case 42: {  // StallGuard threshold
            int32_t thr;
            const int axis = (a.n == 2) ? protoAxis(a.v[0]) : -1;
            if (axis < 0 || !protoInt32(a.v[1], thr) || thr < 0 || thr > 255) { protoError(FW_ERR_BAD_PARAM); break; }
            if (statusAnyBusy()) { protoError(FW_ERR_BUSY); break; }
            const FwError e = tmcSetStallThreshold((AxisId)axis, (uint8_t)thr);
            if (e == FW_OK) protoOk();
            else protoError(e);
            break;
        }

        case 43:  // LED brightness
            if (a.n != 0) { protoError(FW_ERR_BAD_PARAM); break; }
            protoInt(ledsBrightness());
            break;

        case 44: {  // set LED brightness 0..100
            int32_t v;
            if (a.n != 1 || !protoInt32(a.v[0], v) || v < 0 || v > 100) { protoError(FW_ERR_BAD_PARAM); break; }
            if (statusAnyBusy()) { protoError(FW_ERR_BUSY); break; }
            ledsSetBrightness((uint8_t)v);
            protoOk();
            break;
        }

        case 46:  // reset and safe-mode info: R,<reset flags hex>,<last marker>,<safe mode>#
            protoPrintf("R,%02lX,%u,%u#", (unsigned long)diagResetFlags(), (unsigned)diagLastMark(),
                        diagWatchdogReset() ? 1u : 0u);
            break;

        case 47: {  // motor driver config: D,<run>,<hold>,<usteps>,<mode>,<idle_off>#
            const int axis = axisOnly(a);
            if (axis < 0) break;
            const DriverConfig& d = settings.drv[axis];
            protoPrintf("D,%u,%u,%u,%u,%u#", (unsigned)d.runMa, (unsigned)d.holdMa, (unsigned)d.microsteps,
                        (unsigned)d.mode, (unsigned)d.idleOffS);
            break;
        }

        case 48:
            cmdSetDriver(a);
            break;

        default:  // 20, 21, 26-29, 35-39, 45, 49
            protoError(FW_ERR_UNKNOWN_CMD);
            break;
    }
}
