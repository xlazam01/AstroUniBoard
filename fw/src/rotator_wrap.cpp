// STUB — session B (#30) implements the planner and its native tests.
#include "rotator_wrap.h"

bool wrapPlanTarget(int32_t current, int32_t target, int32_t wrapMin, int32_t wrapMax, int32_t* out) {
    (void)current; (void)target; (void)wrapMin; (void)wrapMax; (void)out;
    return false;
}

int32_t wrapReported(int32_t a) {
    a %= 36000;
    return a < 0 ? a + 36000 : a;
}
