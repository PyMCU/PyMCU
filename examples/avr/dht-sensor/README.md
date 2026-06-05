# dht-sensor

Read humidity and temperature from a **DHT11** sensor with a custom bit-banged
driver.

## What it does

`dht11.py` implements the DHT11 single-wire protocol (start pulse, sensor
acknowledge, 40-bit read, checksum validation) as a zero-cost class abstraction.
`main.py` measures every 2 seconds and prints `H:<hum> T:<temp>` over UART,
lighting the built-in LED on success and printing `ERR` on a failed read.

This is a good multi-file example: `main.py` imports the local `dht11` module.

## Hardware

- Arduino Uno @ 16 MHz
- DHT11 DATA → **D2 (PD2)** with a 4.7 kΩ pull-up to +5 V
- DHT11 VCC → +5 V, GND → GND
- Serial terminal at **9600 baud**

## Expected output

```
DHT11
H:XX T:XX     <- on success
ERR           <- on timeout / checksum failure
```

## Key concepts

- `@inline` driver class (ZCA) with runtime pin direction switching
- `pin.pulse_in()` for protocol timing
- Checksum validation

## Build & flash

```bash
cd examples/avr/dht-sensor
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
