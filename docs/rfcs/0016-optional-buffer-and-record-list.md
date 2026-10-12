# RFC 0016: run-time presence for an optional buffer, and a growable list of instances that hold one

- Status: **ACCEPTED (2026-10-12), implementation in progress.** The sections up to 8 are the
  text the maintainer approved; section 9 records how the implementation landed and where it
  differs from that text.
- Date: 2026-10-12
- Affects: `src/compiler/IR/IRGenerator/Call.cs`, `Assign.cs`, `Expr.cs`, `Iteration.cs`,
  `Statements.cs`, `Core.cs`, `State.cs`; the AVR and ARM back ends only through the existing
  `GcAlloc`, `LoadIndirect`, `StoreIndirect`, `BytearrayLoad` and `BytearrayStore` nodes.
- Builds on: RFC 0001 (instances are storage resolved at compile time), RFC 0004 (arena),
  RFC 0006 (`self` is `this`), RFC 0009 (the tag of an optional), RFC 0015 (a buffer carries its
  length), and the memory decisions of 2026-09-15 and 2026-09-29.
- Demand: `adafruit_ds18x20`, unmodified, on the Arduino Uno and on the Feather RP2040, through
  `adafruit_onewire/bus.py`.

## 1. What was measured

`ds18x20_simpletest.py` stops today at `bus.py:168` ("'l_rom' is rebound under a run-time
condition"). With that wall removed by a stub, the next one is `bus.py:147`,
`devices.append(OneWireAddress(rom))` ("'.append()' cannot take an instance of
'OneWireAddress'"). Two independent walls with one origin: a buffer travels as a name with no
run-time presence, and an instance is storage flattened at compile time.

An `Optional[bytearray]` parameter compiles today when every call decides it at compile time
(`f(x)` or `f(None)`), including the rebind `if not l: l = bytearray(8)`. It fails only when the
argument changes between turns of a loop, which is `scan()`: `rom` is `None` the first time and a
buffer afterwards.

There is also a semantic hazard every design has to resolve. `_search_rom` creates
`bytearray(8)` and returns it; in PyMCU that local is one static storage. With
`rom, diff = _search_rom(rom, diff)`, the `l_rom` the callee receives is the same storage as the
new `rom` the callee zeroes before it reads `l_rom[byte]`. In CPython they are two objects.
Untreated, the result would be silently wrong, which is why the current refusal is correct.

## 2. Part A: `Optional[buffer]` with run-time presence

Representation, per position, as in RFC 0009 (the cost is paid only when `None` is a run-time
fact; when it is decided at compile time the code is byte-identical).

a) A buffer that travels to a subroutine or comes back from one already travels by pointer.
   `None` is the null pointer (address 0 is never a buffer: on AVR it is register R0, on ARM
   and RISC-V it is boot ROM). No new storage and no tag; `is None` is a comparison with zero.
b) A local name rebound under a run-time condition (the `l_rom` rebind) becomes a pointer
   variable (2 B on AVR, 4 B on ARM and RISC-V) and its accesses become indirect. Only the
   names that take part degrade; everything else is unchanged.
c) A field or local `Optional[bytearray(N)]` that holds its own buffer has one tag byte plus N
   fixed bytes (like the union field of RFC 0009).

The size N is part of the type. Two branches that join buffers of different sizes into one
name are refused. There is no heap in any of this part.

Aliasing. A buffer parameter that may point at the storage of a local that the function itself
reinitialises and returns (the `l_rom` case) is copied into an N-byte temporary on entry. It
costs N bytes and a copy loop, and is emitted only when the analysis cannot prove there is no
alias. The ds18x20 case pays 8 B and about 20 instructions.

Semantics against CPython. `if not l_rom` is true only for `None` (N is fixed and non-zero).
`is None` / `is not None` narrow as in RFC 0009. Reading `l_rom[i]` without narrowing stays a
compile error, not a run-time failure.

