# ffi-crc8

CRC-8 (Dallas/Maxim) via avr-libc, the same algorithm used by the Arduino
OneWire library.

## What it does

`crc8.c` wraps `_crc_ibutton_update()` from avr-libc `<util/crc16.h>`. The
example computes a CRC over a 7-byte DS18B20-style ROM, feeds the resulting CRC
byte back, and confirms the self-check yields `0x00` (exactly the check
OneWire.cpp performs on a 1-Wire ROM code).

## Expected output (UART, 9600 baud)

```
CRC8
R:XX     <- CRC of the 7 ROM bytes
V:00     <- self-check (data + CRC) == 0x00
Z:00     <- crc8_update(0, 0) == 0x00
OK
```

## Cross-check on Arduino

```cpp
#include <OneWire.h>
uint8_t rom[7] = {0x28,0x11,0x22,0x33,0x44,0x55,0x66};
Serial.println(OneWire::crc8(rom, 7), HEX);   // matches R:XX
```

## Key concepts

- Linking against avr-libc utilities through FFI
- 1-Wire / DS18B20 checksum validation

## Build & flash

```bash
cd examples/avr/ffi-crc8
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
