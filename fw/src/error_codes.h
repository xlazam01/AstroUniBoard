// Error codes shared by firmware logic and the serial protocol, sent as "ERR,<code>#".
// Numbers follow OpenWheel so the host Core libraries can share code; numbers are never reused.
// Table with meanings: docs/PROTOCOL.md §6 (this file is the source of truth).
#pragma once
#include <stdint.h>

enum FwError : uint8_t {
    FW_OK                  = 0,
    // sticky faults (stay in :33# / :34# until ":34,0#")
    FW_ERR_HOME_TIMEOUT    = 1,   // rotator IMU homing did not converge
    FW_ERR_MOVE_TIMEOUT    = 2,   // move did not finish in its expected time
    FW_ERR_CALIB_FAILED    = 3,   // IMU zero calibration failed
    FW_ERR_EEPROM          = 4,   // EEPROM missing, unreadable or write failed
    FW_ERR_MOTOR_DRIVER    = 5,   // TMC2209 not answering or reporting overtemp/short
    // 6 = OpenWheel "no Hall pulse": not used here, never reuse
    // rejections (reply only)
    FW_ERR_BAD_PARAM       = 7,   // parameter missing, malformed or out of range (also outside limits)
    FW_ERR_BUSY            = 8,   // an axis is moving, homing or calibrating
    FW_ERR_NOT_CALIBRATED  = 9,   // IMU home requested without IMU zero calibration
    FW_ERR_UNKNOWN_CMD     = 10,  // command number not implemented (or not in this build's caps)
    FW_ERR_BAD_FORMAT      = 11,  // malformed or too long command line
    FW_ERR_NOT_SUPPORTED   = 12,  // command known but not changeable at runtime
    FW_ERR_MOTOR_STALL     = 13,  // sticky: StallGuard stopped a move
    FW_ERR_SENSOR          = 14,  // sensor missing or giving bad data (no valid reading)
    FW_ERR_IMU_VERTICAL    = 15,  // optical axis too close to vertical: roll undefined
    FW_ERR_NOT_IDENTIFIED  = 16   // no ":32#" since power-on or since the host opened the port (same in OpenWheel)
};