Refusals, with their sentence at the user's line:
- different sizes: "'x' can be a bytearray(8) or a bytearray(16) here; a buffer has one size";
- an optional buffer as a list element or as a union payload with a scalar: "an optional buffer
  cannot be a list element or a union payload on this target; hold it in a field of its own";
- mutation through a name that may be `None` and is not narrowed: "'l_rom' may be None here;
  narrow it first".

## 3. Part B: a growable list of instances that hold a buffer

Today it is refused because an instance is fields at their own positions and a list has nowhere
to copy it; it would repeat one element at every index. Proposal: a "data" class can be stored
in a heap list as a record.

Eligible class. Scalar fields or fixed-size buffers (or eligible records), no properties other
than plain getters, no inheritance with virtual methods and no references to other live
instances of the program. `OneWireAddress` (one `rom` field of 8 B) is eligible.

Representation. `list[C]` is the `list[list[T]]` that already exists: a list with the reference
flag in its header whose elements are pointers to heap objects. Each element is a record, one
more object of size C. The compacting collector with a shadow stack on ARM and RISC-V already
traces and relocates those pointers. There is no destructor and no `__del__`.

Copy on append. `append(C(...))` copies the fields into the record. This is mandatory, not an
optimisation: the `rom` that `_search_rom` returns is shared static storage, and without the copy
all ten addresses would be the last one. The difference with CPython (which shares the
reference) is visible only if the original is mutated after being appended; the compiler refuses
that mutation in the same scope with "this instance was copied into the list when it was
appended; changing it afterwards would not change the list".

`devices[i].field` loads through the pointer. The methods of C expand with `self` bound to the
record (the forced inline of decision 4 of 2026-09-15), so this does not depend on RFC 0006.

ARM and RISC-V. It fits with no new GC work. The ceiling is the header's: 65535 B per object.

