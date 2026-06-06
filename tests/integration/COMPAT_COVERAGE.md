# Compat-layer test coverage audit

Cross-reference of the MicroPython (`pymcu_micropython`) and CircuitPython
(`pymcu_circuitpython`) compat surface against the AVR integration fixtures.
Generated during the 2026-06 stabilization. Update when adding/removing compat API
or fixtures.

Legend: ✅ covered · ⚠️ partial (class used but not all methods/modes) · ❌ gap

## MicroPython (`machine`, `utime`, …)

| API | Coverage | Fixture(s) |
|-----|----------|-----------|
| `machine.Pin` (high/low/toggle/value/mode/init) | ✅ | compat-mp-pin, compat-mp-pin-pull |
| `machine.Pin.irq` | ✅ | PinIrqMp |
| `machine.UART` (write/read/readline/readinto) | ✅ | compat-mp-uart-readline, mp-uart-echo (RP2040) |
| `machine.UART.any()` | ❌ | — |
| `machine.ADC.read` | ✅ | lm35-sensor-mp |
| `machine.ADC.read_u16` | ❌ | — |
| `machine.PWM` (+ freq) | ✅ | compat-mp-pwm, compat-mp-pwm-freq |
| `machine.SPI` (write/read/readinto) | ✅ | compat-mp-spi-rw |
| `machine.SPI.write_readinto` | ⚠️ | not asserted directly |
| `machine.I2C` (scan/writeto/readfrom/readfrom_into) | ✅ | compat-mp-i2c-scan, compat-mp-i2c-rw |
| `machine.Timer` (+ freq) | ✅ | compat-mp-timer, compat-mp-timer-freq |
| `machine.WDT` (feed) | ❌ | native watchdog covered (WatchdogTests); MP shim class not |
| `machine.Signal` (on/off/value, active-low) | ❌ | — |
| `machine.mem8` / `machine.mem16` (`_Mem8`/`_Mem16`) | ❌ | — |
| `machine.reset/idle/lightsleep/deepsleep` | ❌ | — |
| `machine.disable_irq/enable_irq/freq` | ❌ | — |
| `machine.time_pulse_us` | ✅ | dht-sensor-mp (indirect) |
| `utime.sleep_ms/us`, `ticks_*` | ✅ | compat-mp-utime |
| `avr.EEPROM` / `avr.SoftSPI` / `avr.SoftI2C` | ✅ | EepromTests, SoftSpiTests (pymcu ext, not std MP) |
| `lm35.LM35` | ✅ | lm35-sensor-mp |

## CircuitPython (`digitalio`, `analogio`, `busio`, …)

| API | Coverage | Fixture(s) |
|-----|----------|-----------|
| `digitalio.DigitalInOut` direction/value + list-comp + for-in + enumerate | ✅ | compat-cp-gpio |
| `digitalio.Pull` / `digitalio.DriveMode` (pull-up, open-drain) | ❌ | direction/value only |
| `digitalio.Direction` | ✅ | compat-cp-gpio |
| `analogio.AnalogIn` / `analogio.AnalogOut` | ❌ | **no fixture at all** |
| `busio.UART` | ⚠️ | used incidentally for status output in several cp fixtures; RP2040 cp-digitalio-uart asserts it. No dedicated AVR test |
| `busio.I2C` / `busio.SPI` | ❌ | — (AVR) |
| `pwmio.PWMOut` | ✅ | compat-cp-pwmio |
| `microcontroller` (Processor/nvm/watchdog/reset_reason) | ✅ | compat-cp-microcontroller |
| `neopixel.NeoPixel` | ✅ | NeoPixel (instance-array framebuffer) |
| `supervisor` | ✅ | compat-cp-supervisor |
| `alarm` (TimeAlarm/PinAlarm) | ✅ | compat-cp-alarm |
| `time` | ✅ | compat-cp-time |

## Prioritized gaps to close (recommended next fixtures)

1. **analogio (AnalogIn/AnalogOut)** — an entire CP module with zero coverage. Model on
   `lm35-sensor-mp` for the ADC injection pattern.
2. **machine.Signal** — active-low inversion logic is exactly the kind of subtle
   property/branch code that regressed before; cheap to cover (GPIO assert).
3. **digitalio Pull / DriveMode** — extend compat-cp-gpio (or a sibling) to assert PUE/DDR
   for pull-up and open-drain.
4. **busio.I2C / busio.SPI on AVR** — dedicated fixtures (mirror compat-mp-i2c-rw / spi-rw).
5. **machine.mem8 / mem16**, **UART.any()**, **machine.WDT / reset** — small register/flag
   assertions.

Note: the ZCA-instance-array + property-setter machinery these compat modules lean on is
now pinned by `tests/unit/IR/ZcaInlineRegressionTests.cs` (IR-level) and exercised
end-to-end by `compat-cp-gpio` and `zca-instance-array` (self-contained, submodule-free).
