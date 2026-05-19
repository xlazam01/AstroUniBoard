//--------------------------------------------------------------------------------------------------
// local telescope rotator control

#include "Rotator.h"

#ifdef ROTATOR_PRESENT

#include "../../../lib/tasks/OnTask.h"

#include "../../Telescope.h"
#include "../../mount/Mount.h"
#include "../../mount/site/Site.h"

void rotWrapper() { rotator.monitor(); }

#if defined(ROTATOR_CAN_SERVER_PRESENT)
// 1 Hz heartbeat TX for this remote rotator node
static void rotHeartbeatWrapper() {
  if (!canPlus.ready) return;
  const uint16_t hbId = (uint16_t)(CAN_ROTATOR_HB_ID_BASE);
  const uint8_t b = 0; // payload reserved; currently unused
  canPlus.writePacket((int)hbId, &b, 1);
}
#endif

// initialize rotator
bool Rotator::init() {
  #if defined(ROTATOR_CAN_SERVER_PRESENT)
    if (!CanTransportServer::init(true, 2)) return false;
  #endif

  // wait a moment for any background processing that may be needed
  delay(1000);

  // confirm the data structure size
  if (RotatorSettingsSize < sizeof(RotatorSettings)) {
    nv.initError = true;
    DL("ERR: Rotator::init(), RotatorSettingsSize error"); return false;
  }

  // get settings stored in NV ready
  if (!nv.hasValidKey()) {
    VLF("MSG: Rotator, writing defaults to NV");
    nv.updateBytes(NV_ROTATOR_SETTINGS_BASE + RotatorSettingsSize, &settings, sizeof(RotatorSettings));
    #if AXIS3_SENSE_HOME == MPU6000
      float defaultHome = 0.0f;
      nv.updateBytes(NV_ROTATOR_IMU_HOME, &defaultHome, sizeof(float));
    #endif
  }
  readSettings();

  #if AXIS3_SENSE_HOME == MPU6000
    if (!imu.init(MPU6000_I2C_ADDRESS)) {
      DLF("WRN: Rotator::init(), MPU6000 not found at I2C address");
    } else {
      VLF("MSG: Rotator, MPU6000 IMU initialized");
      nv.readBytes(NV_ROTATOR_IMU_HOME, &imuHomeAngle, sizeof(float));
      if (isnan(imuHomeAngle) || imuHomeAngle < -360.0f || imuHomeAngle > 360.0f) imuHomeAngle = 0.0f;
    }
  #endif

  VLF("MSG: Rotator, init (Axis3)");
  if (!axis3.init(&motor3)) {
    initError.driver = true;
    DLF("ERR: Rotator::init(), no motion controller for Axis3!");
    return false;
  }
  axis3.resetPositionSteps(0);
  axis3.setBacklashSteps(settings.backlash);
  axis3.setFrequencyMax(AXIS3_SLEW_RATE_BASE_DESIRED*2.0F);
  axis3.setFrequencyMin(0.01F);
  axis3.setFrequencySlew(AXIS3_SLEW_RATE_BASE_DESIRED);
  axis3.setSlewAccelerationTime(AXIS3_ACCELERATION_TIME);
  axis3.setSlewAccelerationTimeAbort(AXIS3_RAPID_STOP_TIME);
  if (AXIS3_POWER_DOWN == ON) axis3.setPowerDownTime(AXIS3_POWER_DOWN_TIME);

  ready = true;

  return true;
}

void Rotator::begin() {
  if (!ready) return;

  axis3.calibrateDriver();

  // start monitor task
  VF("MSG: Rotator, start derotation task (rate 1s priority 6)... ");
  if (tasks.add(1000, 0, true, 6, rotWrapper, "RotMon")) { VLF("success"); } else { VLF("FAILED!"); }

  // start heartbeat task
  #if defined(ROTATOR_CAN_SERVER_PRESENT)
    VF("MSG: Rotator, starting CAN heartbeat task (rate 1s priority 6)... ");
    if (tasks.add(1000, 0, true, 6, rotHeartbeatWrapper, "RotHB")) { VLF("success"); } else { VLF("FAILED!"); }
  #endif

  unpark();

  #if AXIS3_SENSE_HOME == MPU6000 && AXIS3_HOME_AUTO == ON
    imuHome();
  #endif
}

// get backlash in steps
int Rotator::getBacklash() {
  return settings.backlash;
}

// set backlash in steps
CommandError Rotator::setBacklash(int value) {
  if (!ready) return CE_CMD_UNKNOWN;

  if (value < 0 || value > 10000) return CE_PARAM_RANGE;
  if (settings.parkState >= PS_PARKED) return CE_PARKED;

  settings.backlash = value;
  writeSettings();
  axis3.setBacklashSteps(settings.backlash);

  return CE_NONE;
}