AVR. It fits reasonably only for small records and few entries. The header is 2 B, the ceiling is
255 B per object, the list keeps its count in one byte, the free heap is about 1.7 KB and the
shadow stack has 64 slots. `OneWireAddress` takes 10 B of record (2 header and 8 of `rom`); ten
devices, the default `maximum_devices`, are about 24 B of list and 100 B of records. It fits. A
record over 253 B, or a list whose sum exceeds the heap, does not fit, and that is physical: the
compiler refuses the first ("the record of 'C' is 300 bytes; the heap holds at most 255 per
object") and the second raises a clean `MemoryError` at run time, never corruption.

## 4. Impact on the memory model

Part A does not change lifetimes: buffers stay static, the pointer is automatic and initialised
on entry. It respects the zero-init of statics and the as-if rule (a pointer is never removed if
anyone can read the buffer it points to). It follows the direction of the 2026-09-29 decision
(C++ rules: a null pointer is a cheap `std::optional`).

Part B adds dynamic duration only inside heap lists, which are already opt-in and already linked
only when used. It does not touch the static duration of module instances or their fields. It
applies decision 6 of 2026-09-15 (do not refuse without offering an alternative): the alternative
for AVR is a fixed array of records declared with a size, which already exists when the class has
two or more fields.

## 5. Commit plan

Part A, in this order, each with a unit test and oracle probes:
1. GAS prototype and measurement (phase 0, no compiler code).
2. The `Optional[buffer(N)]` type and narrowing by `is None` on parameters.
3. Returning `Optional[buffer]` from subroutines and inline expansion with run-time presence.
4. A name rebound under a run-time condition as a pointer variable (the rebind).
5. Copy on entry when there may be an alias with a reinitialised local.
6. Refusal messages, documentation, and removal of the old refusals.

Part B:
1. Eligibility of a class and the design of the record; refusal messages.
2. `append(C(...))` that copies the record.
3. Reading `devices[i].field`, `len`, `for` over the list, methods expanded with `self` bound.
4. The same on AVR with the limits and `MemoryError`.
5. RISC-V when its back end allows it.
6. The rule against mutation after the append and its message.

## 6. How it is tested

Oracle probes, the three semantics first and red against CPython: a buffer that is `None` on the
first turn and `bytearray(8)` afterwards; the rebind under a run-time condition; the alias of the
reinitialised return (the case that would give wrong data in silence); and for B a list of
records filled in a loop and read by index and by `for`, with a fresh buffer per iteration. Flash
and SRAM comparison on the 55 programs and the ROM snapshots, which may not move.

Real ds18x20. A simulated 1-Wire slave is needed in the emulator (reset with presence pulse, ROM
search, scratchpad read with CRC). The exit criterion is that Adafruit's `ds18x20_simpletest.py`,
untouched, returns the temperature the slave holds, on AVR and on the Feather RP2040.

## 7. Who else benefits

Of the 18 Adafruit libraries on disk, an optional buffer with run-time presence is asked for only
by `adafruit_onewire`; `adafruit_pixelbuf` declares it but resolves it at compile time with
constants, and `adafruit_ssd1306` names it in a type comment. A list of instances appears only in
`adafruit_onewire` (the `append`s in dht and pixelbuf are of scalars). The measured benefit today
is one library. The patterns are common outside the 18 (BLE, irremote, device buses), but that is
an assumption to measure before counting it.

## 8. Risks and open questions

The aliasing of reinitialised returns is the delicate point of A: the conservative analysis
copies too much and costs bytes. The copy on append of B is a visible difference with CPython,
documented and refused when it can be detected. Question put to the maintainer: is it accepted
that Part B stays on ARM and RISC-V only if the AVR prototype does not fit the measure? Answer:
Part B is also done on AVR (approved 2026-10-12).

## 9. Implementation notes and where it differs from the text above

Reconciliation with RFC 0015 and the `feat/ds18x20-buffers` branch. RFC 0015 gives every buffer a
length that travels with it. Part A reuses that work instead of adding a second mechanism:
`feat/ds18x20-buffers` already carries an `Optional[bytearray]` parameter or local as the buffer
pointer plus a sibling presence byte, and the hidden run-time length parameter of RFC 0015. This
is a deliberate difference from section 2: presence is a byte next to the pointer, not the null
pointer, because the pointer of a buffer in RAM can legitimately be any address and the byte is
what the existing `optionalTagSlots` machinery already tests. The observable rules (narrowing,
`if not l`, the refusals) are the ones in section 2.

Part B as implemented. The class is eligible when it has exactly one field and the constructor
argument is a buffer whose size is known where the append is lowered (a named `bytearray`, or a
literal `bytearray(N)`). The record is a bare heap object of N bytes (no list header), allocated
with `GcAlloc`, filled byte by byte, and the list element is its pointer, kept in the list as a
`GC_REF`. A failed allocation raises `MemoryError`. The unrolled copy is limited to 32 bytes; a
larger record is refused with a message that says so. A handle read back from the list (`xs[i]`,
`for d in xs`, `f()[k]`) is the base pointer of an N-byte buffer, so `h.rom[k]`, `len(h.rom)` and
`r = h.rom` read through the pointer. Classes with more than one field, and the rule against
mutating the original after the append (section 3), are not yet implemented.

The general record and the rule against mutating after the append have landed, in that order
after the single-field case. A class of two or more scalar fields, each set from a constructor
parameter, is a heap object laid out like the class's SRAM slot (the same offsets Model B uses,
so an @outline method takes the pointer as `self` exactly as it takes a slot's base); reads,
writes through the handle and iteration load and store through the pointer, and a method that is
not @outline is refused by name. Buffers, unions, nested instances and fields the constructor
does not set inside such a class are still refused. A buffer copied by `append(C(buf))` is
refused a later `buf[i] = v` in the same function, unless a new buffer is bound to the name
first; a store written before the append (the scan loop's shape) is unaffected. The record
ceiling is 253 bytes on AVR, reported with the record's size, and 65527 on ARM.

Four faults found on the way are independent of this RFC and are fixed in their own commits:
a property read on an instance held in a field (`self._io.value`) used to take the register
shortcut; the list a call returns, indexed in place, read a slot nothing wrote; a bytes bound at
the top level of an imported module read as an unwritten slot from another frame; and a module
that binds a sequence lost its name to another module's int of the same spelling.
