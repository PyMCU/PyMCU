# pymcu-rp2040

RP2040 (Raspberry Pi Pico, dual ARM Cortex-M0+) support for the PyMCU compiler.

Unlike the AVR/PIC/RISC-V backends, this backend does **not** emit assembly
directly. It lowers the architecture-agnostic PyMCU IR (`.mir`) to **LLVM IR
text (`.ll`)** and hands it to LLVM, so register allocation, instruction
selection, the AAPCS calling convention and all optimization passes are done by
LLVM targeting `thumbv6m-none-eabi` (`-mcpu=cortex-m0plus`).

## Pipeline

```
pymcuc --emit-ir          ->  firmware.mir   (target-agnostic IR, 32-bit pointers)
pymcuc-rp2040 (this pkg)  ->  firmware.ll    (LLVM IR text)
LLVM toolchain            ->  opt -> llc -> ld.lld -> llvm-objcopy
                              + generic boot2 (crc32) + crt0  ->  firmware.bin
```

The flat flash image (`firmware.bin`, boot2 at offset 0) is what the RP2040Sharp
emulator's `PicoSimulation.LoadFlash(...)` consumes in the integration tests.

## Layout

- `src/python/pymcu/backend/rp2040/` - backend plugin (wraps the AOT binary)
- `src/csharp/lib/` - `Rp2040BackendProvider` + `Targets/RP2040/Rp2040LlvmCodeGen`
- `src/csharp/cli/` - `pymcuc-rp2040` runner CLI
- `src/runtime/` - generic boot2, crt0 startup, linker script

## Status

Alpha. MVP scope: single-core (core0), GPIO + UART0. No GC / exceptions / float yet.