#if AXIS3_SENSE_HOME == MPU6000

#ifndef M_PI
  #define M_PI 3.14159265358979323846f
#endif

#define IMU_HOME_MAX_PASSES  2      // correction passes after the initial move
#define IMU_HOME_SETTLE_DEG  1.5f   // stop correcting when within this many degrees

// slew to IMU home angle via shortest path
CommandError Rotator::imuHome() {
  if (!ready) return CE_CMD_UNKNOWN;
  if (settings.parkState >= PS_PARKED) return CE_PARKED;

  // Average 5 samples over 100 ms for a stable reading (handles power-on noise)
  float sumX = 0.0f, sumZ = 0.0f;
  int count = 0;
  for (int i = 0; i < 5; i++) {
    int16_t ax, ay, az;
    if (imu.readAccel(ax, ay, az)) { sumX += (float)ax; sumZ += (float)az; count++; }
    delay(20);
  }
  if (count == 0) return CE_SLEW_ERR_UNSPECIFIED;
  float currentAngle = atan2f(sumX / count, sumZ / count) * (180.0f / M_PI);

  // Sync axis3 to the true physical position before computing the goto target.
  // NV-stored position may be stale after a reflash or manual rotation.
  // IMU returns [-180, +180]; axis3 uses [0, 360] with home at 0°.
  // Negative IMU means rotator is past the ±180° wrap (axis3 in (180°, 360°]).
  float physAxis3 = currentAngle;
  if (physAxis3 < 0.0f) physAxis3 += 360.0f;
  if (physAxis3 < AXIS3_LIMIT_MIN) physAxis3 = AXIS3_LIMIT_MIN;
  if (physAxis3 > AXIS3_LIMIT_MAX) physAxis3 = AXIS3_LIMIT_MAX;
  axis3.setInstrumentCoordinate(physAxis3);

  float delta = imuHomeAngle - currentAngle;
  while (delta > 180.0f)  delta -= 360.0f;
  while (delta < -180.0f) delta += 360.0f;

  float target = physAxis3 + delta;
  if (target < AXIS3_LIMIT_MIN) target = AXIS3_LIMIT_MIN;
  if (target > AXIS3_LIMIT_MAX) target = AXIS3_LIMIT_MAX;

  dbgIMU = currentAngle; dbgPhys = physAxis3; dbgDelta = delta; dbgTarget = target;

  imuHomingPass = 0;
  imuHoming = true;
  return gotoTarget(target);
}

// calibrate IMU home angle from N-sample average of current orientation
// gravRef=true: hardcoded 0.0° (gravity reference); gravRef=false: measured average
CommandError Rotator::imuCalibrate(bool gravRef) {
  if (gravRef) {
    imuHomeAngle = 0.0f;
  } else {
    const int N = 10;
    float sumX = 0.0f, sumZ = 0.0f;
    int count = 0;
    for (int i = 0; i < N; i++) {
      int16_t ax, ay, az;
      if (imu.readAccel(ax, ay, az)) {
        sumX += (float)ax;
        sumZ += (float)az;
        count++;
      }
      delay(20);
    }
    if (count == 0) return CE_SLEW_ERR_UNSPECIFIED;
    imuHomeAngle = atan2f(-(sumX / count), -(sumZ / count)) * (180.0f / M_PI);
  }
  nv.updateBytes(NV_ROTATOR_IMU_HOME, &imuHomeAngle, sizeof(float));
  return CE_NONE;
}

#endif // AXIS3_SENSE_HOME == MPU6000

#ifdef MOUNT_PRESENT
  // returns parallactic angle in degrees
  double Rotator::parallacticAngle(Coordinate *coord) {
    return radToDeg(atan2(sin(coord->h), cos(coord->d)*tan(site.location.latitude) - sin(coord->d)*cos(coord->h)));
  }

  // returns parallactic rate in degrees per second
  double Rotator::parallacticRate(Coordinate *coord) {
    // one minute of hour angle in degrees = 15/60 = 0.25
    Coordinate ahead = *coord;
    ahead.h += degToRad(0.125);
    Coordinate behind = *coord;
    behind.h -= degToRad(0.125);
    double b = parallacticAngle(&behind);
    double a = parallacticAngle(&ahead);
    if (b >  90.0 && a < -90.0) a += 360.0;
    if (b < -90.0 && a >  90.0) b += 360.0;
    return (a - b)/60.0;
  }
#endif

// set move rate, 1 for 0.01 deg/sec slew, 2 for 0.1 deg/sec, 3 for 1 deg/sec, 4 for 0.5x goto rate
void Rotator::setMoveRate(int value) {
  if (!ready) return;
  
  switch (value) {
    case 1: moveRate = 0.01F; break;
    case 2: moveRate = 0.1F; break;
    case 3: moveRate = 1.0F; break;
    case 4: moveRate = settings.gotoRate/2.0F; break;
    default: moveRate = 0.1F; break;
  }
  if (moveRate > settings.gotoRate/2.0F) moveRate = settings.gotoRate/2.0F;
}

