# PyMCU Libraries

:::{admonition} Architectural Shift: MicroPython Compatibility
:class: important

PyMCU's standard library is being restructured. The official user-facing API will soon be aligned with the **MicroPython / CircuitPython `machine` and `board` modules**.

The modules documented below (`pymcu.hal.*` and `pymcu.drivers.*`) are considered **internal foundations (HAL)**. While they work perfectly, they are designed to be wrapped by the upcoming compatibility layers. Users are strongly encouraged to use the `machine` module (when available) for better portability across different hardware.
:::

The PyMCU internal library provides hardware abstraction layer (HAL) modules and device drivers.
All modules compile to tight native machine code — there is no Python runtime on the device.

## Internal HAL modules

| Module | Import path | Description |
|---|---|---|
| {doc}`GPIO / Pin <gpio>` | `pymcu.hal.gpio` | Digital I/O, pin interrupts |
| {doc}`UART <uart>` | `pymcu.hal.uart` | Serial communication |
| {doc}`ADC <adc>` | `pymcu.hal.adc` | Analog-to-digital conversion |
| {doc}`Timer <timer>` | `pymcu.hal.timer` | Hardware timers |
| {doc}`PWM <pwm>` | `pymcu.hal.pwm` | Pulse-width modulation |
| {doc}`SPI <spi>` | `pymcu.hal.spi` | SPI bus (hardware + soft) |
| {doc}`I2C <i2c>` | `pymcu.hal.i2c` | I2C bus (hardware + soft) |
| {doc}`EEPROM <eeprom>` | `pymcu.hal.eeprom` | Non-volatile byte storage |
| {doc}`Watchdog <watchdog>` | `pymcu.hal.watchdog` | Watchdog timer |
| {doc}`Power / Sleep <power>` | `pymcu.hal.power` | Sleep modes |
| {doc}`Time / Delays <time>` | `pymcu.time` | Busy-wait delays |

## Device drivers

| Driver | Import path | Description |
|---|---|---|
| {doc}`DHT11 <drivers/dht11>` | `pymcu.drivers.dht11` | Temperature and humidity sensor |

## Third-party libraries

{doc}`Writing a PyMCU library <authoring>` — package layout, manifest, architecture
dispatch and publishing, for anyone shipping a driver of their own.

## Design principles

All HAL classes are `@inline` — they have **zero SRAM cost**. Instantiating a `Pin` or `UART`
compiles to register writes; no struct is allocated on the MCU. See {doc}`../language/type-system`
for details on the Zero-Cost Abstraction model.

```{toctree}
:maxdepth: 1
:hidden:

gpio
uart
adc
timer
pwm
spi
i2c
eeprom
watchdog
power
time
drivers/index
authoring
```