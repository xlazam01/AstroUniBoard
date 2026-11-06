// Protocol V2, OpenWheel framing: ":NN[params]#". Every command is answered:
//   queries -> "<value>#", set/actions -> "OK#", rejected -> "ERR,<code>#" (error_codes.h).
// Command numbers: common 20-49 (OpenWheel numbers), focuser 50-69, rotator 70-89, env/IMU 90-99.
// Spec: docs/PROTOCOL.md (common block drafted; device blocks #8-#10).
#pragma once
#include <stdint.h>

class Protocol {
public:
    void process();   // read and answer everything waiting on PROTOCOL_SERIAL

private:
    char    buf[48];
    uint8_t len = 0;
    bool    inCommand = false;
    bool    overflow = false;

    void handleCommand();
};
