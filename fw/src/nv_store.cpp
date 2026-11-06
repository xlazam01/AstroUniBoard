// Non-volatile store on the 24AA64 (#17). See nv_store.h for the interface.
//
// Memory map (8 KB, 32-byte pages):
//   0x0000..0x03FF  settings record    (header + Settings, room for later, longer versions)
//   0x0400..0x047F  IMU calibration record
//   0x1000..0x1FFF  position ring: 256 slots of 16 bytes (2 per page)
//
// Record header (8 bytes): magic u16, version u8, reserved u8, length u16 (payload bytes), crc u16.
// The CRC-16/CCITT-FALSE covers the first 6 header bytes and the payload.
// Header bytes all 0xFF or a foreign magic = never written: defaults, no fault. Any other mismatch = fault.
//
// Migration: a record with an older version and a shorter payload is loaded by copying the stored
// prefix over the defaults (settings.h rule: fields are only appended). A newer version or an
// unexpected length is rejected (defaults + fault).
//
// Position ring: every slot holds the complete state (sequence number, both positions, flags) and its
// own CRC. A write goes to the slot after the newest one, so wear is spread over 128 pages; boot reads
// all slots and takes the newest valid one (a torn write fails its CRC and the previous slot wins).
// A move costs two slot writes: nvMarkMoving() ("moving" bit) and nvSavePosition() (bit cleared).
//
// Writes: at most 16 bytes per I2C transaction (Wire buffer is 32, two address bytes; 16-byte chunks
// never cross a 32-byte page). Before every EEPROM access the previous write cycle (<= 5 ms) is waited
// for with ACK polling, at most NV_READY_TIMEOUT_MS. Position slots are written from nvUpdate() without
// waiting: when the chip is still busy the slot stays pending until the next loop pass.
#include <Arduino.h>
#include <Wire.h>
#include <string.h>

#include "nv_store.h"
#include "status.h"
#include "tmc2209.h"

#define NV_ADDR_PRIMARY    0x50   // rev 2 docs (A2..A0 low)
#define NV_ADDR_ALTERNATE  0x57   // fw-onstepx pinmap comment (A2..A0 high)

#define NV_SIZE            8192
#define NV_CHUNK           16
#define NV_READ_CHUNK      32     // Wire receive buffer
#define NV_READY_TIMEOUT_MS 10    // 24AA64 write cycle is 5 ms max

#define NV_SETTINGS_ADDR   0x0000
#define NV_SETTINGS_AREA   0x0400
#define NV_IMU_ADDR        0x0400
#define NV_IMU_AREA        0x0080
#define NV_RING_ADDR       0x1000
#define NV_RING_SLOTS      256
#define NV_SLOT_SIZE       16

#define NV_MAGIC_SETTINGS  0x5355   // "US"
#define NV_MAGIC_IMU       0x4955   // "UI"
#define NV_IMU_VERSION     1

// Slot flags
#define SLOT_FOC_MOVING    0x01
#define SLOT_ROT_MOVING    0x02
#define SLOT_FOC_SAVED     0x04   // a focuser position was saved at least once
#define SLOT_ROT_SAVED     0x08

struct __attribute__((packed)) NvHeader {
    uint16_t magic;
    uint8_t  version;
    uint8_t  reserved;
    uint16_t length;
    uint16_t crc;
};
static_assert(sizeof(NvHeader) == 8, "header layout");

struct __attribute__((packed)) PosSlot {
    uint32_t seq;
    int32_t  focPos;
    int32_t  rotCdeg;
    uint8_t  flags;
    uint8_t  reserved;
    uint16_t crc;     // over the first 14 bytes
};
static_assert(sizeof(PosSlot) == NV_SLOT_SIZE, "slot layout");
static_assert(sizeof(NvHeader) + sizeof(Settings) <= NV_SETTINGS_AREA, "settings area too small");
static_assert(sizeof(NvHeader) + sizeof(ImuCalibration) <= NV_IMU_AREA, "IMU area too small");
static_assert(sizeof(ImuCalibration) <= sizeof(Settings), "saveRecord buffer is sized for Settings");

// I2C address of the EEPROM that answered, 0 = none. Not in the frozen header: read it with
// `extern uint8_t nvEepromAddress;` (bring-up report / I2C scan check).
uint8_t nvEepromAddress = 0;

