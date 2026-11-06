// Rotator arithmetic (#29, #32): see rotator_calc.h. Pure integer code, no Arduino.
#include "rotator_calc.h"

static int64_t abs64(int64_t a) { return a < 0 ? -a : a; }

// num / den rounded half away from zero (den > 0)
static int64_t divRound(int64_t num, int64_t den) {
    return num >= 0 ? (num + den / 2) / den : -((-num + den / 2) / den);
}

static int32_t sat32(int64_t v) {
    if (v > INT32_MAX) return INT32_MAX;
    if (v < INT32_MIN) return INT32_MIN;
    return (int32_t)v;
}

int32_t rotCdegToSteps(int32_t cdeg, uint32_t spk) {
    return sat32(divRound((int64_t)cdeg * (int64_t)spk, 100000));
}

int32_t rotStepsToCdeg(int32_t steps, uint32_t spk) {
    if (spk == 0) return 0;
    return sat32(divRound((int64_t)steps * 100000, (int64_t)spk));
}

uint32_t rotRateToSteps(uint32_t rate, uint32_t spk) {
    const int64_t v = divRound((int64_t)rate * (int64_t)spk, 100000);
    if (v < 1) return 1;
    if (v > (int64_t)UINT32_MAX) return UINT32_MAX;
    return (uint32_t)v;
}

uint32_t rotRescaleSpk(uint32_t spk, uint16_t oldUsteps, uint16_t newUsteps) {
    if (oldUsteps == 0) return spk;
    const int64_t v = divRound((int64_t)spk * newUsteps, oldUsteps);
    if (v < 1) return 1;
    if (v > (int64_t)UINT32_MAX) return UINT32_MAX;
    return (uint32_t)v;
}

bool rotPlanHome(int32_t current, int32_t roll, int32_t wrapMin, int32_t wrapMax, int32_t* out) {
    const int64_t zero = (int64_t)current - roll;
    bool found = false;
    int64_t best = 0;
    // current may be anywhere a saved angle can be (+-36000), roll is +-18000: k = -3..3 covers it
    for (int k = -3; k <= 3; k++) {
        const int64_t c = zero + (int64_t)k * 36000;
        if (c < wrapMin || c > wrapMax) continue;
        if (!found) { best = c; found = true; continue; }
        const int64_t ac = abs64(c), ab = abs64(best);
        if (ac < ab || (ac == ab && abs64(c - current) < abs64(best - current))) best = c;
    }
    if (found && out) *out = (int32_t)best;
    return found;
}

int32_t rotPlanCorrection(int32_t current, int32_t roll, int32_t wrapMin, int32_t wrapMax) {
    int64_t t = (int64_t)current - roll;
    if (t < wrapMin) t = wrapMin;
    if (t > wrapMax) t = wrapMax;
    return (int32_t)t;
}
