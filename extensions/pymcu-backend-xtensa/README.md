# pymcu-xtensa

Extension package for the PyMCU compiler adding Xtensa ISA support.

Supports: ESP8266 (LX106), ESP32 (LX6), ESP32-S2 (LX7), ESP32-S3 (LX7).

Uses the **call0 ABI** (flat register model, no windowed register rotation)
so the generated assembly is compatible with ESP-IDF `-mabi=call0` toolchains
and with MicroPython's native/viper code emitter convention.
