// Focuser (#25, #26, PROTOCOL.md §4.1) on axisFoc: absolute / relative moves inside 0..max (no
// clamping, no homing, Q16), backlash take-up on a direction change, position persistence (Q15).
// Running values live in settings.foc / settings.drv[AXIS_FOC].
// Owner: session A. FROZEN interface.
#pragma once
#include <stdint.h>
#include "error_codes.h"

void focuserBegin();               // after nvBegin() and tmcBegin(): restores the saved position
void focuserUpdate();              // main loop: end of move, 2 s idle save

FwError focuserMoveTo(int32_t target);    // FW_OK / FW_ERR_BAD_PARAM / FW_ERR_BUSY
FwError focuserMoveBy(int32_t delta);
void    focuserHalt();
FwError focuserSync(int32_t pos);         // sets the position, clears "unverified"

int32_t focuserPosition();
int32_t focuserTarget();           // -1 when not moving
bool    focuserBusy();
bool    focuserHalting();
bool    focuserUnverified();       // status flag 0
void    focuserSetUnverified();    // stall, fault

void focuserApplySettings();       // push settings.foc speed/accel/reverse/idle-off to the axis (:24, load)
// :48 changed the microsteps: rescale position, max, backlash, speed, accel by newUsteps/oldUsteps
void focuserRescale(uint16_t oldUsteps, uint16_t newUsteps);
