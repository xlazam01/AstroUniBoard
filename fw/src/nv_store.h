// Non-volatile store on the 24AA64 (8 KB I2C EEPROM, 0x50 or 0x57 — check with an I2C scan) (#17).
// Three independent records, each with magic + version + CRC:
//   - settings     the whole Settings struct, written only by ":23#"
//   - position     focuser steps + rotator cdeg, written 2 s after an axis stops (Q15); wear-levelled
//   - IMU zero     calibration from ":95#", written at once on success
// A missing EEPROM or a bad CRC leaves the defaults in use and sets FLAG_EEPROM_FAULT.
// Owner: session D. FROZEN interface.
#pragma once
#include <stdint.h>
#include "error_codes.h"
#include "settings.h"

struct ImuCalibration {
    uint8_t valid;          // 0 / 1
    int16_t g0[3];          // gravity at the zero orientation, mg (chip axes)
    int16_t n[3];           // rotation axis, unit vector * 10000
};

// Position record as loaded at boot
struct SavedPositions {
    bool    focValid;       // a position was saved and no focuser move was cut by a power loss
    int32_t focPos;         // steps (last saved value, also when !focValid)
    uint16_t focUsteps;     // microstep setting focPos is counted in; 0 = unknown (older slot)
    bool    rotValid;
    int32_t rotCdeg;        // signed mechanical angle
};

bool nvBegin();                         // detect the EEPROM, load settings into `settings` (defaults on failure)
bool nvFault();                         // EEPROM missing / bad CRC / write failed -> FLAG_EEPROM_FAULT

FwError nvSaveSettings();               // ":23#": FW_OK or FW_ERR_EEPROM

void nvLoadPositions(SavedPositions& out);
void nvMarkMoving(AxisId axis);         // just before a move starts: a power loss now leaves the position unverified
// After the axis has been idle POSITION_SAVE_DELAY_MS. verified = false keeps the position marked
// unverified across a reboot (focuser after a stall or before the first sync).
void nvSavePosition(AxisId axis, int32_t value, bool verified = true);

bool    nvLoadImuCal(ImuCalibration& out);   // false: none stored
FwError nvSaveImuCal(const ImuCalibration& cal);

void nvUpdate();                        // main loop: finishes pending writes without blocking the loop
