// Focuser block 50-69 (PROTOCOL.md §4.1). Owner: session A. STUB: answers ERR,10.
#include "protocol.h"

void protocolFocuser(uint8_t cmd, char* params) {
    (void)cmd;
    (void)params;
    protoError(FW_ERR_UNKNOWN_CMD);
}
