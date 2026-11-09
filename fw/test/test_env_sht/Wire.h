// Native test stand-in for Wire: a bus with fake SHT3x / SHT4x parts (test_env_sht.cpp).
#pragma once
#include <stdint.h>

class TwoWire {
public:
    void    beginTransmission(uint8_t addr);
    void    write(uint8_t b);
    uint8_t endTransmission();
    uint8_t requestFrom(uint8_t addr, uint8_t n);
    int     read();
};
extern TwoWire Wire;
