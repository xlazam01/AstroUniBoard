// Rotator (#29, #30, #32 sequencing, PROTOCOL.md §4.2) on axisRot: signed mechanical angle in cdeg
// inside the cable-wrap range, IMU homing (:77), IMU zero calibration sequence (:95), boot home (Q20).
// Running values live in settings.rot / settings.drv[AXIS_ROT].
// Owner: session B. FROZEN interface. The IMU is used only through imu_mpu6000.h.
#pragma once
#include <stdint.h>
#include "error_codes.h"

#define ROT_NO_TARGET  INT32_MIN

void rotatorBegin();               // after nvBegin(), tmcBegin(), imuBegin(): saved angle, flag 1 set
void rotatorUpdate();              // main loop: end of move, homing / calibration steps, 2 s idle save, boot home

FwError rotatorMoveTo(int32_t target0to35999);   // :71, cable-wrap rule
FwError rotatorMoveBy(int32_t deltaCdeg);         // :72, target inside wrapMin..wrapMax
void    rotatorHalt();                            // also aborts homing / calibration
FwError rotatorSync(int32_t signedCdeg);          // :76, clears "unverified"
FwError rotatorImuHome();      // :77: FW_OK / FW_ERR_NOT_CALIBRATED / FW_ERR_SENSOR / FW_ERR_IMU_VERTICAL / FW_ERR_BUSY
FwError rotatorImuCalibrate(); // :95: FW_OK / FW_ERR_SENSOR / FW_ERR_IMU_VERTICAL / FW_ERR_BUSY

int32_t rotatorAngle();            // signed cdeg
int32_t rotatorTarget();           // signed target cdeg of the running move, ROT_NO_TARGET when none
bool    rotatorBusy();             // moving, halting, homing or calibrating
bool    rotatorHalting();
bool    rotatorHoming();           // state 4: IMU homing or IMU calibration
bool    rotatorUnverified();       // status flag 1

void rotatorApplySettings();       // push settings.rot speed/accel/reverse/idle-off to the axis (:24, load)
// :48 changed the microsteps: rescale stepsPer1000Deg by newUsteps/oldUsteps (angle stays)
void rotatorRescale(uint16_t oldUsteps, uint16_t newUsteps);
