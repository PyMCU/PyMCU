# RFC 0005: a non-literal raise message compiles as a deferred print

- Status: **IMPLEMENTED**. Where the landed code differs from this note's first draft, the
  section text below says so: the slot names shipped as `__exn_argK` (always `int32`) and
  `__exn_fargK`, the site id lives in `__exn_site`, the dispatcher is
  `__pymcu_print_exn_msg` and reads `__exn_site` itself rather than taking it as a
  parameter, and a `bool` piece is supported (`EmitStreamBool`) instead of refused.
- Issue: [#435](https://github.com/PyMCU/PyMCU/issues/435)
- Date: 2026-09-15
- Affects: `src/compiler/Frontend/Parser.cs`, `Ast.cs`, `AstJsonWriter.cs`,
  `PyParser/pymcu_translate.py`, `PythonAstReader.cs`,
  `src/compiler/IR/IRGenerator/ControlFlow.cs`, `Call.cs`, `Core.cs`, `State.cs`. No changes
  to `extensions/pymcu-sdk` or to the AVR backend in `pymcu-avr` -- see "Why no new IR"
  below.

## 1. Problem

`raise X(msg)` accepts a string literal or a name bound to one; anything else in the message
position is either silently discarded (a non-call expression, #262) or refused outright (a
call, because discarding it would drop an evaluation CPython performs when the raise fires).
An f-string is neither of those exceptions -- it is refused today with:

```
raise ValueError(f"bad address {addr}"): 'code' is not a string constant known at compile
time. The message must be one or more string literals, or the name of a module-level
constant declared as `code: str = "..."`
```

Two entries in `docs/language/limitations.md`'s survey table stop here and report nothing
else:

| Library | Line | Shape |
|---|---|---|
| `adafruit_seesaw` | 852 | an f-string in a raise message |
| `adafruit_ht16k33` (segments) | 846/893 | a call inside a raise message |

The refusal is honest about the model as it stands -- the message never reaches the firmware,
so an f-string's interpolations would be evaluated and dropped, exactly the CALL argument
already refused for the same reason -- but the model itself has everything it needs to keep
the message instead of discarding it: `print()` already lowers an f-string with runtime
interpolations to direct writes with no heap (`EmitStreamFString`), and RFC 0003 Part 2
already gives a raised exception exactly one live message slot. What is missing is a place
to put a message that is not known until the raise actually fires, and a way for the handler
that reads it to find that value again.

## 2. Model

**One exception is live at a time.** That is not a new restriction, it is what RFC 0003 Part
2 already decided: the exception object is two static facts, not an allocation. This RFC
adds a third: which raise SITE produced the current message, when the message needed one.

**A raise message becomes a PIECE LIST**, the same shape an f-string already lowers to
(`FStringExpr.Parts`, literal text and expression parts). A message that is not already an
f-string is normalized into a one-part piece list:

| Message form | Piece list |
|---|---|
| `f"bad {addr}"` | `["bad ", addr]` |
| `"a" + str(n)` (or any `+`-chain) | Normalized by `RewriteStringBuilding`, which turns a concatenation chain into the `FStringExpr` `print()` already lowers |
| `get_error_text(code)` (a bare call) | `[get_error_text(code)]` -- one piece, no literal fragments |
| `str(x)` as the whole message | Unwrapped to `[x]` -- the printer formats the value itself |

A nested f-string piece with no format spec is flattened into the parent's list. A piece
that constant-folds to text (`TryFoldRaiseMessageToString`) is stored as a literal fragment
instead of a runtime value, so `f"bad {2 + 2}"` costs no slot at all.

**Literal fragments intern into flash** exactly as a `print()` literal does
(`InternStringAsFlash`). **Runtime pieces go into shared, position-indexed slots**, not a
byte-addressed buffer: the first int-like piece of ANY raise message in the program (in
source order within that raise) is `__exn_arg0`, the second is `__exn_arg1`, and so on;
float pieces get their own independent counter, `__exn_farg0`, `__exn_farg1`, .... Int-like
slots are declared `int32` unconditionally -- every piece is widened to `int32` through an
ordinary `Copy` at the raise, the same promotion the compiler already performs for a plain
variable assignment, so printing the wider slot's value produces the same digits. (The first
draft sized each position at the widest type stored there; always-`int32` is simpler and
costs at most three bytes per position.) One exception is live at a time, so two sites never
need their position-0 value to coexist.

