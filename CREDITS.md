# Credits

This project stands on the shoulders of two pioneering works that dared to ask
the same question from different angles: *what if microcontrollers could be
programmed in Python?*

---

## Pyastra — The First Pioneer (2006)

**Pyastra** (*PYthon to ASsembler TRAnslator*) was a visionary open-source
project hosted on [SourceForge](https://sourceforge.net/projects/pyastra/) that
set out to compile a subset of Python directly into microcontroller assembly —
years before MicroPython, CircuitPython, or any of today's Python-on-hardware
ecosystems existed.

Written in Python and released under the GNU GPL v2.0, Pyastra targeted the
Microchip PIC16 instruction set and proved, in alpha form, that the idea was
not just a dream: a restricted but recognisable Python syntax could be
mechanically lowered into tight, bare-metal assembly code.

The project never reached maturity — its last release was around 2006 — but the
seed it planted was real. Pyastra showed that compiling Python to native
microcontroller code was *possible*, and that vision is one of the direct
inspirations behind this project.

> "The idea of compiling Python — not interpreting it — on a microcontroller
> was radical in 2006. Pyastra went there first."

---

## Richard Wardlow — Original pyMCU (2012)

> *A very special tribute.*

**Richard Wardlow** ([@rwardlow](https://github.com/rwardlow)) created the
original [pyMCU](https://github.com/rwardlow/pyMCU) project in 2012 and, in
doing so, brought an entirely different kind of magic to the Python-hardware
world.

His pyMCU was an elegantly simple idea: a USB-connected PIC 16F1939 board with
firmware that spoke a clean protocol, paired with a Python library that let
anyone on a PC, Mac, or Linux machine control real electronics — LEDs, sensors,
servos — with nothing more than a few lines of Python. No embedded expertise
required. It was accessible, joyful, and genuinely creative.

Beyond the technical contribution, Richard did something even more meaningful:
**when this project was born, he read about it, reached out, and gave his
gracious blessing for us to carry the PyMCU name forward.** He did not have to
do that. The generosity and community spirit he showed in that moment reflect
everything that makes open-source software worth building.

This project carries the PyMCU name with pride, and with deep gratitude to
Richard for entrusting it to us.

| | Original pyMCU (2012) | This PyMCU |
|---|---|---|
| **Python runs on** | Host computer (PC/Mac/Linux) | Directly on the MCU |
| **MCU role** | Firmware managed by Richard's board | Bare-metal target for compiled code |
| **Model** | USB tether + Python library | AOT compiler — no runtime, no interpreter |
| **Goal** | Python &lt;-&gt; hardware bridge | Python as a first-class MCU language |

Two very different approaches, one shared mission: make microcontrollers
reachable through Python.

> *Thank you, Richard. The name is in good hands.*

---

*PyMCU compiler project — (c) 2026 Ivan Montiel Cardona and the PyMCU Project Authors.*
