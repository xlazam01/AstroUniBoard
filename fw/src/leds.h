// Status and activity LEDs (#20), active LOW on PB12 / PB4, software-PWM dimming on TIM16 (as OpenWheel).
// Status LED: on while running (blinks fast on a sticky error). Activity LED: heartbeat when idle
// (1 Hz, 4 Hz in safe mode), fast blink while an axis moves.
// Owner: session D. FROZEN interface.
#pragma once
#include <stdint.h>

void    ledsBegin();                       // uses settings.ledBrightness
void    ledsUpdate();                      // main loop; reads statusGet() / statusSafeMode()
void    ledsSetBrightness(uint8_t percent);   // 0..100, 0 = off (also stores settings.ledBrightness)
uint8_t ledsBrightness();