**A per-program raise-site id is stored alongside the type code.** `__exn_site: uint8` is 0
for "the message is `__exn_msg`, read the flash address there, exactly like today" and N (a
stable, sequential id assigned in source order to each raise whose message is non-literal)
for "run print sequence N against the current slot values." A literal-message raise in a
program that also has dynamic sites sets `__exn_site = 0` (in addition to `__exn_msg`,
unchanged); a non-literal one sets the slots then `__exn_site = N` (and never touches
`__exn_msg`); a bare re-raise touches neither, exactly as it leaves `__exn_msg` alone today.

**`print(e)`, `str(e)` and `e.args[0]`** dispatch on `__exn_site`: 0 keeps today's call to
the runtime flash-string writer; nonzero enters the synthesized dispatcher function,
`__pymcu_print_exn_msg`, which reads `__exn_site` itself and runs the recorded sequence.
(The draft passed the site id as a parameter; the landed version reads the global directly,
which keeps the call sites argument-free.)

### Why no new IR

The dispatcher needs, per site, a straight-line sequence of "write this flash fragment" /
"format-print this slot's current value" calls, chosen by an id known only at run time. That
shape -- a runtime tag choosing among fixed, known-in-advance code paths -- appears once
already in this compiler (`__pymcu_unhandled_exn`'s per-type name dispatch), and it is
implemented as **hand-written AVR assembly in the backend**, because there the set of
choices is a flat list of `(code, flash string)` pairs. A print SEQUENCE is not flat: it
interleaves flash-fragment writes and value-formatting calls with the interleaving itself
encoding the message, one of several formatter subroutines per piece depending on its type,
and it has to be word-for-word what `print()` already produces for the identical piece so
the two never drift. Reaching for hand-assembly here would mean re-deriving the AVR calling
convention for each formatter call by hand, in a second place, for a feature whose whole
draw is that it reuses the first place exactly.

Instead, `__pymcu_print_exn_msg` is synthesized as an ORDINARY function at the Tacky IR
level -- appended to `ProgramIR.Functions` after the user's AST is fully processed
(`SynthesizeExceptionMessagePrinter`), the same way `outlinedMethods` synthesizes a shared
subroutine body for a ZCA method today. Its body is built by calling the SAME private
emitters `print()`'s f-string lowering already calls -- `EmitStreamStr`, `EmitStreamVal`,
`EmitStreamBool`, `ResolveWriteStrFn`, `ResolveFloatWriteFn`, `ResolveDecimalWriteFn` --
with `currentFunction` pointed at the synthesized function while its body is built. Every
one of those emitters already lowers to ordinary `Call`, `Copy` and comparison/jump
instructions that the backend compiles today for any other function. The `if site == 1:
... elif site == 2: ...` dispatch is the same IR an ordinary `if`/`elif` chain in a user's
function already produces. **No new Tacky record, no AVR backend change.** This is also why
the fixtures in Section 7 gate on firmware from the ordinary pipeline, not on a new opcode.

## 3. SRAM cost formula

| Item | Cost | When |
|---|---|---|
| `__exn_site` | 1 byte | Only when the program has >= 1 raise with a non-literal message AND some handler binds a name (`programBindsExceptionObject`) |
| `__exn_argK` | 4 bytes (always `int32`) | One per int-like piece position, per program, sized by the union of every qualifying site's piece list |
| `__exn_fargK` | 4 bytes | One per float piece position, per program |
| `__pymcu_print_exn_msg` | one call + one comparison-chain entry per qualifying site, each site's body equivalent to writing that many `print()`-style calls by hand | Only emitted when >= 1 qualifying site exists |
| A literal-message raise | +1 store (`__exn_site = 0`) | Only in a program that ALSO has >= 1 qualifying non-literal site -- otherwise unchanged, matching #369's existing gate |
| Everything above | **zero** | No handler anywhere binds a name (today's gate, unchanged and untouched) |

A program with a single f-string raise message carrying one `uint8` and one `float`, read
once, pays: 1 byte (`__exn_site`) + 4 bytes (`__exn_arg0`) + 4 bytes (`__exn_farg0`) = 9
bytes of SRAM, plus the dispatcher's code (one comparison, one flash-fragment write, one
decimal call, one flash-fragment write, one float-write call, one flash-fragment write) --
the same six calls the equivalent `print(f"...")` would make by hand.

## 4. Refusals

Located at the piece:

> the piece at position `<n>` of this raise message is an instance of X, which a raise
> message does not support. Print it as its own value with `print(...)`, or narrow it to an
> integer or a float first.

Scope of this first cut, stated as a boundary rather than left implicit:

- **Supported piece types:** `uint8`/`int8`/`uint16`/`int16`/`uint32`/`int32`, `float`,
  `bool` (prints `True`/`False` through `EmitStreamBool`, the same formatter `print()`
  uses -- the draft had refused it; the shared recognizer made support free).
- **Not yet supported, refused by name:** an instance interpolation. That is exactly the
  shape `RejectInstanceInterpolation` already special-cases for `print()`; a raise message
  piece reuses the same recognizer to decide refuse-vs-accept, so the boundary is enforced
  by the same code that already draws it for `print()`, not a second copy of the rule.
- A **format spec** on an f-string piece (`f"{x:02x}"`) is supported the same way `print()`
  supports one: it routes through the existing formatter (`ParseFormatSpec`), so the raise
  message and the equivalent `print()` produce identical text for identical specs.
- A call as the WHOLE message, or as one piece of an f-string message, is fine as long as
  its return type is one of the supported scalar types -- there is nothing new to refuse
  there beyond what `print()` already refuses for the same call.

## 5. Surfaces changed

- **Front ends.** `RaiseStmt` (`Ast.cs`) carries an optional `MessageExpr`, populated when
  the message is an f-string, a `+`-concatenation, or any other expression that is not a
  bare string literal or a bare identifier -- the two forms that already had a
  representation (`Message`, `MessageName`). `Parser.cs` stopped discarding a non-call
  expression and stopped refusing a call in this position; `pymcu_translate.py` and
  `PythonAstReader.cs` moved together (`AstJsonWriter.cs` writes the same field), since a
  discarded/refused shape on one side and an accepted one on the other is the exact class
  of bug #277 already found once (see the `raise ... from ...` history).
- **IR generation** (`ControlFlow.cs`, `VisitRaise`). Where `resolvedMessage` was computed,
  a non-literal message is instead normalized into a piece list (Section 2). The
  `programHasDynamicRaiseMessage` pre-pass -- gated on `programBindsExceptionObject`, so a
  program that never binds `e` emits none of this -- decides before the first function is
  lowered, so `print(e)` in a handler compiled before the raise still calls the printer.
- **Slot allocation.** `EmitDynamicRaiseMessage` declares `__exn_argK` / `__exn_fargK` in
  `mutableGlobals` alongside `ExceptionMessageVar`, each at its fixed width (`int32`,
  `float`), and appends the site's piece list to `raiseMessageSites`.
- **The dispatcher.** `SynthesizeExceptionMessagePrinter` is appended to the program's
  function list after IR generation completes (Section 2's "Why no new IR"), present only
  when at least one qualifying site exists.
- **The read side** (`Call.cs`, the `print(e)` / `str(e)` / `e.args[0]` path). When the
  program contains any qualifying site, these reads emit a call to
  `__pymcu_print_exn_msg`, which dispatches on `__exn_site`; when it does not (today's
  programs, and any program using only literal messages), they stay exactly as they were,
  byte for byte.

## 6. Sequencing / relationship to RFC 0003

Builds directly on RFC 0003 Part 2 (`except X as e`, #369): reuses
`programBindsExceptionObject`, `exceptionBindings`, `TryGetExceptionBinding`, the message
read path, `DeclareExceptionMessageVar`, and the flash-string interning `print()` already
performs. Independent of Part 1 (`**kwargs`/`*args`) and of Part 2(c) (a field on a
user-defined exception class, still unimplemented) -- neither is touched.

## 7. Gates, as shipped

- Programs that never bind an exception name emit none of this machinery
  (`programHasDynamicRaiseMessage` stays false) -- the zero-cost row of Section 3, pinned
  by the `WithoutABoundHandlerTheRecordIsNotEmitted` unit test.
- `tests/integration/fixtures/raise-message-deferred-print` (in `pymcu-avr`): an f-string
  message with a `uint8` piece, a `len(...)` call piece, and a `uint8`+`float` pair,
  raised inside functions and read via `print(e)` in the handlers, prints exactly what
  CPython prints for the same program -- on both front ends.
- Oracle probes `181..183_raise_message_*.py` (the corpus lives at `tests/oracle/probes/` in `pymcu-avr`) cover the
  f-string-with-int-and-float, float-only, and int-only shapes against CPython under both
  front ends.
- `tests/unit/IR/RaiseMessageDeferredPrintTests.cs` pins the IR shape: site id and slots
  stored at the raise, the literal fast path for a folded message, the call-piece
  acceptance, and the `CompileError` refusal needing a compile-time string.
