# pymcu-backend-pic

PIC backend extension for the [PyMCU](https://github.com/PyMCU/PyMCU) compiler.
Generates assembly for Microchip PIC microcontrollers from statically-typed Python source.

---

## Architecture Tier Support

| Tier | `arch` value | Devices | Status |
|------|-------------|---------|--------|
| **Supported WIP** | `pic18` | PIC18Fxxxx (e.g. PIC18F4520) | Full feature set targeted |
| **Experimental** | `pic14` | PIC16Fxxx midrange | 8+16-bit, limited variable space |
| **Experimental** | `pic12` | PIC12Fxxx / PIC10Fxxx baseline | 8-bit only, extreme constraints |

The compiler emits a `[WARNING]` line for Experimental tiers at the top of the generated assembly.

---

## Installation

```bash
pip install pymcu[pic]
```

or for standalone use:

```bash
pip install pymcu-backend-pic
```

---

## Usage

In your `pyproject.toml`:

```toml
[tool.pymcu]
arch = "pic18"         # or "pic14" / "pic12"
chip = "PIC18F4520"    # exact chip name passed to assembler

[tool.pymcu.fuses]
# PIC14 only: enable PAGESEL for programs > 2K words
# multipage = "true"
```

Then build with:

```bash
pymcu build
```

---

## PIC18 — Supported WIP

- **Signed comparisons**: Uses `N` and `OV` STATUS bits (`N XOR OV = 1` → less-than).
- **Flash tables** (`const uint8[N]` in ROM): Uses `TBLRD` via `TBLPTR` (TBLPTRU:H:L).
- **Soft 8-bit divide** (`//`, `%`): Emits `__pic18_div8` shift-and-subtract subroutine once per compilation unit.
- **Variable-count shifts**: Emits `RLNCF`/`RRNCF` loop with `TSTFSZ`/`BRA` control.
- **ISR context save**: Conditionally saves `FSR0` (if ISR uses indirect access) and `PRODL/PRODH` (if ISR uses multiply). `W`, `STATUS`, `BSR` use PIC18 hardware shadow registers (`RETFIE FAST`).
- **Variables**: All variables must fit in Access Bank GPRs (max 80 bytes at 0x020–0x06F).

### Remaining limitations

- No banking support beyond the Access Bank (80-byte limit).
- 16-bit divide not yet emitted.
- 32-bit types and `float` are not supported.

---

## PIC14 — Experimental

- **Signed comparisons**: Sign-bit XOR technique (no `OV` bit on PIC14).
- **Flash tables**: `RETLW` sequences with `ADDWF PCL` computed-goto lookup.
- **Soft 8-bit divide**: Emits `__pic14_div8` subroutine.
- **Variable-count shifts**: `RLF`/`RRF` rotate loops with `BCF STATUS,C` clearing.
- **ISR context**: Saves `W`, `STATUS`, and `FSR` (if ISR uses indirect access).
- **PAGESEL**: Disabled by default. Enable with `fuses.multipage = "true"` for programs spanning more than one 2K-word page.
- **Variables**: Max 76 bytes in Bank 0 GPRs (0x20–0x6B).

---

## PIC12 — Experimental

Supports **8-bit operations only**. All of the following produce a compile-time error:

- 16-bit or larger integer types
- `float`
- Variable-count shifts
- Multiply or divide
- Runtime-indexed flash array access

Supported features:
- Constant-index RAM array access and flash array access (`RETLW` table)
- FSR/INDF-based indirect load/store, BitSet/BitClear
- 2-level hardware call stack (avoid deeply nested calls)
