// STUB — session A (#25, #26) implements the focuser.
#include "focuser.h"
#include "axis.h"
#include "settings.h"

static bool unverified = true;

void focuserBegin() {}
void focuserUpdate() {}
FwError focuserMoveTo(int32_t target) { (void)target; return FW_ERR_UNKNOWN_CMD; }
FwError focuserMoveBy(int32_t delta) { (void)delta; return FW_ERR_UNKNOWN_CMD; }
void focuserHalt() { axisFoc.halt(); }
FwError focuserSync(int32_t pos) { (void)pos; return FW_ERR_UNKNOWN_CMD; }
int32_t focuserPosition() { return axisFoc.position(); }
int32_t focuserTarget() { return axisFoc.busy() ? axisFoc.target() : -1; }
bool focuserBusy() { return axisFoc.busy(); }
bool focuserHalting() { return axisFoc.halting(); }
bool focuserUnverified() { return unverified; }
void focuserSetUnverified() { unverified = true; }
void focuserApplySettings() {}
void focuserRescale(uint16_t oldUsteps, uint16_t newUsteps) { (void)oldUsteps; (void)newUsteps; }
