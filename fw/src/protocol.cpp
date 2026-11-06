#include <Arduino.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>

#include "config.h"
#include "protocol.h"
#include "settings.h"

// One write per reply, so the host never sees half a reply.
void protoReply(const char* s, int n) {
    if (n <= 0) return;
    PROTOCOL_SERIAL.write((const uint8_t*)s, (size_t)n);
}

static bool identified = false;   // cleared only by a reset (#58)

void protoSetIdentified() { identified = true; }

void protoOk() { protoReply("OK#", 3); }

void protoError(FwError code) {
    char out[12];
    protoReply(out, snprintf(out, sizeof(out), "ERR,%d#", (int)code));
}

void protoInt(int32_t v) {
    char out[16];
    protoReply(out, snprintf(out, sizeof(out), "%ld#", (long)v));
}

void protoPrintf(const char* fmt, ...) {
    char out[96];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(out, sizeof(out), fmt, ap);
    va_end(ap);
    if (n >= (int)sizeof(out)) n = sizeof(out) - 1;
    protoReply(out, n);
}

bool protoSplit(char* p, ProtoArgs& a) {
    a.n = 0;
    if (*p == ' ' || *p == ',') p++;
    if (*p == '\0') return true;
    for (;;) {
        if (a.n >= PROTO_MAX_ARGS) return false;
        a.v[a.n++] = p;
        char* c = strchr(p, ',');
        if (!c) break;
        *c = '\0';
        p = c + 1;
    }
    for (uint8_t i = 0; i < a.n; i++)
        if (a.v[i][0] == '\0') return false;
    return true;
}

bool protoInt32(const char* s, int32_t& out) {
    const char* p = s;
    bool neg = false;
    if (*p == '+' || *p == '-') { neg = (*p == '-'); p++; }
    if (*p == '\0') return false;
    int64_t v = 0;
    for (; *p; p++) {
        if (*p < '0' || *p > '9') return false;
        v = v * 10 + (*p - '0');
        if (v > 2147483648LL) return false;
    }
    if (neg) v = -v;
    if (v > INT32_MAX || v < INT32_MIN) return false;
    out = (int32_t)v;
    return true;
}

int protoAxis(const char* s) {
    if (s[0] == '\0' || s[1] != '\0') return -1;
    if (s[0] == 'F' || s[0] == 'f') return AXIS_FOC;
    if (s[0] == 'R' || s[0] == 'r') return AXIS_ROT;
    return -1;
}

void Protocol::process() {
    while (PROTOCOL_SERIAL.available() > 0) {
        const char c = (char)PROTOCOL_SERIAL.read();
        if (c == ':') {
            inCommand = true;
            overflow = false;
            len = 0;
        } else if (c == '#') {
            if (!inCommand) continue;
            buf[len] = '\0';
            if (overflow) protoError(FW_ERR_BAD_FORMAT);
            else handleCommand();
            inCommand = false;
        } else if (inCommand) {
            if (len < PROTO_MAX_LINE) buf[len++] = c;
            else overflow = true;
        }
        // bytes outside ":...#" are noise and are ignored without a reply
    }
}

void Protocol::handleCommand() {
    if (len < 2 || !isDigit(buf[0]) || !isDigit(buf[1])) { protoError(FW_ERR_BAD_FORMAT); return; }
    const uint8_t cmd = (uint8_t)((buf[0] - '0') * 10 + (buf[1] - '0'));
    char* params = &buf[2];

    // A device on the shared test PID 1209:0001 must be identified before it acts on anything.
    if (!identified && cmd != 25 && cmd != 32) { protoError(FW_ERR_NOT_IDENTIFIED); return; }

    if (cmd >= 90)      protocolEnv(cmd, params);
    else if (cmd >= 70) protocolRotator(cmd, params);
    else if (cmd >= 50) protocolFocuser(cmd, params);
    else if (cmd >= 20) protocolCommon(cmd, params);
    else                protoError(FW_ERR_UNKNOWN_CMD);
}
