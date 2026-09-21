# RFC 0008: files without a filesystem -- open() over a compile-time table, and one file protocol for every target class

- Status: **PROPOSED**. No compiler changes in this RFC; it fixes the shape, the error
  matrix and the gate an implementation is held to.
- Date: 2026-09-21
- Demanding program: `adafruit_framebuf.BitmapFont` (Adafruit_CircuitPython_framebuf 1.6.12),
  reached by `display.text("...", 0, 0, 1)` on an unmodified `adafruit_ssd1306`. Today
  `open()` is refused ("No filesystem", `docs/language/limitations.md`), so the OLED driver
  builds but cannot draw text.
- Affects (once implemented): `lib/src/pymcu/fs.py` (new), `lib/src/pymcu/os.py`
  (`stat`, `listdir`), the `open` builtin in `src/compiler/IR/IRGenerator/Call.cs`, the
  `struct.unpack(fmt, f.read(n))` fusion, `src/driver/commands/build.py` (embedding),
  the `.mir` (one flash blob per file), every backend's flash-table emission (already
  exists for `const` tables).

## 0. Decisions this RFC encodes

1. **The table does not exist on the chip.** `open(name, mode)` with a literal `name` (or a
   field/constant holding one) resolves at compile time to a handle over a flash blob:
   `(base: const, length: const, pos: uint16)`. The name lookup folds away. This is the same
   mechanism `const` tables already use to land in flash on demand.
2. **One file protocol, several backends.** The protocol is CPython's file object as
   CircuitPython exposes it: `read(n)`, `readinto(buf)`, `readline()`, `seek(off, whence)`,
   `tell()`, `close()`, `with`, plus `os.stat(name)` and `os.listdir(dir)`. Every backend
   implements the same protocol in auditable Python under `lib/src/pymcu/fs.py`; a library
   such as `BitmapFont` must not be able to tell which backend it runs on.
