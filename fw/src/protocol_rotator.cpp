// Rotator block 70-89 (PROTOCOL.md §4.2). Owner: session B.
// Argument rules: a command without parameters rejects any value with ERR,7; a setter needs exactly
// its values (missing, malformed, extra or out of range -> ERR,7).
#include "protocol.h"
#include "rotator.h"
#include "settings.h"
#include "status.h"

static void reply(FwError e) {
    if (e == FW_OK) protoOk();
    else protoError(e);
}

// Exactly one integer in lo..hi
static bool oneValue(const ProtoArgs& a, int32_t lo, int32_t hi, int32_t& v) {
    return a.n == 1 && protoInt32(a.v[0], v) && v >= lo && v <= hi;
}

void protocolRotator(uint8_t cmd, char* params) {
    ProtoArgs a;
    if (!protoSplit(params, a)) {
        if (cmd == 73) rotatorHalt();
        protoError(FW_ERR_BAD_PARAM);
        return;
    }

    // getters and actions without parameters (:73 halts whatever follows it: stopping wins)
    switch (cmd) {
        case 70: case 74: case 77: case 78: case 80: case 82: case 84: case 86: case 88:
            if (a.n != 0) { protoError(FW_ERR_BAD_PARAM); return; }
            break;
        default:
            break;
    }

    int32_t v = 0;
    switch (cmd) {
        case 70:  // signed mechanical angle
            protoInt(rotatorAngle());
            break;

        case 71:  // move to 0..35999 with the cable-wrap rule
            if (a.n != 1 || !protoInt32(a.v[0], v)) { protoError(FW_ERR_BAD_PARAM); break; }
            reply(rotatorMoveTo(v));
            break;

        case 72:  // move relative, target inside min..max
            if (a.n != 1 || !protoInt32(a.v[0], v)) { protoError(FW_ERR_BAD_PARAM); break; }
            reply(rotatorMoveBy(v));
            break;

        case 73:  // halt (also aborts IMU homing / calibration)
            rotatorHalt();
            protoOk();
            break;

        case 74:  // cable-wrap range
            protoPrintf("%ld,%ld#", (long)settings.rot.wrapMin, (long)settings.rot.wrapMax);
            break;

        case 75: {  // set cable-wrap range: min -36000..0, max 0..36000, current angle inside
            int32_t mn, mx;
            if (a.n != 2 || !protoInt32(a.v[0], mn) || !protoInt32(a.v[1], mx) ||
                mn < -36000 || mn > 0 || mx < 0 || mx > 36000) {
                protoError(FW_ERR_BAD_PARAM);
                break;
            }
            if (statusAnyBusy()) { protoError(FW_ERR_BUSY); break; }
            const int32_t ang = rotatorAngle();
            if (ang < mn || ang > mx) { protoError(FW_ERR_BAD_PARAM); break; }
            settings.rot.wrapMin = mn;
            settings.rot.wrapMax = mx;
            protoOk();
            break;
        }

        case 76:  // sync: set the signed angle, no motion, clears flag 1
            if (a.n != 1 || !protoInt32(a.v[0], v)) { protoError(FW_ERR_BAD_PARAM); break; }
            reply(rotatorSync(v));
            break;

        case 77:  // IMU home
            reply(rotatorImuHome());
            break;

        case 78:
            protoInt((int32_t)settings.rot.speed);
            break;
        case 79:  // speed, takes effect at the next move
            if (!oneValue(a, 10, 3600, v)) { protoError(FW_ERR_BAD_PARAM); break; }
            settings.rot.speed = (uint32_t)v;
            rotatorApplySettings();
            protoOk();
            break;

        case 80:
            protoInt((int32_t)settings.rot.accel);
            break;
        case 81:
            if (!oneValue(a, 10, 36000, v)) { protoError(FW_ERR_BAD_PARAM); break; }
            settings.rot.accel = (uint32_t)v;
            rotatorApplySettings();
            protoOk();
            break;

        case 82:
            protoInt(settings.rot.reverse ? 1 : 0);
            break;
        case 83:
            if (!oneValue(a, 0, 1, v)) { protoError(FW_ERR_BAD_PARAM); break; }
            if (statusAnyBusy()) { protoError(FW_ERR_BUSY); break; }
            settings.rot.reverse = (uint8_t)v;
            rotatorApplySettings();
            protoOk();
            break;

        case 84:
            protoInt((int32_t)settings.rot.stepsPer1000Deg);
            break;
        case 85:  // steps per 1000 deg: the angle stays, the step position is recomputed
            if (!oneValue(a, 1000, 10000000, v)) { protoError(FW_ERR_BAD_PARAM); break; }
            if (statusAnyBusy()) { protoError(FW_ERR_BUSY); break; }
            settings.rot.stepsPer1000Deg = (uint32_t)v;
            rotatorApplySettings();
            protoOk();
            break;

        case 86:
            protoInt(settings.rot.bootHome ? 1 : 0);
            break;
        case 87:
            if (!oneValue(a, 0, 1, v)) { protoError(FW_ERR_BAD_PARAM); break; }
            settings.rot.bootHome = (uint8_t)v;
            protoOk();
            break;

        case 88:
            protoInt((int32_t)settings.rot.homeTolCdeg);
            break;
        case 89:
            if (!oneValue(a, 5, 500, v)) { protoError(FW_ERR_BAD_PARAM); break; }
            settings.rot.homeTolCdeg = (uint16_t)v;
            protoOk();
            break;

        default:
            protoError(FW_ERR_UNKNOWN_CMD);
            break;
    }
}
