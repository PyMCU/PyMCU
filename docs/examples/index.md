# Examples

Annotated firmware examples showing real-world PyMCU patterns.

All examples target ATmega328P / Arduino Uno unless otherwise noted. Each ships with a full
integration test suite (AVR8Sharp cycle-accurate simulator).

```{toctree}
:maxdepth: 1

blink
uart-echo
sensor-dashboard
```

---

## Quick index

| Example | Flash | SRAM | Topics |
|---|---|---|---|
| {doc}`blink` | 124 B | 0 B | GPIO, delay |
| {doc}`uart-echo` | 170 B | 0 B | UART, read/write |
| {doc}`sensor-dashboard` | ~800 B | 4 B | DHT11, UART, timer |
