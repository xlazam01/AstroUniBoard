// Environment and IMU block 90-99 (PROTOCOL.md §4.3). Owner: session C.
// Skeleton: :91 (both SHT31 sensors, was the temporary :90) works; the rest answers ERR,10.
#include "env_sht31.h"
#include "protocol.h"

void protocolEnv(uint8_t cmd, char* params) {
    ProtoArgs a;
    if (!protoSplit(params, a)) { protoError(FW_ERR_BAD_FORMAT); return; }

    switch (cmd) {
        case 91: {  // V,<st>,<t>,<rh>,<dew>,<st>,<t>,<rh>,<dew>,<power_cycles>#  on-board first, then J9
            if (a.n != 0) { protoError(FW_ERR_BAD_PARAM); break; }
            const ShtReading& o = envReading(ENV_ONBOARD);
            const ShtReading& x = envReading(ENV_EXTERNAL);
            protoPrintf("V,%u,%d,%u,%d,%u,%d,%u,%d,%u#",
                        (unsigned)o.status, (int)o.tDeciC, (unsigned)o.rhDeci, (int)o.dewDeciC,
                        (unsigned)x.status, (int)x.tDeciC, (unsigned)x.rhDeci, (int)x.dewDeciC,
                        (unsigned)envPowerCycles());
            break;
        }

        default:
            protoError(FW_ERR_UNKNOWN_CMD);
            break;
    }
}
