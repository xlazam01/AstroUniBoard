// Environment and IMU block 90-99 (PROTOCOL.md §4.3). Owner: session C (#31, #32, #34).
// Works also in safe mode, where no I2C device was started: env -> ERR,14 / status 3, IMU -> ERR,14.
#include "env_sht31.h"
#include "imu_mpu6000.h"
#include "protocol.h"
#include "rotator.h"
#include "settings.h"
#include "status.h"

void protocolEnv(uint8_t cmd, char* params) {
    ProtoArgs a;
    if (!protoSplit(params, a)) { protoError(FW_ERR_BAD_PARAM); return; }

    // Every command here except :93 takes no parameter.
    if (cmd >= 90 && cmd <= 96 && cmd != 93 && a.n != 0) { protoError(FW_ERR_BAD_PARAM); return; }

    switch (cmd) {
        case 90: {  // W,<src>,<t>,<rh>,<dew>#  src 1 on-board, 2 external
            const int8_t s = envSelected();
            if (s < 0) { protoError(FW_ERR_SENSOR); break; }
            const ShtReading& r = envReading((EnvSensor)s);
            protoPrintf("W,%u,%d,%u,%d#", (unsigned)(s == ENV_ONBOARD ? 1 : 2),
                        (int)r.tDeciC, (unsigned)r.rhDeci, (int)r.dewDeciC);
            break;
        }

        case 91: {  // V,<st>,<t>,<rh>,<dew>,<st>,<t>,<rh>,<dew>,<power_cycles>#  on-board first, then J9
            const ShtReading& o = envReading(ENV_ONBOARD);
            const ShtReading& x = envReading(ENV_EXTERNAL);
            protoPrintf("V,%u,%d,%u,%d,%u,%d,%u,%d,%u#",
                        (unsigned)o.status, (int)o.tDeciC, (unsigned)o.rhDeci, (int)o.dewDeciC,
                        (unsigned)x.status, (int)x.tDeciC, (unsigned)x.rhDeci, (int)x.dewDeciC,
                        (unsigned)envPowerCycles());
            break;
        }

        case 92:    // sensor selection: 0 auto, 1 on-board, 2 external
            protoInt(settings.envSelect);
            break;

        case 93: {  // set the selection (running value only, :23# saves)
            int32_t v;
            if (a.n != 1 || !protoInt32(a.v[0], v) || v < ENV_SEL_AUTO || v > ENV_SEL_EXTERNAL) {
                protoError(FW_ERR_BAD_PARAM);
                break;
            }
            settings.envSelect = (uint8_t)v;
            protoOk();
            break;
        }

        case 94: {  // I,<roll>,<tilt>,<ax>,<ay>,<az>#
            ImuVec mg;
            if (!imuAnswering() || !imuAccel(mg)) { protoError(FW_ERR_SENSOR); break; }
            protoPrintf("I,%ld,%ld,%d,%d,%d#", (long)imuRollCdeg(), (long)imuTiltCdeg(),
                        (int)mg.x, (int)mg.y, (int)mg.z);
            break;
        }

        case 95: {  // IMU zero calibration; the rotator runs the sequence (state 4)
            if (statusAnyBusy()) { protoError(FW_ERR_BUSY); break; }
            if (!imuAnswering()) { protoError(FW_ERR_SENSOR); break; }
            const FwError e = rotatorImuCalibrate();
            if (e == FW_OK) protoOk();
            else protoError(e);
            break;
        }

        case 96: {  // C,<valid>,<g0x>,<g0y>,<g0z>,<nx>,<ny>,<nz>#
            const ImuCalibration& c = imuCalData();
            protoPrintf("C,%u,%d,%d,%d,%d,%d,%d#", (unsigned)c.valid,
                        (int)c.g0[0], (int)c.g0[1], (int)c.g0[2], (int)c.n[0], (int)c.n[1], (int)c.n[2]);
            break;
        }

        default:    // 97-99 reserved
            protoError(FW_ERR_UNKNOWN_CMD);
            break;
    }
}