3. **Errors are placed by when they are decidable.** A condition known at compile time is a
   `CompileError` raised by the shim on the author's line; a condition Python answers at run
   time without raising (`read()` past the end returns `b""`) keeps that semantics; an
   `except` clause whose exception can no longer happen is a dead branch, folded exactly as
   `except ImportError` is folded today (#351), never left as silently wrong code.
4. **No heap.** `read(n)` needs `n` at compile time (its result is a stack/flash window,
   not an allocation); a run-time `n` is refused with the `readinto(buf)` spelling in the
   message. `readline()` needs a declared maximum line length, as `input()` does.
5. **Writes follow the backend.** A backend that cannot write (romfs, every chip) refuses
   `"w"`, `"a"`, `"r+"` and `write()` at compile time. A backend that can (a real filesystem
   on a board that declares one) accepts them.

## 1. What the demanding program needs, line by line

```python
self._font = open(self.font_name, "rb")                                 # (a)
self.font_width, self.font_height = struct.unpack("BB", self._font.read(2))  # (b)
if 2 + 256 * self.font_width != os.stat(font_name)[6]:                  # (c)
    raise RuntimeError("Invalid font file: " + font_name)
except OSError: print("Could not find font file", font_name); raise     # (d)
except OverflowError: pass                                              # (e)
self._font.seek(2 + (ord(char) * self.font_width) + char_x)             # (f)
line = struct.unpack("B", self._font.read(1))[0]                        # (g)
self._font.close()                                                      # (h)
```

| line | construct | resolution |
|---|---|---|
| (a) | `open(field, "rb")`, field holds the literal `"font5x8.bin"` (a default argument) | compile-time handle over the embedded blob; `"rb"` and `"r"` both accepted, the distinction (text decoding) is not observable here |
| (b) | `struct.unpack("BB", f.read(2))` | fused: two `LPM` (AVR) / two byte loads (RP) at `base + pos`, `pos += 2`; no `bytes` object |
| (c) | `os.stat(name)[6]` | compile-time constant (the blob length); the `if` folds |
| (d) | `except OSError` around an `open` of an embedded name | dead branch. If the name is NOT embedded, the handler is the branch that runs at compile time: its `raise` becomes the `CompileError` "`font5x8.bin` is not embedded in this build; put it under `sources` or list it in `[tool.pymcu] files`" |
| (e) | `except OverflowError` | dead branch (no `os.stat` overflow: the size is a constant) |
| (f) | `seek(k)` with a run-time `k` | `pos = k` (a `uint16` store); `whence` other than 0 folds arithmetically |
| (g) | `struct.unpack("B", f.read(1))[0]` | fused as (b); one load |
| (h) | `close()` | no-op on romfs |

Also reached by `FrameBuffer.text`: `self._font.font_name != font_name` (two compile-time
strings, folds), `BitmapFont(font_name)` constructed lazily inside `text()` (a ZCA field
assigned outside `__init__`, allowed since the 2026-09-15 decision).

## 2. Where the bytes come from

The driver embeds, in this order of precedence:

1. every path listed in `[tool.pymcu] files = ["font5x8.bin", "data/*.bin"]`, relative to
   the project root;
2. every literal name an `open()` in the program (entry file, project modules, installed
   libraries) names, when a file of that name exists under `sources` or next to the entry
   file (the CircuitPython convention: `font5x8.bin` in the drive root next to `code.py`).

Each file becomes one flash blob in the `.mir` (`{"$t": "blob", "name": ..., "bytes": ...}`)
and one entry in the compile-time table `name -> (label, length)`. A name that appears in
an `open()` and matches nothing is the compile-time error of row (d). Nothing is embedded
that no `open()` reaches, so a program that opens no file is byte-identical (the gate).

Flash cost per backend: AVR `LPM` (1 byte per byte); RP2040/RP2350 XIP (plain pointer);
PIC14 `RETLW` tables (one 14-bit word per byte) and PIC18 `TBLRD` (1 byte per byte). PIC14
is declared as the expensive case in `limitations.md`, not hidden.

## 3. Target classes and backends

| class | examples | backend | writes |
|---|---|---|---|
| no interpreter can run | AVR, PIC, CH32V003 | `romfs` only | refused at compile time |
| interpreter-capable, PyMCU runs as a **native module** inside CircuitPython (`pymcu natmod`) | RP2040, RP2350 | the host's VFS: FAT on flash, USB mass storage, `settings.toml`, all provided by the interpreter; the shim delegates `open()` to the host | as the host allows |
| interpreter-capable, PyMCU **standalone** firmware | RP2040, RP2350 | `romfs` always; a real filesystem is an OPEN decision (section 6) | per backend |

The second row is the preferred path for the Pico class: PyMCU supplies the speed and the
interpreter supplies the drive. It requires nothing from this RFC beyond the protocol being
the same one the host implements, which it is by construction (it is CPython's).

## 4. The shim, sketched

```python
# lib/src/pymcu/fs.py  (auditable; the whole policy is here)
class RomFile:
    def __init__(self, base: const[ptr], length: const[uint16]):
        self._base = base; self._len = length; self._pos = 0
    @inline
    def read(self, n: const[int]):        # a run-time n is refused by the type
        ...                                # returns a compile-time-sized window; fused by struct.unpack
    @inline
    def readinto(self, buf) -> uint16: ...
    @inline
    def seek(self, off: uint16, whence: const[uint8] = 0) -> uint16: ...
    @inline
    def tell(self) -> uint16: return self._pos
    @inline
    def close(self): pass
    @inline
    def write(self, b):
        raise CompileError("this file lives in flash and cannot be written; ...")

@inline
def open(name: const[str], mode: const[str] = "r"):
    if "w" in mode or "a" in mode or "+" in mode:
        raise CompileError("no writable filesystem on this target; ...")
    return __romfs_handle__(name)          # compiler intrinsic: table lookup, or the row (d) error
```

`os.stat(name)` returns a 10-tuple whose `[6]` is the constant length and whose other
fields are 0; `os.listdir(".")` returns the compile-time tuple of embedded names.

## 5. Diagnostics (all on the author's line, none naming a compiler internal)

- `open("x", "w")` on romfs: names the target and the fact that flash is not writable.
- `open(name)` with a run-time `name`: "the file name must be known when the program is
  compiled".
- `f.read(n)` with a run-time `n`: suggests `readinto`.
- a name that is not embedded: names the file and the two ways to embed it (section 2).
- `readline()` without a bound: names `readline(max)` (mirrors `input()`).

## 6. Open questions (decide before phase 2)

1. **Standalone filesystem on the Pico class.** FAT, read-only, in auditable Python over XIP
   flash: measure its flash cost before deciding whether it exists. USB mass storage is the
   expensive part (TinyUSB) and only exists to look like CircuitPython; the native-module
   row already gives that for free.
2. **Native-module host calls.** Measured on 2026-09-21 against the vendored CircuitPython
   (10.3.0-alpha.2, MICROPY_VERSION 1.27.0): the fun table has no `mp_vfs_open`; `open()`
   is one Python-level call (`mp_load_global(MP_QSTR_open)` + `mp_call_function_n_kw`,
   `open` is a static qstr), after which `read`, `seek`, `tell` and `close` go through the
   stream protocol in C (`mp_get_stream_raise(f, READ|IOCTL)` then `sp->read(f, buf, n,
   &err)` and `ioctl(MP_STREAM_SEEK / MP_STREAM_CLOSE)`), which writes straight into a
   PyMCU-owned buffer and allocates nothing. `os.stat` is `mp_import_name` +
   `mp_load_attr` + call + `mp_obj_subscr`. The natmod adapter already compiles against
   `py/dynruntime.h`, so every call above is a macro it can emit; the kernel reaches them
   through `@extern` helpers placed in the adapter, and a file handle crosses as an opaque
   pointer-sized word. Three blockers live in the vendored tree, not in PyMCU: (a)
   `py/dynruntime.h` does not compile (`raise_msg` renamed `raise_msg_str`; fixed upstream
   2026-07-12); (b) `tools/mpy_ld.py` `MP_FUN_TABLE_MP_TYPE_TYPE_OFFSET` is off by one
   (CircuitPython inserted `assert_native_inited`), so every `MP_DEFINE_CONST_FUN_OBJ_*`
   resolves to the wrong type object; (c) stock CircuitPython firmware for every RP board
   has `CIRCUITPY_ENABLE_MPY_NATIVE = 0` and refuses native `.mpy` ("native code in .mpy
   unsupported"). Row 2 of section 3 therefore needs a CircuitPython build with that flag,
   or MicroPython, whose rp2 port enables `MICROPY_EMIT_THUMB` unconditionally and whose
   fun table has no `assert_native_inited` (indices differ: the adapter must be compiled
   against the tree that will load it, which the driver already enforces).
3. Text mode: is `"r"` ever observable for the demanding libraries (line decoding, `\r\n`)?
   Until a library needs it, `"r"` and `"rb"` are the same handle.

## 7. Phases and the gate

- Phase 1 (romfs, AVR + RP): driver embedding, the `.mir` blob, the intrinsic, `fs.py`,
  `os.stat`/`os.listdir`, the `struct.unpack(fmt, f.read(n))` fusion, the fold of
  `except OSError`/`OverflowError` around a compile-time `open`.
  Demandant: the unmodified `adafruit_ssd1306` + `adafruit_framebuf` drawing
  `display.text("PyMCU", 0, 0, 1)` on the emulated Uno, compared byte for byte with the
  CPython I2C oracle of `tests/integration/fixtures/adafruit-ssd1306-unmodified` (the 513-byte
  framebuffer of `show()` must carry the glyphs CPython renders).
- Phase 2: the native-module delegation (row 2 of section 3).
- Gate, every phase: every fixture and example that opens no file is byte-identical; the
  MicroPython blink stays at 142 bytes with `main` at 0x68..0x8D; the size of a program
  that opens `font5x8.bin` grows by the blob (1282 bytes) plus the handle code, which is
  reported per instruction in the RFC's measurement file.