// start slew in the specified direction
CommandError Rotator::move(Direction dir) {
  if (!ready) return CE_CMD_UNKNOWN;
  
  if (settings.parkState >= PS_PARKED) return CE_PARKED;

  axis3.enable(true);
  axis3.setSynchronizedFrequency(0.0F);

  return axis3.autoSlew(dir, moveRate);
}

// get goto rate, 1 for 0.5x base, 2 for 0.75x base, 3 for base, 4 for 1.5x base, 5 for 2x base
int Rotator::getGotoRate() {
  if (settings.gotoRate < AXIS3_SLEW_RATE_BASE_DESIRED/1.75) return 1;
  if (settings.gotoRate < AXIS3_SLEW_RATE_BASE_DESIRED/1.25) return 2;
  if (settings.gotoRate < AXIS3_SLEW_RATE_BASE_DESIRED*1.25) return 3;
  if (settings.gotoRate < AXIS3_SLEW_RATE_BASE_DESIRED*1.75) return 4; else return 5;
}

// set goto rate, 1 for 0.5x base, 2 for 0.66x base, 3 for base, 4 for 1.5x base, 5 for 2x base
void Rotator::setGotoRate(int value) {
  switch (value) {
    case 1: settings.gotoRate = AXIS3_SLEW_RATE_BASE_DESIRED/2.0; break;
    case 2: settings.gotoRate = AXIS3_SLEW_RATE_BASE_DESIRED/1.5; break;
    case 3: settings.gotoRate = AXIS3_SLEW_RATE_BASE_DESIRED*1.0; break;
    case 4: settings.gotoRate = AXIS3_SLEW_RATE_BASE_DESIRED*1.5; break;
    case 5: settings.gotoRate = AXIS3_SLEW_RATE_BASE_DESIRED*2.0; break;
    default: settings.gotoRate = AXIS3_SLEW_RATE_BASE_DESIRED; break;
  }
  writeSettings();
}

// move rotator to a specific location
CommandError Rotator::gotoTarget(float target) {
  if (!ready) return CE_CMD_UNKNOWN;

  if (settings.parkState >= PS_PARKED) return CE_PARKED;

  VF("MSG: Rotator, goto target coordinate set ("); V(target); VL(" deg)");
  VLF("MSG: Rotator, attempting goto");

  axis3.enable(true);
  axis3.setSynchronizedFrequency(0.0F);
  axis3.setTargetCoordinate(target);

  CommandError e = axis3.autoGoto(settings.gotoRate);
  if (e != CE_NONE) { VLF("MSG: Rotator, goto failed"); }

  return e;
}

// parks rotator at current position
CommandError Rotator::park() {
  if (!ready) return CE_CMD_UNKNOWN;
  
  if (settings.parkState == PS_PARKED)      return CE_NONE;
  if (settings.parkState == PS_PARKING)     return CE_PARK_FAILED;
  if (settings.parkState == PS_UNPARKING)   return CE_PARK_FAILED;
  if (settings.parkState == PS_PARK_FAILED) return CE_PARK_FAILED;

  derotatorEnabled = false;

  VLF("MSG: Rotator, parking");

  axis3.enable(true);
  axis3.setBacklash(0.0F);
  settings.position = axis3.getInstrumentCoordinate();
  axis3.setTargetCoordinatePark(settings.position);

  CommandError e = axis3.autoGoto(settings.gotoRate);

  if (e == CE_NONE) {
    settings.parkState = PS_PARKING;
    writeSettings();
  }

  return e;
}

// unparks rotator
CommandError Rotator::unpark() {
  if (!ready) return CE_CMD_UNKNOWN;
  
  if (settings.parkState == PS_PARKING)     return CE_PARK_FAILED;
  if (settings.parkState == PS_UNPARKING)   return CE_PARK_FAILED;
  if (settings.parkState == PS_PARK_FAILED) return CE_PARK_FAILED;

  // setting write delay to 0 disables on-the-fly position writes and forces strict parking
  if (ROTATOR_WRITE_DELAY == 0) {
    if (settings.parkState != PS_PARKED) return CE_NOT_PARKED;
  }

  axis3.enable(true);
  VF("MSG: Rotator, unpark position "); V(settings.position); VL(" deg");

  // simple unpark if we didn't actually park
  if (settings.parkState == PS_UNPARKED) {
    axis3.setInstrumentCoordinate(settings.position);
    writeSettings();
    return CE_NONE;
  }

  axis3.setBacklash(0.0F);
  axis3.setInstrumentCoordinatePark(settings.position);

  axis3.setBacklash(settings.backlash);
  axis3.setTargetCoordinate(settings.position);

  CommandError e = axis3.autoGoto(settings.gotoRate);

  if (e == CE_NONE) {
    settings.parkState = PS_UNPARKING;
    writeSettings();
  }

  return e;
}

