// Common block 20-49 (PROTOCOL.md §3). Owner: session D.
// Skeleton: :22 :25 :31 :46 work; the rest answers ERR,10 until implemented.
#include <Arduino.h>

#include "bootloader.h"
#include "config.h"
#include "diag.h"
#include "protocol.h"

void protocolCommon(uint8_t cmd, char* params) {
    ProtoArgs a;
    if (!protoSplit(params, a)) { protoError(FW_ERR_BAD_FORMAT); return; }

    switch (cmd) {
        case 22:  // reboot
        case 31:  // reboot into the USB DFU bootloader (magic word + reset)
            protoOk();
            PROTOCOL_SERIAL.flush();
            delay(50);   // let the USB host fetch the reply before the device drops off the bus
            if (cmd == 31) rebootToDfu();
            rebootNormal();
            break;

        case 25:  // ping
            protoOk();
            break;

        case 46:  // reset and safe-mode info: R,<reset flags hex>,<last marker>,<safe mode>#
            protoPrintf("R,%02lX,%u,%u#", (unsigned long)diagResetFlags(), (unsigned)diagLastMark(),
                        diagWatchdogReset() ? 1u : 0u);
            break;

        default:
            protoError(FW_ERR_UNKNOWN_CMD);
            break;
    }
}
