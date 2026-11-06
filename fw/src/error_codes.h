// Error codes shared by firmware logic and the serial protocol, sent as "ERR,<code>#".
// Numbers follow OpenWheel so the host Core libraries can share code; numbers are never reused.
// Full table: issue #11 (this file is the source of truth).
#pragma once
#include <stdint.h>

enum FwError : uint8_t {
    FW_OK                  = 0,
    FW_ERR_EEPROM          = 4,   // EEPROM missing, unreadable or write failed
    FW_ERR_MOTOR_DRIVER    = 5,   // TMC2209 not answering or reporting overtemp/short
    FW_ERR_BAD_PARAM       = 7,   // parameter missing, malformed or out of range
    FW_ERR_BUSY            = 8,   // an axis is moving
    FW_ERR_UNKNOWN_CMD     = 10,  // command number not implemented
    FW_ERR_BAD_FORMAT      = 11,  // malformed or too long command line
    FW_ERR_NOT_SUPPORTED   = 12,  // command known but not changeable at runtime
    FW_ERR_SENSOR          = 14   // sensor missing or giving bad data (no valid reading)
};
