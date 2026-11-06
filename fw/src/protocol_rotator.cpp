// Rotator block 70-89 (PROTOCOL.md §4.2). Owner: session B. STUB: answers ERR,10.
#include "protocol.h"

void protocolRotator(uint8_t cmd, char* params) {
    (void)cmd;
    (void)params;
    protoError(FW_ERR_UNKNOWN_CMD);
}
