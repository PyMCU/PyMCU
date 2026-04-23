# pymcu-xtensa

PyMCU Xtensa support: codegen backend and toolchain for ESP8266, ESP32, ESP32-S2, ESP32-S3.

## Supported targets

| Chip       | Core  | Notes                       |
|------------|-------|-----------------------------|
| ESP8266    | LX106 | 80 / 160 MHz                |
| ESP32      | LX6   | 240 MHz dual-core           |
| ESP32-S2   | LX7   | 240 MHz single-core         |
| ESP32-S3   | LX7   | 240 MHz dual-core           |

## ABI

call0 (flat register model — no windowed register rotation):

- `a0` — return address (set by `call0` instruction)
- `a1` — stack pointer (grows downward)
- `a2` — first argument / return value
- `a3`–`a7` — arguments 2–6

## Codegen modes

Two output modes are supported by the C# backend runner (`pymcuc-xtensa`):

- **GAS assembly** (default): emits Xtensa GNU AS syntax (`.asm`).
  Assembled by `xtensa-*-elf-as` or by Clang.
- **LLVM IR** (`--emit-llvm`): emits LLVM IR (`.ll`) for consumption by
  `xtensa-esp-elf-clang` from ESP-IDF 5.x+. Enables full LLVM optimisation.

## Toolchain detection (Python plugin)

The `XtensaToolchainPlugin` automatically selects the toolchain:

1. If `xtensa-esp-elf-clang` is on PATH — LLVM pipeline (ESP-IDF 5.x).
2. Otherwise — GNU GAS (`xtensa-esp32-elf-as` / `xtensa-lx106-elf-as`).

## License

MIT
