#include "MPU6000.h"

#if AXIS3_SENSE_HOME == MPU6000

#define MPU_REG_PWR_MGMT_1    0x6B
#define MPU_REG_ACCEL_CONFIG  0x1C
#define MPU_REG_ACCEL_XOUT_H  0x3B

#ifndef M_PI
  #define M_PI 3.14159265358979323846f
#endif

bool MPU6000Imu::init(uint8_t address) {
  _addr = address;
  if (!writeReg(MPU_REG_PWR_MGMT_1, 0x00)) return false;  // wake up, use internal clock
  delay(10);
  if (!writeReg(MPU_REG_ACCEL_CONFIG, 0x00)) return false; // ±2g full-scale range
  return true;
}

bool MPU6000Imu::readAccel(int16_t &ax, int16_t &ay, int16_t &az) {
  uint8_t buf[6];
  if (!readBytes(MPU_REG_ACCEL_XOUT_H, buf, 6)) return false;
  ax = (int16_t)((buf[0] << 8) | buf[1]);
  ay = (int16_t)((buf[2] << 8) | buf[3]);
  az = (int16_t)((buf[4] << 8) | buf[5]);
  return true;
}

float MPU6000Imu::angle() {
  int16_t ax, ay, az;
  if (!readAccel(ax, ay, az)) return NAN;
  // chip is mounted face-up (back toward PCB): gravity reads az=-g at landscape.
  // atan2(-ax, -az) = 0° at landscape (ax=0, az=-g) and ±180° when upside-down.
  return atan2f(-(float)ax, -(float)az) * (180.0f / M_PI);
}

bool MPU6000Imu::writeReg(uint8_t reg, uint8_t val) {
  HAL_WIRE.beginTransmission(_addr);
  HAL_WIRE.write(reg);
  HAL_WIRE.write(val);
  return HAL_WIRE.endTransmission() == 0;
}

bool MPU6000Imu::readBytes(uint8_t reg, uint8_t *buf, uint8_t len) {
  HAL_WIRE.beginTransmission(_addr);
  HAL_WIRE.write(reg);
  if (HAL_WIRE.endTransmission(false) != 0) return false;
  HAL_WIRE.requestFrom((uint8_t)_addr, (uint8_t)len);
  if (HAL_WIRE.available() < len) return false;
  for (uint8_t i = 0; i < len; i++) buf[i] = HAL_WIRE.read();
  return true;
}

#endif
