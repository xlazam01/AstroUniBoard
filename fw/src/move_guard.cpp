#include "move_guard.h"

FwError moveGuard(const MoveGuardIn& in) {
    if (!in.suspect) return FW_OK;
    if (in.safeMode) {
        if (in.absolute) return FW_ERR_POS_UNVERIFIED;
        const int64_t d = in.delta;
        return (d > in.jogLimit || d < -(int64_t)in.jogLimit) ? FW_ERR_POS_UNVERIFIED : FW_OK;
    }
    return in.guardOn ? FW_ERR_POS_UNVERIFIED : FW_OK;
}
