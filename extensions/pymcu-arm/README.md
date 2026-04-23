# pymcu-arm

PyMCU ARM Cortex-M backend — RP2040 (Cortex-M0+) and RP2350 (Cortex-M33).

This backend uses **LLVM IR** as the intermediate output format.  The PyMCU
compiler translates its internal Tacky IR into LLVM IR text (`.ll`), and the
bundled toolchain plugin drives `clang` to compile the resulting IR to an
ARM ELF binary (and optionally a UF2 image for direct flashing).

Using LLVM means the full LLVM optimisation pipeline is available: mem2reg,
loop unrolling, instruction combining, and vectorisation for M33 targets.

## Supported chips

| Chip    | Core          | Target triple                  |
|---------|---------------|-------------------------------|
| RP2040  | Cortex-M0+    | `thumbv6m-none-eabi`           |
| RP2350  | Cortex-M33    | `thumbv8m.main-none-eabi`      |

## Toolchain requirements

The toolchain plugin downloads a pre-built LLVM/Clang release from the
[ARM LLVM embedded toolchain](https://github.com/ARM-software/LLVM-embedded-toolchain-for-Arm)
into `~/.pymcu/tools/`.  `clang`, `llvm-objcopy`, and `lld` must be on PATH
or present in the cached directory.

## License

MIT — see `../../LICENSE`.
