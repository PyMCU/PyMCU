# UART — `pymcu.hal.uart`

```python
from pymcu.hal.uart import UART
```

Serial communication over USART0. `UART` is an `@inline` class — it compiles to USART register
writes with zero SRAM allocation.

---

## class `UART`

### `UART(baud=9600, bits=8, parity=0, stop=1)`

Initializes the hardware UART peripheral. On AVR, configures USART0.

`bits`, `parity` and `stop` are the frame format. Parity is numbered the same on every
architecture: `0` none, `1` even, `2` odd. All three are compile-time constants, so an 8N1
UART emits the same single register write it always did, and a frame the part cannot send is
refused where the `UART` is constructed: a 9-bit frame keeps its ninth bit in a different
register and this HAL reads one register per byte, so it says so rather than sending eight.

They used to be nowhere at all, and every layer above accepted them from its caller and
dropped them: a program that asked for 7E1 ran 8N1 and the frames went out wrong in silence.

| Baud rate | UBRR0 (16 MHz) |
|---|---|
| 9600 | 103 |
| 19200 | 51 |
| 38400 | 25 |
| 57600 | 16 |
| 115200 | 8 |

### Methods

| Method | Description |
|---|---|
| `write(data: uint8)` | Send a single byte (blocks until UDRE0 is set) |
| `read() -> uint8` | Receive a byte (blocks until RXC0 is set) |
| `read_nb() -> uint8` | Non-blocking read; returns byte if available, else 0 |
| `read_blocking() -> uint8` | Poll RXC until byte arrives and return it |
| `read_byte_isr() -> uint8` | Direct UDR0 read for use inside `@interrupt` handlers |
| `available() -> uint8` | Returns 1 if a byte is waiting in the hardware's receive register |
| `read_timeout(ms: uint16) -> int16` | Poll for a byte, giving up after `ms` milliseconds; `-1` means nothing arrived |
| `start_buffered_rx(size=0)` | Turn on the interrupt-driven receive ring: RXCIE, SEI and the ISR that fills it. A `size` larger than the ring is refused |
| `rx_count() -> uint8` | How many bytes are waiting in the ring (a count, where `available()` is a flag) |
| `rx_buffer_size() -> uint8` | The ring's capacity, 64 bytes, as a compile-time constant |
| `rx_read_timeout(ms: uint16) -> int16` | Read one byte from the ring, giving up after `ms` milliseconds; `-1` means nothing arrived |
| `write_str(s: const[str])` | Send a flash string via LPM loop |
| `println(s: const[str])` | `write_str(s)` + newline (0x0A) |
| `print_byte(value: uint8)` | Print `value` as decimal digits + newline |
| `read_line(buf: uint8[N], max_len: uint8) -> uint8` | Read until `\n` or `max_len` bytes into a fixed-size buffer; returns number of bytes read |
| `enable_rx_interrupt()` | Enable RXC interrupt (RXCIE0 in UCSR0B) |
| `rx_isr()` | ISR body: reads UDR0 into ring buffer |

---

## Examples

### Hello, world

```python
from pymcu.hal.uart import UART

uart = UART(9600)
uart.println("hello")
```

### Send and receive bytes

```python
from pymcu.hal.uart import UART
from pymcu.types import uint8

uart = UART(9600)
uart.write(65)          # sends 'A'

b: uint8 = uart.read()  # blocking receive
uart.write(b)           # echo back
```

### Echo loop

```python
from pymcu.hal.uart import UART
from pymcu.types import uint8

def main():
    uart = UART(9600)
    while True:
        b: uint8 = uart.read()
        uart.write(b)
```

### Non-blocking receive with walrus

```python
from pymcu.hal.uart import UART
from pymcu.types import uint8

def main():
    uart = UART(9600)
    while True:
        if c := uart.read_nb():     # walrus: assign and test in one
            uart.write(c)
```

### Interrupt-driven ring buffer

```python
from pymcu.hal.uart import UART
from pymcu.types import uint8

uart = UART(9600)

@interrupt(0x0018)    # USART0 RX Complete (ATmega328P)
def on_rx():
    uart.rx_isr()     # stores byte in ring buffer

def main():
    uart.enable_rx_interrupt()
    while True:
        if uart.available():
            b: uint8 = uart.read_nb()
            uart.write(b)
```

### Read a line of text

```python
from pymcu.hal.uart import UART
from pymcu.types import uint8

uart = UART(9600)
buf: uint8[64] = [0] * 64

def main():
    uart.println("send a line:")
    while True:
        n: uint8 = uart.read_line(buf, 64)   # blocks until '\n' or 64 bytes
        for i in range(n):
            uart.write(buf[i])               # echo line back
```

`read_line` reads bytes until a `\n` (0x0A) is received or `max_len` bytes have been
stored — whichever comes first. The newline is **not** stored in the buffer. Returns the
number of bytes written.

---

## Receiving with a deadline

A read that never returns is not a timeout. `read_timeout` and `rx_read_timeout` poll for a
byte and give up:

```python
from pymcu.hal.uart import UART
from pymcu.types import int16

u = UART(9600)
b: int16 = u.read_timeout(500)     # half a second
if b < 0:
    u.write_str("no answer\n")
else:
    u.write(b & 0xFF)
```

The inner loop runs 100 times per millisecond and spends 9 us of calibrated delay in each
pass. Measured in avr8sharp at 16 MHz, a 100 ms timeout with nothing arriving takes
1 581 950 cycles, which is 98.9 ms: **1.1 % short**. That is the accuracy on offer.

## Buffered receive

Polled, the UART holds one byte and `available()` is a flag. `start_buffered_rx()` turns on
the receive interrupt and the 64-byte ring behind it, and `rx_count()` becomes a real count:

```python
u = UART(115200)
u.start_buffered_rx()
while u.rx_count() < 4:
    pass
```

The ring is a fixed array allocated at compile time and costs nothing in a program that never
turns it on: a UART program that only writes is the same size with the ring as without,
because nothing references the array and it is eliminated. A byte that arrives with the ring
full is dropped.
