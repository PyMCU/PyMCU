/*
 * crc8.c -- CRC-8 Dallas/Maxim via avr-libc <util/crc16.h>
 *
 * Thin wrapper around _crc_ibutton_update(), the same function used by
 * the Arduino OneWire library (OneWire.cpp) to verify 1-Wire ROM codes
 * and DS18B20 scratchpad checksums.
 *
 * avr-libc is MIT-compatible (modified BSD).
 * https://www.nongnu.org/avr-libc/user-manual/group__util__crc.html
 */

#include <util/crc16.h>
#include "crc8.h"

uint8_t crc8_update(uint8_t crc, uint8_t data)
{
    return _crc_ibutton_update(crc, data);
}
