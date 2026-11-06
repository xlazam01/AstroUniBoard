// Focuser block 50-69 (PROTOCOL.md §4.1, #25). Owner: session A.
// Getters take no parameters, setters exactly one; anything else is ERR,7.
#include "protocol.h"
#include "focuser.h"
#include "settings.h"

// Parses exactly one integer parameter in lo..hi. Answers ERR,7 itself and returns false on failure.
static bool oneValue(char* params, int32_t lo, int32_t hi, int32_t& v) {
    ProtoArgs a;
    if (!protoSplit(params, a) || a.n != 1 || !protoInt32(a.v[0], v) || v < lo || v > hi) {
        protoError(FW_ERR_BAD_PARAM);
        return false;
    }
    return true;
}

static bool noValue(char* params) {
    ProtoArgs a;
    if (!protoSplit(params, a) || a.n != 0) {
        protoError(FW_ERR_BAD_PARAM);
        return false;
    }
    return true;
}

static void reply(FwError e) {
    if (e == FW_OK) protoOk();
    else protoError(e);
}

void protocolFocuser(uint8_t cmd, char* params) {
    FocuserSettings& f = settings.foc;
    int32_t v;
    switch (cmd) {
    case 50:
        if (noValue(params)) protoInt(focuserPosition());
        break;
    case 51:
        if (oneValue(params, INT32_MIN, INT32_MAX, v)) reply(focuserMoveTo(v));
        break;
    case 52:
        if (oneValue(params, INT32_MIN, INT32_MAX, v)) reply(focuserMoveBy(v));
        break;
    case 53:
        if (noValue(params)) {
            focuserHalt();
            protoOk();
        }
        break;
    case 54:
        if (noValue(params)) protoInt(f.maxPos);
        break;
    case 55:
        if (oneValue(params, 1, 10000000, v)) {
            const int32_t floor = focuserBusy() && focuserTarget() > focuserPosition() ? focuserTarget()
                                                                                       : focuserPosition();
            if (v < floor) {
                protoError(FW_ERR_BAD_PARAM);
            } else {
                f.maxPos = v;
                protoOk();
            }
        }
        break;
    case 56:
        if (oneValue(params, INT32_MIN, INT32_MAX, v)) reply(focuserSync(v));
        break;
    case 58:
        if (noValue(params)) protoInt((int32_t)f.speed);
        break;
    case 59:
        if (oneValue(params, 1, 20000, v)) {
            f.speed = (uint32_t)v;
            focuserApplySettings();
            protoOk();
        }
        break;
    case 60:
        if (noValue(params)) protoInt((int32_t)f.accel);
        break;
    case 61:
        if (oneValue(params, 1, 100000, v)) {
            f.accel = (uint32_t)v;
            focuserApplySettings();
            protoOk();
        }
        break;
    case 62:
        if (noValue(params)) protoInt((int32_t)f.backlash);
        break;
    case 63:
        if (oneValue(params, 0, 10000, v)) {
            f.backlash = (uint32_t)v;
            protoOk();
        }
        break;
    case 64:
        if (noValue(params)) protoInt(f.reverse ? 1 : 0);
        break;
    case 65:
        if (oneValue(params, 0, 1, v)) {
            if (focuserBusy()) {
                protoError(FW_ERR_BUSY);
            } else {
                f.reverse = (uint8_t)v;
                focuserApplySettings();
                protoOk();
            }
        }
        break;
    case 66:
        if (noValue(params)) protoInt((int32_t)f.stepSize);
        break;
    case 67:
        if (oneValue(params, 0, 100000, v)) {
            f.stepSize = (uint32_t)v;
            protoOk();
        }
        break;
    default:            // 57, 68, 69 reserved
        protoError(FW_ERR_UNKNOWN_CMD);
        break;
    }
}