enum : uint8_t {
    FAULT_MISSING  = 0x01,
    FAULT_SETTINGS = 0x02,
    FAULT_IMU      = 0x04,
    FAULT_WRITE    = 0x08,
    FAULT_READ     = 0x10,
};
static uint8_t  faults = FAULT_MISSING;   // safe mode never calls nvBegin(): fault stays set
static uint32_t lastWriteMs = 0;
static bool     writeCycle = false;       // a write was issued and may still be in its internal cycle

static PosSlot  pos;                      // RAM copy of the newest state
static uint16_t nextSlot = 0;
static bool     posPending = false;

// --- CRC-16/CCITT-FALSE ---------------------------------------------------------------------------
static uint16_t crc16(uint16_t crc, const uint8_t* p, size_t n) {
    while (n--) {
        crc ^= (uint16_t)(*p++) << 8;
        for (uint8_t i = 0; i < 8; i++) crc = (crc & 0x8000) ? (uint16_t)((crc << 1) ^ 0x1021) : (uint16_t)(crc << 1);
    }
    return crc;
}

static uint16_t recordCrc(const NvHeader& h, const uint8_t* payload, size_t n) {
    return crc16(crc16(0xFFFF, (const uint8_t*)&h, 6), payload, n);
}

static uint16_t slotCrc(const PosSlot& s) { return crc16(0xFFFF, (const uint8_t*)&s, 14); }

// --- low level --------------------------------------------------------------------------------------
static bool ack(uint8_t addr) {
    Wire.beginTransmission(addr);
    return Wire.endTransmission() == 0;
}

// Write cycle finished? Never waits.
static bool readyNow() {
    if (!writeCycle) return true;
    if (ack(nvEepromAddress)) { writeCycle = false; return true; }
    if (millis() - lastWriteMs > NV_READY_TIMEOUT_MS) {   // never acknowledged again: give up
        writeCycle = false;
        faults |= FAULT_WRITE;
    }
    return false;
}

// Waits for the previous write cycle (ACK polling, <= NV_READY_TIMEOUT_MS).
static bool waitReady() {
    if (!writeCycle) return true;
    for (;;) {
        if (ack(nvEepromAddress)) { writeCycle = false; return true; }
        if (millis() - lastWriteMs > NV_READY_TIMEOUT_MS) { writeCycle = false; return false; }
        delayMicroseconds(200);
    }
}

static bool readBytes(uint16_t addr, uint8_t* dst, size_t n) {
    if (!nvEepromAddress || !waitReady()) return false;
    while (n > 0) {
        const uint8_t len = (uint8_t)(n > NV_READ_CHUNK ? NV_READ_CHUNK : n);
        Wire.beginTransmission(nvEepromAddress);
        Wire.write((uint8_t)(addr >> 8));
        Wire.write((uint8_t)addr);
        if (Wire.endTransmission(false) != 0) return false;
        if (Wire.requestFrom(nvEepromAddress, len) != len) return false;
        for (uint8_t i = 0; i < len; i++) dst[i] = (uint8_t)Wire.read();
        addr += len;
        dst += len;
        n -= len;
    }
    return true;
}

// One chunk inside one page, without waiting for the write cycle afterwards.
static bool writeChunk(uint16_t addr, const uint8_t* src, uint8_t n) {
    Wire.beginTransmission(nvEepromAddress);
    Wire.write((uint8_t)(addr >> 8));
    Wire.write((uint8_t)addr);
    Wire.write(src, n);
    const uint8_t r = Wire.endTransmission();
    if (r != 0) return false;
    lastWriteMs = millis();
    writeCycle = true;
    return true;
}

// Blocking write of a whole record: ~5 ms per 16-byte chunk (settings: 6 chunks).
static bool writeBytes(uint16_t addr, const uint8_t* src, size_t n) {
    if (!nvEepromAddress) return false;
    while (n > 0) {
        uint8_t len = (uint8_t)(NV_CHUNK - (addr % NV_CHUNK));
        if (len > n) len = (uint8_t)n;
        if (!waitReady() || !writeChunk(addr, src, len)) { faults |= FAULT_WRITE; return false; }
        addr += len;
        src += len;
        n -= len;
    }
    return true;
}

// --- records ----------------------------------------------------------------------------------------
enum LoadResult : uint8_t { LOAD_OK, LOAD_BLANK, LOAD_BAD };

