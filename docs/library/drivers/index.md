# Device Drivers

PyMCU ships a growing collection of device drivers in `pymcu.drivers`. All drivers are
target-independent where possible, with architecture-specific pulse-timing delegated to
architecture sub-modules.

| Driver | Module | Description |
|---|---|---|
| DHT11 | `pymcu.drivers.dht11` | Temperature + humidity (1-Wire protocol) |
| DS18B20 | `pymcu.drivers.ds18b20` | Precision temperature (1-Wire, 12-bit) |
| LM35 | `pymcu.drivers.lm35` | Analog temperature sensor (ADC-based) |

```{toctree}
:maxdepth: 1

dht11
ds18b20
```
