// STUB — session D (#17) implements the 24AA64 records.
#include "nv_store.h"

bool nvBegin() { settingsDefaults(settings); return false; }
bool nvFault() { return true; }
FwError nvSaveSettings() { return FW_ERR_EEPROM; }
void nvLoadPositions(SavedPositions& out) { out = SavedPositions{false, 0, false, 0}; }
void nvMarkMoving(AxisId axis) { (void)axis; }
void nvSavePosition(AxisId axis, int32_t value) { (void)axis; (void)value; }
bool nvLoadImuCal(ImuCalibration& out) { out = ImuCalibration{}; return false; }
FwError nvSaveImuCal(const ImuCalibration& cal) { (void)cal; return FW_ERR_EEPROM; }
void nvUpdate() {}