// Loads a record into dst (size = current struct size). Older, shorter versions fill the prefix only;
// the caller has put the defaults into dst first.
static LoadResult loadRecord(uint16_t addr, uint16_t area, uint16_t magic, uint8_t version,
                             uint8_t* dst, uint16_t size) {
    NvHeader h;
    if (!readBytes(addr, (uint8_t*)&h, sizeof(h))) return LOAD_BAD;
    const uint8_t* hb = (const uint8_t*)&h;
    bool blank = true;
    for (uint8_t i = 0; i < sizeof(h); i++) if (hb[i] != 0xFF) blank = false;
    if (blank) return LOAD_BLANK;
    // Another magic = never written by this firmware (e.g. old fw-onstepx data): same as blank.
    if (h.magic != magic) return LOAD_BLANK;

    if (h.version == 0 || h.version > version) return LOAD_BAD;
    if (h.length == 0 || h.length > area - sizeof(NvHeader)) return LOAD_BAD;
    if (h.version == version ? h.length != size : h.length >= size) return LOAD_BAD;

    // h.length <= size here. dst is the caller's scratch copy: on a bad CRC the caller discards it.
    // Migration: an older, shorter record overwrites only its prefix; the rest keeps the defaults.
    if (!readBytes(addr + sizeof(h), dst, h.length)) return LOAD_BAD;
    if (recordCrc(h, dst, h.length) != h.crc) return LOAD_BAD;
    return LOAD_OK;
}

static bool saveRecord(uint16_t addr, uint16_t magic, uint8_t version, const uint8_t* src, uint16_t size) {
    // header and payload in one buffer so the record is written in as few chunks as possible
    uint8_t buf[sizeof(NvHeader) + sizeof(Settings)];
    if (size > sizeof(buf) - sizeof(NvHeader)) return false;
    NvHeader h{magic, version, 0xFF, size, 0};
    h.crc = recordCrc(h, src, size);
    memcpy(buf, &h, sizeof(h));
    memcpy(buf + sizeof(h), src, size);
    if (!writeBytes(addr, buf, sizeof(h) + size)) return false;

    // read back: catches a write-protected or flaky chip
    uint8_t chk[sizeof(buf)];
    if (!readBytes(addr, chk, sizeof(h) + size) || memcmp(buf, chk, sizeof(h) + size) != 0) {
        faults |= FAULT_WRITE;
        return false;
    }
    return true;
}

static bool settingsSane(const Settings& s) {
    for (uint8_t a = 0; a < AXIS_COUNT; a++) {
        const DriverConfig& d = s.drv[a];
        if (d.runMa == 0 || d.runMa > TMC_MAX_RUN_MA || d.holdMa > d.runMa) return false;
        if (d.microsteps == 0 || d.microsteps > 256 || (d.microsteps & (d.microsteps - 1))) return false;
        if (d.mode > DRV_SPREADCYCLE) return false;
    }
    return s.envSelect <= ENV_SEL_EXTERNAL && s.ledBrightness <= 100;
}

// --- position ring ----------------------------------------------------------------------------------
static void scanRing() {
    bool found = false;
    uint16_t newest = 0;
    PosSlot best{};
    PosSlot page[NV_READ_CHUNK / NV_SLOT_SIZE];
    for (uint16_t i = 0; i < NV_RING_SLOTS; i += NV_READ_CHUNK / NV_SLOT_SIZE) {
        if (!readBytes(NV_RING_ADDR + i * NV_SLOT_SIZE, (uint8_t*)page, sizeof(page))) {
            faults |= FAULT_READ;   // positions stay unverified
            return;
        }
        for (uint8_t k = 0; k < NV_READ_CHUNK / NV_SLOT_SIZE; k++) {
            const PosSlot& s = page[k];
            if ((s.flags & 0xF0) || s.reserved != 0 || s.crc != slotCrc(s)) continue;   // blank = 0xFF
            if (!found || (int32_t)(s.seq - best.seq) > 0) { best = s; newest = i + k; found = true; }
        }
    }
    if (found) {
        pos = best;
        nextSlot = (uint16_t)((newest + 1) % NV_RING_SLOTS);
    }
}

static void queuePosition() {
    pos.seq++;
    posPending = true;
    nvUpdate();   // writes at once unless the chip is still busy
}