void Rotator::readSettings() {
  nv.readBytes(NV_ROTATOR_SETTINGS_BASE + RotatorSettingsSize, &settings, sizeof(RotatorSettings));

  if (settings.backlash < 0)     { settings.backlash = 0; initError.value = true; DLF("ERR: Rotator.init(), bad NV backlash < 0 steps (set to 0)"); }
  if (settings.backlash > 10000) { settings.backlash = 0; initError.value = true; DLF("ERR: Rotator.init(), bad NV backlash > 10000 steps (set to 0)"); }

  if (settings.position < AXIS3_LIMIT_MIN) { settings.position = 0.0F; initError.value = true; DLF("ERR: Rotator.init(), bad NV park pos < AXIS3_LIMIT_MIN (set to 0.0)"); }
  if (settings.position > AXIS3_LIMIT_MAX) { settings.position = 0.0F; initError.value = true; DLF("ERR: Rotator.init(), bad NV park pos > AXIS3_LIMIT_MAX (set to 0.0)"); }
}

void Rotator::writeSettings() {
  nv.updateBytes(NV_ROTATOR_SETTINGS_BASE + RotatorSettingsSize, &settings, sizeof(RotatorSettings));
}

// poll rotator to handle parking and derotation
void Rotator::monitor() {
  secs++;

  if (axis3.isSlewing() || settings.position == axis3.getInstrumentCoordinate()) writeTime = secs + ROTATOR_WRITE_DELAY;

  if (!axis3.isSlewing()) {

    if (settings.parkState == PS_PARKING) {
      if (axis3.atTarget()) {
        axis3.enable(false);
        settings.parkState = PS_PARKED;
        writeSettings();
        #if DEBUG == VERBOSE
          long offset = axis3.getInstrumentCoordinateSteps() - axis3.getMotorPositionSteps();
          VF("MSG: Rotator, park motor target   "); VL(axis3.getTargetCoordinateSteps() - offset);
          VF("MSG: Rotator, park motor position "); VL(axis3.getMotorPositionSteps());
        #endif
      }
    } else

    if (settings.parkState == PS_UNPARKING) {
      if (axis3.atTarget()) {
        settings.parkState = PS_UNPARKED;
        writeSettings();
      }
    } else

    if (settings.parkState == PS_UNPARKED) {
      #ifdef MOUNT_PRESENT
        if (derotatorEnabled && transform.mountType == ALTAZM) {
          float pr = 0.0F;
          if (!axis3.getSynchronized()) axis3.setSynchronized(true);
          Coordinate current = mount.getPosition();
          pr = parallacticRate(&current);
          if (derotatorReverse) pr = -pr;
          axis3.setSynchronized(true);
          axis3.setSynchronizedFrequency(pr);
        }
      #endif

      if (homing) {
        axis3.resetPosition((axis3.getLimitMax() + axis3.getLimitMin())/2.0F);
        axis3.setBacklashSteps(getBacklash());
        homing = false;
      }

      #if AXIS3_SENSE_HOME == MPU6000
        if (imuHoming) {
          float currentAngle = imu.angle();
          if (!isnan(currentAngle)) {
            float delta = imuHomeAngle - currentAngle;
            while (delta > 180.0f)  delta -= 360.0f;
            while (delta < -180.0f) delta += 360.0f;
            if (fabsf(delta) > IMU_HOME_SETTLE_DEG && imuHomingPass < IMU_HOME_MAX_PASSES) {
              imuHomingPass++;
              float target = (float)axis3.getInstrumentCoordinate() + delta;
              if (target < AXIS3_LIMIT_MIN) target = AXIS3_LIMIT_MIN;
              if (target > AXIS3_LIMIT_MAX) target = AXIS3_LIMIT_MAX;
              gotoTarget(target);
            } else {
              axis3.resetPosition(0.0);
              axis3.setBacklashSteps(getBacklash());
              settings.position = 0.0f;
              writeSettings();
              imuHoming = false;
              imuHomingPass = 0;
            }
          } else {
            imuHoming = false;
            imuHomingPass = 0;
          }
        }
      #endif

      // delayed write of rotator position
      if (ROTATOR_WRITE_DELAY != 0) {
        if (secs > writeTime) {
          settings.position = axis3.getInstrumentCoordinate();
          writeSettings();
          VF("MSG: Rotator, writing position ("); V(settings.position); VL(" deg) to NV"); 
        }
      }
    }

  }
}

Rotator rotator;

#endif
