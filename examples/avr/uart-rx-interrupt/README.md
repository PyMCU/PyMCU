# uart-rx-interrupt

Interrupt-driven UART receive with a 16-byte ring buffer.

## What it does

`uart.irq(on_rx)` registers a handler at the USART_RX vector and enables
`RXCIE0` + `SEI`. The handler calls the stdlib `uart_rx_isr()` to push each
received byte into a 16-byte ring buffer. The main loop drains the buffer
non-blockingly with `rx_available()` / `rx_read()` and echoes each byte.

Contrast with [`uart-echo`](../uart-echo), which blocks on `read()`.

## Hardware

- Arduino Uno @ 16 MHz
- UART TX/RX at **9600 baud**

## Expected output

`RXIRQ` banner, then each received byte echoed back.

## Key concepts

- USART RX interrupt + ring buffer
- Non-blocking consumption from the main loop

## Build & flash

```bash
cd examples/avr/uart-rx-interrupt
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
