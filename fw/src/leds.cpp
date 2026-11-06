// STUB — session D (#20) implements dimming and blink patterns. Heartbeat only.
#include <Arduino.h>
#include "config.h"
#include "leds.h"
#include "settings.h"
#include "status.h"

void ledsBegin() {
    pinMode(PIN_STATUS_LED, OUTPUT);
    digitalWrite(PIN_STATUS_LED, LOW);
    pinMode(PIN_ACT_LED, OUTPUT);
}

void ledsUpdate() {
    digitalWrite(PIN_ACT_LED, (millis() / (statusSafeMode() ? 125 : 500)) & 1);
}

void ledsSetBrightness(uint8_t percent) { settings.ledBrightness = percent; }
uint8_t ledsBrightness() { return settings.ledBrightness; }
