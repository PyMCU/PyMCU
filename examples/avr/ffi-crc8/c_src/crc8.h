#pragma once
#include <stdint.h>

/*
 * CRC-8 Dallas/Maxim (iButton) -- wraps _crc_ibutton_update from avr-libc.
 *
 * This is the exact CRC-8 algorithm used by the Arduino OneWire library
 * (OneWire.cpp: OneWire::crc8) to verify 1-Wire device ROM codes and
 * DS18B20 temperature sensor scratchpad data.
 *
 * Polynomial: x^8 + x^5 + x^4 + 1 (0x8C reflected)
 * Initial value: 0x00
 */

/* Accumulate one byte into a running CRC. Seed with crc=0x00.
 * Feeding all data bytes followed by the CRC byte should yield 0x00. */
uint8_t crc8_update(uint8_t crc, uint8_t data);
