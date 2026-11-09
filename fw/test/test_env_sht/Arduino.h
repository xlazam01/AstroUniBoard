// Native test stand-in for the Arduino core: only what env_sht31.cpp uses.
#pragma once
#include <stdint.h>
#include <math.h>

#define OUTPUT 1
#define HIGH 1
#define LOW 0
#define PB9 109

extern uint32_t g_ms;    // simulated millis()
extern int      g_pwr;   // PB9 (SHT_PWR) level

inline uint32_t millis() { return g_ms; }
inline void pinMode(int, int) {}
inline void digitalWrite(int pin, int v) { if (pin == PB9) g_pwr = v; }