// --- interface --------------------------------------------------------------------------------------
bool nvBegin() {
    settingsDefaults(settings);
    memset(&pos, 0, sizeof(pos));
    nextSlot = 0;
    posPending = false;
    writeCycle = false;
    faults = FAULT_MISSING;

    if (ack(NV_ADDR_PRIMARY)) nvEepromAddress = NV_ADDR_PRIMARY;
    else if (ack(NV_ADDR_ALTERNATE)) nvEepromAddress = NV_ADDR_ALTERNATE;
    else { nvEepromAddress = 0; return false; }
    faults = 0;

    Settings s;
    settingsDefaults(s);
    const LoadResult r = loadRecord(NV_SETTINGS_ADDR, NV_SETTINGS_AREA, NV_MAGIC_SETTINGS, SETTINGS_VERSION,
                                    (uint8_t*)&s, sizeof(s));
    if (r == LOAD_OK && settingsSane(s)) settings = s;
    else if (r != LOAD_BLANK) faults |= FAULT_SETTINGS;

    scanRing();
    return faults == 0;
}

bool nvFault() { return faults != 0; }

FwError nvSaveSettings() {
    if (!nvEepromAddress) return FW_ERR_EEPROM;
    if (!saveRecord(NV_SETTINGS_ADDR, NV_MAGIC_SETTINGS, SETTINGS_VERSION, (const uint8_t*)&settings,
                    sizeof(settings)))
        return FW_ERR_EEPROM;
    faults &= (uint8_t)~(FAULT_SETTINGS | FAULT_WRITE);
    return FW_OK;
}

void nvLoadPositions(SavedPositions& out) {
    out.focValid = (pos.flags & SLOT_FOC_SAVED) && !(pos.flags & SLOT_FOC_MOVING);
    out.focPos   = pos.focPos;
    out.rotValid = (pos.flags & SLOT_ROT_SAVED) && !(pos.flags & SLOT_ROT_MOVING);
    out.rotCdeg  = pos.rotCdeg;
}

void nvMarkMoving(AxisId axis) {
    const uint8_t bit = (axis == AXIS_FOC) ? SLOT_FOC_MOVING : SLOT_ROT_MOVING;
    if (pos.flags & bit) return;   // already marked (e.g. a second move before the idle save)
    pos.flags |= bit;
    queuePosition();
}

void nvSavePosition(AxisId axis, int32_t value) {
    if (axis == AXIS_FOC) {
        const uint8_t f = (uint8_t)((pos.flags & ~SLOT_FOC_MOVING) | SLOT_FOC_SAVED);
        if (f == pos.flags && value == pos.focPos) return;
        pos.flags = f;
        pos.focPos = value;
    } else {
        const uint8_t f = (uint8_t)((pos.flags & ~SLOT_ROT_MOVING) | SLOT_ROT_SAVED);
        if (f == pos.flags && value == pos.rotCdeg) return;
        pos.flags = f;
        pos.rotCdeg = value;
    }
    queuePosition();
}

bool nvLoadImuCal(ImuCalibration& out) {
    out = ImuCalibration{};
    if (!nvEepromAddress) return false;
    ImuCalibration c{};
    const LoadResult r = loadRecord(NV_IMU_ADDR, NV_IMU_AREA, NV_MAGIC_IMU, NV_IMU_VERSION, (uint8_t*)&c, sizeof(c));
    if (r == LOAD_BAD) faults |= FAULT_IMU;
    if (r != LOAD_OK || c.valid != 1) return false;
    out = c;
    return true;
}

FwError nvSaveImuCal(const ImuCalibration& cal) {
    if (!nvEepromAddress) return FW_ERR_EEPROM;
    if (!saveRecord(NV_IMU_ADDR, NV_MAGIC_IMU, NV_IMU_VERSION, (const uint8_t*)&cal, sizeof(cal)))
        return FW_ERR_EEPROM;
    faults &= (uint8_t)~(FAULT_IMU | FAULT_WRITE);
    return FW_OK;
}

void nvUpdate() {
    if (!posPending || !nvEepromAddress) return;
    if (!readyNow()) return;
    pos.crc = slotCrc(pos);
    if (writeChunk(NV_RING_ADDR + nextSlot * NV_SLOT_SIZE, (const uint8_t*)&pos, NV_SLOT_SIZE)) {
        nextSlot = (uint16_t)((nextSlot + 1) % NV_RING_SLOTS);
        posPending = false;
    } else {
        // the chip did not take the slot: keep it pending, report once
        if (!(faults & FAULT_WRITE)) statusRaise(FW_ERR_EEPROM);
        faults |= FAULT_WRITE;
        lastWriteMs = millis();
        writeCycle = true;   // retry after a write-cycle time instead of every loop pass
    }
}
