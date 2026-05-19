#pragma once

#include "../../Common.h"

#if AXIS3_SENSE_HOME == MPU6000

class MPU6000Imu {
public:
  bool init(uint8_t address = 0x68);
  bool readAccel(int16_t &ax, int16_t &ay, int16_t &az);

  // angle in degrees using gravity as reference
  // chip mounted face-up (back toward PCB): az=-g at landscape, so atan2(-ax,-az)
  // returns 0° at landscape (camera upright) and ±180° when upside-down.
  float angle();

private:
  bool writeReg(uint8_t reg, uint8_t val);
  bool readBytes(uint8_t reg, uint8_t *buf, uint8_t len);
  uint8_t _addr = 0x68;
};

#endif
