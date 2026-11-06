// STUB — session D (#18) implements the ADC readings.
#include "housekeeping.h"
#include "clock_config.h"
#include "tmc2209.h"

void hkBegin() {}
void hkUpdate() {}
int16_t hkMcuTempDeciC() { return 0; }
uint16_t hkVddaMv() { return 0; }
uint8_t hkWarnings() { return tmcWarnings() | (clockFromCrystal() ? 0 : HK_WARN_NO_CRYSTAL); }
