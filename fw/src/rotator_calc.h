// Rotator arithmetic (#29, #32) without Arduino, used by rotator.cpp and the native unit tests
// (fw/test/test_wrap). Angles in centidegrees, steps in microsteps, spk = microsteps per 1000 deg
// (settings.rot.stepsPer1000Deg). Owner: session B (private helper, not a frozen interface).
#pragma once
#include <stdint.h>

// steps = cdeg * spk / 100000, rounded half away from zero, saturated to int32
int32_t rotCdegToSteps(int32_t cdeg, uint32_t spk);
// cdeg = steps * 100000 / spk, rounded half away from zero, saturated to int32 (spk 0 -> 0)
int32_t rotStepsToCdeg(int32_t steps, uint32_t spk);
// rate in cdeg/s or cdeg/s^2 -> steps/s or steps/s^2, at least 1
uint32_t rotRateToSteps(uint32_t cdegPerS, uint32_t spk);
// spk * newUsteps / oldUsteps, rounded, kept in 1..UINT32_MAX (oldUsteps 0 -> spk unchanged)
uint32_t rotRescaleSpk(uint32_t spk, uint16_t oldUsteps, uint16_t newUsteps);

// IMU home, first pass: the IMU zero lies at current - roll (mod 36000). Picks the candidate
// current - roll + k*36000 inside [wrapMin, wrapMax] nearest 0, i.e. the one that unwinds the cable
// (tie: nearest `current`). False when no candidate fits the range.
bool rotPlanHome(int32_t current, int32_t roll, int32_t wrapMin, int32_t wrapMax, int32_t* out);

// IMU home, correction passes: current - roll, clamped into [wrapMin, wrapMax] (no full turns).
int32_t rotPlanCorrection(int32_t current, int32_t roll, int32_t wrapMin, int32_t wrapMax);
