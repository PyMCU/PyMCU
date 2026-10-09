# RFC 0014: by type, not by name -- resolve behaviour from the binding, not the spelling

- Status: **PROPOSED (document only; no compiler change in this branch)**. Two decisions
  this epic encodes have already partially landed on main and are restated here so the
  source comments citing "RFC 0014" resolve to this document: decision 4 (a user binding
  shadows a builtin name) is live for `open`/`hex`/`bin`/`oct`/`round`, and decision 5
  (the driver never scans source text for a semantic question) is live for the arena,
  `pymcu.strfmt` and `pymcu.round2` injections.
- Date: 2026-10-07
- Affects: `src/compiler/IR/IRGenerator/Expr.cs`, `Assign.cs`, `Call.cs`, `Core.cs`,
  `ControlFlow.cs`, `Scan.cs`, `BoundOutline.cs`, `src/compiler/IR/NameResolution.cs`,
  `src/compiler/Frontend/Parser.cs`, `src/compiler/Frontend/CompileTimeEvaluator.cs`,
  `src/compiler/Frontend/AsyncTransform.cs`, `src/compiler/Common/Logger.cs`,
  `src/driver/commands/build.py`, `src/driver/core/compiler.py`,
  `src/driver/core/libraries.py`, `lib/src/pymcu/types.py`, `lib/src/pymcu/asyncio.py`.
- Builds on: RFC 0001 (ZCA storage and the single-field collapse), RFC 0007 (the
  compile-time introspection table), RFC 0008 (`open()` and embedded files), RFC 0009
  (tagged-union member reads), RFC 0011 (the coroutine/generator state-machine transform),
  RFC 0012 (the `ptr[T]` register surface this epic rewrites underneath).
- Measurements: every "today" claim below was compiled with the shared main compiler
  (`pymcuc` built from `a3015d8a`, arm64 UUID `B7BEECE7-2305-3365-9722-F818C3804819`)
  and run on avr8sharp, both front ends (default C# and `PYMCU_PY_PARSER=1`); line
  numbers cite `origin/main` `b03e933f`, where the cited code is textually identical.
  CPython output is the same probe file run under CPython 3.14 with the oracle shims
  (`tests/oracle/test_oracle.py:install_cpython_shims`, `GPIOR0.value = 0`).

## 0. Decisions this RFC encodes

1. **A member access dispatches on the receiver's binding, never on the member's
   spelling alone.** `obj.value` means what the resolved type of `obj` says `value`
   means: a `ptr[T]` reads the register because the `ptr` class declares a `value`
   property the compiler lowers; every other receiver takes the ordinary member path
   (field, class attribute, property, descriptor, method) with no register fallback.
   The same rule applies to `[bit]` (`__getitem__`/`__setitem__`), to `x.uname()`,
   and to `obj.sleep_ms(...)`.
2. **A compiler intrinsic is a declaration, not a name.** The intrinsics live as real
   declarations in `pymcu.types` (or a module it re-exports). A call lowers to an
   intrinsic because the callee's *binding* resolves to that declaration; a bare-name
   match in a global string set is never how an intrinsic is found. Importing
   `pymcu.types` binds names into the importing module's scope, nothing more.
3. **An `await`/`asyncio` lowering matches the bound module, and so does every delay
   rewrite.** `await aio.sleep(n)` lowers as a coroutine wait because `aio` resolves
   to the asyncio module *and* the callee resolves to its `sleep` declaration; a member
   spelled `sleep` on anything else is an ordinary method call.
4. **A module-level user binding shadows a builtin of the same spelling, as in
   CPython.** The builtin dispatch runs only for calls whose name still resolves to the
   builtin. Landed for `open`, `hex`, `bin`, `oct`, `round` (`Call.cs:1028`,
   `IsBuiltinShadowed` at `Call.cs:7896`); this RFC extends the same rule to every
   builtin and intrinsic the ladder dispatches on.
5. **The driver decides nothing from source text.** Whether a program needs a helper,
   a reservation or an injection is answered by the compiler, which reports it on the
   build's stdout token stream (`[NEEDS_*]`, `[ARENA_USED]`), or by an `ast` walk over
   real syntax nodes, never by a regex over comment-and-string-stripped text. Landed
   for arena/strfmt/round2 and the print()/UART()/ticks detectors; this RFC retires
   the remaining scans.
6. **Compiler-internal names carry no user-visible meaning.** `tmp_`, `self_`,
   `inline`, `_pymcu_` and `__lam` prefixes are storage-key conventions the compiler
   mints for itself. They stop being load-bearing the moment a user name can fall into
   the same bucket: synthesized bindings get flags in the binding record, not prefixes
   a hand-written name can collide with.
7. **No ambient names.** `F_CPU`, `__FREQ__`, `__FREQUENCY__`, `__CHIP__`,
   `__TIMEBASE__`, `sys`, `os`, `uos`, `usys`, `uname` bind only through an explicit
   import or a declared marker. `__name__` is the exception: it is a real Python
   dunder every module has, and stays.

## 1. The rule

The maintainer's question, verbatim: *"el binding de value a algo interno se siente
raro... por que no hacerlo una property o algo similar en Python, de modo que podamos
definir su comportamiento, como C define macros o funciones que manejara el
compilador"*.

That is the whole design in one sentence. A `ptr[T]` is already a Python class
declaration in `lib/src/pymcu/types.py:14`, with a `value` property and a
`__getitem__`/`__setitem__` pair that raise `RuntimeError` under CPython. The compiler
does not dispatch through those declarations today: it pattern-matches the *spelling*
`.value` on any receiver, then layers guard after guard (`IsKnownInstanceField`,
`IsPropertyGetterRead`, `IsDescriptorMemberRead`, `IsDescriptorMemberWrite`,
`IsNonDataDescriptorMember`, `IsCollapsedFieldAnchor`, `ReadsARegister`) so that user
code spelled the same way still works. Every guard is a bug somebody hit. The audit
found the same pattern in six more places; this RFC treats all seven as one epic.

A name can decide only one thing in Python: which binding it resolves to. Everything
after that is a property of the binding, not the spelling. `pymcu` already knows how
to say "this declaration is handled by the compiler": `ptr`, `const`, `asm`,
`compile_isr`, `claim`, `funcref` are declared in `types.py` and intercepted by the
IR generator. This RFC extends that mechanism to member access and tightens it: the
intercept happens when the call's binding *is* the declaration, not when the source
text happens to match.

## 2. What the name-dispatched paths do today, measured

Each probe below is a standalone `main.py`, compiled for `atmega328p` at 16 MHz by
`pymcu build` and run in avr8sharp. CPython column = same file executed under CPython
with the oracle's pymcu shims. Both front ends produced identical output on every
probe, so a single "PyMCU" column is shown.

| # | Program (shape) | CPython | PyMCU (cs and py) | Verdict |
|---|---|---|---|---|
| p4 | `class Port: @property def value: return self._v*2` then `print(Port().value)` | `10` | `0` | **silent wrong value** |
| p19 | `class Port: value = 66` then `print(p.value, Port.value)` | `66 66` | `255 66` | **silent wrong value** (the read through the instance takes the register path; the read through the class does not) |
| p5 | `def len(x): return 42` then `print(len("ignored"))` | `42` | `7` | **user def silently ignored** |
| p6 | `def asm(code): print(...)` then `asm("nop")` | `user asm: nop` | *(nothing; a `nop` lands in the .asm)* | **user def silently replaced by the intrinsic** |
| p7 | `from pymcu.chips.atmega328p import GPIOR0` + `def ptr(x): return x+1` then `print(ptr(5))` | `6` | `255` | **user def silently ignored** (transitively armed intrinsic) |
| p15 | `def hex(x): return "mine"` then `print(hex(255))` | `mine` | `6` | **wrong value** (the def runs -- `in-user-hex` prints in p36 -- but its `str` return reaches the caller corrupted: `6` here, the argument `255` echoed in p36) |
| p30 | `def str(x): return "mine"` then `print(str(5))` | `mine` | `5` | **user def silently ignored** |
| p12 | `def print(x): pass` then `print(1)` | *(prints nothing)* | `CompileError: call to undefined function 'uart_write_str'` | **driver honours the shadow, the compiler does not** |
| p25 | `sys = Fake()` (class member `platform = "user"`) then `print(sys.platform)` | `user` | `257` | **silent wrong value** (introspection answered by spelling) |
| p32 | `os = Fake()` with `def uname()` then `print(os.uname())` | `user-uname` | `257` | **silent wrong value** (same path) |
| p8 | `print(F_CPU); print(__FREQ__)` with no import | `NameError` | `16000000 16000000` | ambient binding by spelling |
| p16 | `class Wifi: def UART(self): ...` then `w.UART()` + `print("hi")` | `hi` | `hi`, but build log reads `print() + user UART() -- injecting console functions (no init)` | reservation decision taken off the attribute spelling |
| p17 | `class Motor: def monotonic(self): ...` then `m.monotonic()` | `1 END` | `1 END`, but `ticks_ms() detected -- injecting millis_init() preamble (Timer0 OVF @ prescaler 64)`; firmware.hex 1605 B vs 1114 B for the same program without the call | **+491 B and a live Timer0 ISR for a program that never uses the time base** |
| p27 | `hasattr(GPIOR0, "value")` on a real `ptr[uint8]` | `True` | `False` | `.value` is not a member of the type, it is a spelling the compiler intercepts |
| p9 | `print(sys.platform)` with no import | `NameError` | `CompileError: name 'sys' is not defined` | correct refusal |
| p31 | `sys = 5; print(sys.platform)` | `AttributeError` | `CompileError: object has no attribute 'platform'` | correct refusal |
| p29 | `import sys` on the native flavor | `darwin` | `ImportError` naming the compat layers | honest refusal |

Green today, and the migration must keep them green (these are the regression probes):

`p1` single-field class with a field `value` (`41`), `p2` two-field class (`5 9`),
`p3` descriptor named `value` with `__get__`/`__set__` (`77`), `p13` descriptor write
then read (`73`), `p10` user method `m.sleep_ms(5)` (`slept 5 / 5`), `p11` user method
`value()` (`9`), `p14` produced-instance field `make().value` (`33`), `p18`
`ptr[uint8]` baseline `.value`/`[bit]` (`1 1`), `p20` property `p.value` on a *named*
instance (`10`), `p21` `def hex` returning an int (`123`), `p22` `p.value = 40` write
through a class attribute (`40 66`), `p23` field `c.value` read through a parameter
(`44`), `p24` property setter `p.value = 4` (`8`), `p26` user `__getitem__` (`6 8`),
`p28` user `sys` object with an *int* `platform` member (`41`).

Notes on the two most instructive failures:

- **p4 vs p20.** The same property read answers 0 or 10 depending on whether the
  receiver is a produced expression (`Port().value`) or a named variable
  (`p.value`). The property guard at `Expr.cs:6752` keys on the receiver's variable
  name; a receiver that has no name falls through to the register path, finds no
  address, and returns 0. p19 shows the same hole from the other side: a *class
  attribute* spelled `value`, read through an instance, takes the register path and
  prints 255.
- **p7.** No file in the program spells `from pymcu.types import ptr`. The `GPIOR0`
  import pulls in `pymcu.chips.atmega328p`, whose own `from pymcu.types import ...`
  lands in the loop at `Core.cs:752` and arms `ptr` program-wide; the user's own
  `def ptr` at module level is then unreachable, because `ResolveCallee` at
  `Core.cs:3662` answers the intrinsic before it ever looks at `functionParams`
  (`Core.cs:3733`).

## 3. Inventory

Every site below exists on `origin/main` `b03e933f`. "Name wins" means the check runs
on the spelling with no receiver-type or binding test; "guarded" means a name check
wrapped in the exclusions previous bugs forced in.

### Family 1: `.value` and `[bit]` on `ptr[T]`

Read side, `src/compiler/IR/IRGenerator/Expr.cs`:

| Lines | Site | What it does |
|---|---|---|
| 532-558 | comment block above `IsKnownInstanceField` | documents the `.value` collision with known instance fields and flattened storage |
| 560-583 | `IsKnownInstanceField` | a member named `value` is a real field when the receiver's class lays one out |
| 585-606 | `IsCollapsedFieldAnchor` | single-field collapse: `x.value` is the anchor variable itself (RFC 0001 Model B) |
| 5940-5972 | `IsDescriptorMemberRead`/`IsDescriptorMemberWrite`/`IsNonDataDescriptorMember` | descriptor spelled `value` wins over the register path |
| 6057 | `IsPropertyGetterRead` | property spelled `value` wins over the register path (the guard p4 falls outside) |
| 6752 | `expr.Member == "value" && propertyGetters.Count > 0 && IsPropertyGetterRead(expr)` | property getter beats `.value` |
| 6762 | `expr.Member == "value" && IsDescriptorMemberRead(expr)` | descriptor read beats `.value` (#419) |
| 6775-6824 | `expr.Member == "value" && !IsKnownInstanceField(...)` then the register/pointer path | constant-address load, runtime-pointer `LoadIndirect`, `__io`/`ptr` handling |
| 3232 | `VisitIndex` | `[i]` dispatch tail |
| 3467, 3485, 3918 | property/descriptor guards inside index and member lists | same name-based exclusions applied to subscripts of members |
| 4379-4397 | class-instance guard in `VisitIndex` | an instance without `__getitem__` is refused before the bit path (#171) |
| 4450-4465 | runtime-pointer bit read | `p[i]` through `RuntimePtrTargetElem` emits `LoadIndirect`+`BitCheck`/`RShift` |
| 4468-4491 | constant bit read | `BitCheck` on the storage, constant index required |

Write side, `src/compiler/IR/IRGenerator/Assign.cs`:

| Lines | Site | What it does |
|---|---|---|
| 3073-3090 | `memExpr2.Member == "value" && IsDescriptorMemberWrite(...)` | descriptor `__set__` beats the MMIO write |
| 3092-3114 | `IsNonDataDescriptorMember` | non-data descriptor spelled `value` refuses the MMIO write with a reason (#419) |
| 3112-3121 | `!IsKnownInstanceField(..., "value")` guard | field spelled `value` kept out of the register path |
| 3121 onward | the `.value` write path | register/pointer store, collapsed-anchor handling |
| 3562-3601 | snapshot vs pointer-alias on `self.x = REG.value` | `MemberAccessExpr { Member: "value" }` on the RHS decides copy vs alias |
| 5300-5311 | runtime-pointer bit store dispatch | `p[i] = v` where `p` is a runtime `ptr` |
| 5314-5379 | constant vs runtime bit index | `bit` resolved through `constantVariables` |
| 5382-5392 | `BitSet`/`BitClear`/`BitWrite` emission | SBI/CBI or masked store |
| 5416 | `EmitRuntimePtrBitStore` | pointee bit store through `LoadIndirect`/`StoreIndirect` |
| 9980 | `ReadsARegister(augIe.Index)` | augmented store pins a register-reading index once |
| 10228-10256 | augmented `p[i] OP=` on a runtime pointer | read-modify-write through the pointee |
| 10258-10315 | augmented `.value OP=` | `mae.Member == "value"` -> register RMW |
| 10327-10342 | generic member augmented assignment | the fallback `MemberAccessExpr` arm |

Consumers of the name, `src/compiler/IR/IRGenerator/Call.cs`, `Assign.cs` and
`ControlFlow.cs`:

| Lines | Site | What it does |
|---|---|---|
| `Call.cs:3796`, `Assign.cs:4848-4850`, `Assign.cs:9980` | `ReadsARegister` call sites | pin/register reads marked effectful |
| `Call.cs:6842` | `len(...)` on a member | `!IsPropertyGetterRead && !IsDescriptorMemberRead` exclusion |
| `Call.cs:10876-10885` | `ReadsARegister` | `MemberAccessExpr { Member: "value" }` == volatile register read unless field/property |
| `Call.cs:13040-13104` | `hasattr(x, "value")` | member-name special case (answers `False` for a real `ptr`, see p27) |
| `Call.cs:4605` | property-getter read inside a call arg | same exclusion |
| `ControlFlow.cs:4076` | `ma.Member == "value"` | every member spelled `value` counts as a numeric read in static-int reasoning |

The historical record is in the comments themselves: "a field named value ... fell
through to the register path", "the descriptor call entirely (#419)", "`led.value`
silently read the pin id instead of the pin", "the augmented `.value` could be
silently dropped", "runtime-pointer bit writes previously modified the pointer
variable rather than the pointee". Each was fixed by adding a guard keyed on the same
spelling. p4/p19 above are the next bugs in that series, still open.

### Family 2: streaming shortcuts resolved by name suffix

`src/compiler/IR/IRGenerator/Call.cs`:

| Lines | Site | Suffix matched |
|---|---|---|
| 9376-9387 | `ResolveWriteStrFn` | `ResolveCallee("print_str")`, then `uart_write_str`, then any `inlineFunctions` key `EndsWith("_print_str")`/`EndsWith("_uart_write_str")` |
| 9472-9481 | `ResolveRuntimeWriteStrFn` | `EndsWith("uart_write_str")` over `functionParams` and `functionReturnTypes` |
| 9483-9490 | `ResolveFloatWriteFn` | `EndsWith("uart_write_float")` |
| 9494-9512 | `ResolveDecimalWriteFn` | `EndsWith("uart_write_decimal_u8/u16/u32/i16/i32")` |
| 10555-10565 | `ResolveByteReprFn` | `EndsWith("uart_write_byte_repr")` |
| 10623-10630 | `ResolveFmtFn` | `EndsWith("uart_write_fmt")` |
| 10632-10640 | `ResolveFloatFmtFn` | `EndsWith("uart_write_float_fmt")` |
| 10645-10653 | `ResolveHexFn` | `EndsWith("uart_write_hex")` |
| 11376-11389 | `ResolveByteWriteFn` | `EndsWith("uart_write")` |
| 11243-11282 | `TryEmitStreamMethodFString` | member spelled `write_str`/`println`; the receiver's class is already resolved by module (`IsStdlibClass`, 11232) since `13703a55`; `println` appends `"\n"` at 11280 |
| 11537 | `Member != "print_str"` | console member check |

What `fix/b1-byname` already removed: the member-call shortcut now asks
`IsStdlibClass` (defined *where*, not spelled *how*), and the driver detects
`print()`/`input()`/`UART()`/`ticks_ms()`/`millis_init()`/`clock_init()` from real
call nodes (`da0e80ff`, `42faaa90`, `bcf8a218`). What remains is the helpers'
*resolution*: `print(...)` does not name the console functions by the module binding
the driver injected; it scans every registered function for a name that *ends* in the
magic suffix. Any user `@inline def my_print_str` in any imported module is a
candidate (p33/p34 measured: today the console's entry happens to sort first, which
is ordering luck, not a rule).

The same family, one level up, is the builtin ladder in `VisitCallCore`
(`Call.cs:994-1300`): bare-name dispatch on `range`, `len`, `open`, `int_from_bytes`,
`struct_*`, `abs`/`min`/`max`/`ord`/`chr`, `sum`/`any`/`all`, `bool`, `hex`/`bin`/`oct`,
`str`, `repr`, `pow`, `round`, `memoryview`, `list`/`tuple`, `divmod`,
`CastTypes.ContainsKey(callee)` (`uint8(x)` etc. as calls), `bitcast`, `gc_alloc`,
`asm`, `isinstance`, `print`, `ptr`, `const`, `funcref`, `_set_irq_zca_arg`,
`compile_isr`, `claim`. Only `open`, `hex`, `bin`, `oct`, `round` consult
`IsBuiltinShadowed` (7896-7916). p5/p6/p15/p30 measured the rest.

### Family 3: `sleep_ms`/`delay_ms` recognized by name

The delay functions in `lib/src/pymcu/time.py` (`sleep` 51, `delay_ms` 62, `delay_us`
472) are ordinary stdlib functions resolved by module binding; the dispatch-by-name
left is:

| Lines | Site | What it does |
|---|---|---|
| `AsyncTransform.cs:2119-2159` | `TryGetAwaitSleep` | `m.Member == "sleep" \|\| m.Member == "sleep_ms"` on a receiver spelled like the asyncio alias; scales `sleep` x1_000_000, `sleep_ms` x1000, range-checks the literal |
| `AsyncTransform.cs:1736-1753` | await classification | only `await <aio>.sleep*` is a wait; everything else refuses |
| `build.py:488-500` | `_detect_ticks_ms_usage` | member-call spelling `ticks_ms`/`ticks_us`/`micros`/`monotonic`/`monotonic_ns` on *any* receiver reserves Timer0 (measured p17: `m.monotonic()` on a user class injects `millis_init`, +491 B) |
| `build.py:503-518` | `_detect_async_def_usage` | regex `_ASYNC_DEF_RE` (188) over comment-stripped text does the same on ATmega |
| `build.py:521-529` | `_sources_call` | `millis_init`/`clock_init` "already called" decided by a bare-name or attribute call (an unrelated `x.millis_init()` suppresses the injection) |

The `Core.cs:709` note ("`t.sleep_ms` ... mangles to `time_sleep_ms`") is ordinary
module-alias mangling, correct as is. `p10` measured `obj.sleep_ms(5)` on a user
class: correctly an ordinary method call today. The trap is the driver's receiver-
agnostic member-name scan, not the transform.

### Family 4: global intrinsics armed by any import of `pymcu.types`

`src/compiler/IR/IRGenerator/Core.cs`:

| Lines | Site | What it arms |
|---|---|---|
| 612-628 | unconditional seed | `uint8`..`int32`, `int`, `print`, `input`, `len`, `sum`, `any`, `all`, `hex`, `bin`, `str`, `pow`, `zip`, `reversed`, `divmod`, `bitcast`, `gc_alloc` in `intrinsicNames` |
| 681-696 | `imp.ModuleName == "pymcu.types"` in the entry file | adds `ptr`, `const`, `device_info`, `inline`, `naked`, `interrupt`, `asm`, `compile_isr`, `claim`, `_set_irq_zca_arg`, `funcref` |
| 745-764 | the same check for *every imported module* | 166 files under `lib/src/pymcu/` import `pymcu.types`, so nearly every program arms all of them program-wide |

`ResolveCallee` (`Core.cs:3637-3752`) consults that set before the module's own
definitions: member-name intrinsic hit at 3650-3652, bare-name intrinsic hit at 3662,
imported-alias spelling at 3686 and 3747. The builtin ladder in `Call.cs`
(989-1065, 1226-1295) then keys on the resolved name. Most arms re-check the set
(`callee == "ptr" && intrinsicNames.Contains("ptr")`, 1228; same shape for `const`,
`funcref`, `_set_irq_zca_arg`, `compile_isr`, `claim` through 1295, and
`funcref` even accepts the pre-mangled spelling `"pymcu_types_funcref"`), so a user
`def ptr` survives only while *nothing* imported `pymcu.types` anywhere (p7 shows the
common case losing). The `asm` arm at `Call.cs:1065` checks nothing at all:
`ResolveCallee` returns `"asm"` for the *user's* function and the ladder fires
`EmitAsmBuiltin` on the spelling alone, import or no import (p6).

Same family in the parser (`src/compiler/Frontend/Parser.cs:610-749`): decorators are
matched by spelling with no binding check at all -- `inline` (614), `extern` (618),
`property` (627), `asm_pio` (632), `rp2.asm_pio` (639), `<x>.setter`/`.getter` (654,
660), `interrupt` (687), `staticmethod` (716, accepted and ignored), `classmethod`
(720), `naked` (728), `used`/`export_c` (732), `outline` (738), `warning` (744). A
user decorator spelled `inline` or `naked` hijacks the function; a dotted
`@mylib.inline` is refused as unknown. `Scan.cs:4410`/`4445` add further
spelling-based checks on top.

### Family 5: `inline_`/`self_`/`tmp_` prefixes interpreted as names

| File:line | Site |
|---|---|
| `IR/NameResolution.cs:231-251` | `SpellingOf` classifies a storage key by prefix/shape: `tmp_` -> "temporary", `__lam` -> lambda, `A.B` -> dotted, `a_b` -> flattened-field |
| `IR/NameResolution.cs:258-265`, ~300, ~321 | prefix/scope resolution walks and the observer pass |
| `IRGenerator/State.cs:176` | `currentInlinePrefix` field |
| `IRGenerator/Core.cs:111-115` | `tmp_<counter>` minting |
| `IRGenerator/Core.cs:3859-3862` | `IsInjectedPreamble`: a call whose callee starts `_pymcu_` counts as the driver's injected preamble (a user `_pymcu_foo()` at module top is classified as injected) |
| `IRGenerator/Expr.cs:352`, `450-488` | temporary naming under `currentInlinePrefix` |
| `IRGenerator/Expr.cs:4667` | names starting `tmp_` excluded from a path |
| `IRGenerator/Expr.cs:5538`, `5564`, `6947`, `7299` | further `tmp_`/`self_`/inline-prefix checks deciding storage |
| `IRGenerator/Call.cs:5466` | `IsReceiverParamName`: `self` or `self_*` counts as a receiver parameter |
| `IRGenerator/Call.cs:5617` | base-method binding skips `self_*` params |
| `IRGenerator/Scan.cs:1391-1397`, `3536`, `3806` | `Target.StartsWith("self.")` detects instance fields |
| `IRGenerator/Scan.cs:4624` | synthesized parameter named `self_<field>` |
| `IRGenerator/Assign.cs:1772-1823` | inline-prefix handling |
| `IRGenerator/Assign.cs:3388` | alias traversal stops at a `tmp_` spelling |
| `IRGenerator/Assign.cs:3542` | `baseName.StartsWith("inline")` gates constant tracking |
| `IRGenerator/BoundOutline.cs:1608` | `head.StartsWith("inline")` on a synthesized name |

### Family 6: `sys`/`os`/`F_CPU` answered without a binding

`src/compiler/Frontend/CompileTimeEvaluator.cs`:

| Lines | Site |
|---|---|
| 46-54 | `__CHIP__`, `__FREQ__`/`F_CPU`, `__name__`, `__CHIP__.<field>` fold with no import check |
| 186-189 | the same names in the second evaluator path |

`src/compiler/IR/IRGenerator/Core.cs`:

| Lines | Site |
|---|---|
| 632-636 | `constantVariables["__FREQ__"]`, `["__FREQUENCY__"]`, `["__TIMEBASE__"]` seeded for every program |
| 1121, 2093 | `__name__` handling (kept: a real dunder) |
| 3136-3151 | `CheckIntrospectionBinding` refuses `from sys import platform` under a compat layer |

`src/compiler/IR/IRGenerator/Expr.cs`:

| Lines | Site |
|---|---|
| 6302-6304 | `IsModuleAlias`: `mods.Contains(n)` accepts the bare spelling `sys`/`usys`/`os`/`uos` with no binding check (p25, p32 measured) |
| 6307-6312 | `IsUnameCallee`: bare `uname()` and `x.uname()` on a receiver spelled `os`/`uos` |
| 6324-6349 | `IntrospectionTextOf`: `sys.implementation.name`, `sys.platform`, `uname().<field>` |
| 6373-6375 | `__CHIP__.<member>` by bare name |
| 6391-6404 | `sys.implementation.version` refusal and uname-field handling |
| 3326-3347 | `sys.implementation.version[i]` index path |

### Family 7: driver text matching that reserves resources or injects code

`src/driver/commands/build.py`:

| Lines | Site | Decision taken by text |
|---|---|---|
| 184-188 | `_PRINT_RE`/`_UART_RE`/`_TICKS_MS_RE`/`_INPUT_RE`/`_ASYNC_DEF_RE` | now only the fallback for a file `ast` cannot parse (321-337); the regexes stay live for that path |
| 243, 250-266 | `_RAISE_MSG_RE`/`_detect_raise_with_message` | a `raise X(` spelling -> console-writer injection; matches inside string literals, misses `raise (X)(...)` |
| 246-247, 269-279 | `_IMPORT_BOARD_RE`/`_imports_board` | decides whether the missing-board-file warning fires |
| 282-301 | `_module_level_shadows` | module-level shadowing for print/input -- already AST, correct pattern |
| 304-358 | `_detect_print_usage` | AST for `print`/`input`; but `attr == "UART"` (354) still counts `x.UART()` on any receiver (p16 measured) |
| 361, 364-389 | `_FSTRING_VALUE_RE`/`_detect_fstring_value_usage` | `= f"`, `.join(`, `str(`, `repr(` in text -> `pymcu.strfmt` injection; a user `def str` or a string literal containing the pattern still trips it |
| 488-500 | `_detect_ticks_ms_usage` | member spelling `monotonic`/`micros`/... reserves Timer0 (p17) |
| 503-518 | `_detect_async_def_usage` | `async def` by regex |
| 521-529 | `_sources_call` | `x.millis_init()`/`x.clock_init()` on any receiver counts as the init call |
| 545-546, 549-568 | `_OPEN_LITERAL_RE`/`_detect_open_literals` | `open("name")` literals discovered by regex for auto-embed |
| 634, 664-668 | `_MAIN_DEF_RE` in `_inject_preamble` | `def main():` found by regex to place injected lines |
| 1600-1700 | the injection call sites | stdout preamble (1603, 1626, 1639), strfmt (1649), millis_init (1675), clock_init (1696) |

`src/driver/core/libraries.py:491-518`: `chip_arch()` reads the chip's
`device_info(arch="...")` declaration out of the stdlib source with a regex over the
module text.

Already fixed in this direction (the pattern the rest follows): `140fc88d`
([NEEDS_ARENA]/[ARENA_USED]), `2d935c36c` (strfmt/round2 off the regex),
`da0e80ff`, `42faaa90`, `bcf8a218` (real-call detection), `1345fb469`
(console fns when `print()` coexists with a user `UART()`), `13703a55`
(`IsStdlibClass` for the stream-method shortcut).

## 4. Design

### 4.1 `ptr[T].value` and `[bit]`: real members of a real class (family 1)

`lib/src/pymcu/types.py` already declares the surface; the compiler simply stops
bypassing it:

```python
def __mmio_load__(p: "ptr[T]", width: int) -> int:
    # Compiler-reserved: a volatile load of p's element at its address.
    # CPython stub: registers do not exist on the host.
    raise RuntimeError("MMIO only exists in compiled code")

def __mmio_store__(p: "ptr[T]", width: int, value: int) -> None:
    raise RuntimeError("MMIO only exists in compiled code")

def __mmio_bit_load__(p: "ptr[T]", bit: int) -> int:
    raise RuntimeError("MMIO only exists in compiled code")

def __mmio_bit_store__(p: "ptr[T]", bit: int, value: int) -> None:
    raise RuntimeError("MMIO only exists in compiled code")

class ptr(Generic[T]):
    @property
    def value(self) -> T:
        return __mmio_load__(self, _width_of(T))

    @value.setter
    def value(self, v: T):
        __mmio_store__(self, _width_of(T), v)

    def __getitem__(self, bit: int) -> bool:
        return bool(__mmio_bit_load__(self, bit))

    def __setitem__(self, bit: int, value: int):
        __mmio_bit_store__(self, bit, value)
```

(`_width_of` stands for however the width reaches the intrinsic -- a literal the
property writer bakes per `T`, or a second type parameter; section 9 leaves the exact
spelling open.)

The compiler's part:

- `x.value` resolves like any member access: find the receiver's type, look the member
  up on it, lower what was found. On `ptr[T]` that is the `value` property, whose
  `@inline` body is an intrinsic call; the intrinsic lowers to `LDS`/`IN`/`OUT`/`STS`
  exactly where the register path emits them today. On every other receiver the
  register path does not exist at all.
- `x[i]` likewise lowers `ptr[T].__getitem__`/`__setitem__` over `__mmio_bit_*`; the
  constant-index requirement and the I/O-range `SBI`/`CBI`/`SBIS`/`SBIC` selection move
  into the intrinsic's lowering, unchanged.
- The reserved names are ordinary module members of `pymcu.types`: importing them
  binds them, the compiler intercepts a call only when the binding *is* that
  declaration (decision 2), and user code cannot reach them by accident because a
  name the user never imported does not resolve.
- `ReadsARegister` (`Call.cs:10876`) stops asking "is the member spelled value" and
  asks "did this expression lower through an MMIO intrinsic", which is already knowable
  where the call resolved -- the effect bit travels with the resolved callee, not the
  syntax.
- `IsKnownInstanceField`, `IsCollapsedFieldAnchor`, `IsPropertyGetterRead`,
  `IsDescriptorMemberRead`/`Write`/`IsNonDataDescriptorMember`, the `.value` arms at
  `Expr.cs:6752/6762/6775`, `Assign.cs:3079/3102/3121/10258`, the class-instance guard
  at `Expr.cs:4379` and `ControlFlow.cs:4076` are all deleted; the ordinary member
  ladder covers every case they were protecting.

`const[T]` gets the same treatment where it is intercepted (`.value` on a `const`
reads the folded constant -- already a member today, no intrinsic needed).

### 4.2 Streams: resolve through the injected binding (family 2)

`print()` and `input()` already arrive through a driver-injected binding
(`from pymcu.hal.console import print_str` plus `UART as _pymcu_stdout`,
`build.py:727-770`). The lowering resolves the call against that *bound* name, the
same way `_pymcu_strfmt._fs_i32` resolves through its injected import today.
`ResolveWriteStrFn`,
`ResolveRuntimeWriteStrFn`, `ResolveFloatWriteFn`, `ResolveDecimalWriteFn`,
`ResolveByteReprFn`, `ResolveFmtFn`, `ResolveFloatFmtFn`, `ResolveHexFn`,
`ResolveByteWriteFn` keep their signatures and change one line each: the suffix scan
becomes a `ResolveCallee` on the injected binding. Nothing is found by `EndsWith`.

`uart.write_str(f"...")`/`println` (`TryEmitStreamMethodFString`, 11243) already asks
`IsStdlibClass` and stays; decision 3 of this RFC is satisfied there. A *user* stream
class still cannot get the f-string streaming lowering, because the lowering is
registered to the stdlib UART class -- that is the honest status quo, and widening it
is out of scope (section 8).

The builtin ladder keeps its order but every builtin name consults the user-binding
check first: `IsBuiltinShadowed` generalizes to "does any in-scope binding claim this
spelling", the ladder only fires when none does (decision 4). `CastTypes` calls
(`uint8(x)`) resolve through the `pymcu.types` binding of `uint8`, so a user `uint8`
shadows the cast the same way.

### 4.3 `asyncio.sleep` and the driver's Timer0 reservation (family 3)

`TryGetAwaitSleep` keeps the member names `sleep`/`sleep_ms` -- they are the module's
API -- but qualifies them on the *resolved* module, not the spelled alias:
`aio` must be bound by `import asyncio[ as aio]`, which `FindAsyncioAlias` already
establishes, so the remaining change is matching the callee's binding rather than
`mod.Name`. `await x.sleep(1)` on a user `x` refuses with the same message it has
today.

Timer0 moves to the token channel (decision 5): the compiler emits
`[NEEDS_TIMEBASE]` when it lowers a call that reads the millis/micros counter --
`pymcu.time.monotonic`, `time.ticks_ms`, `asyncio.ticks` reached through a bound
module, or the coroutine machinery itself -- and `[TIMEBASE_INIT]` when it lowers a
call bound to `pymcu.hal.timer.millis_init`. The driver injects the preamble when it
sees the first without the second. `x.monotonic()` on a user class reports neither,
so nothing is reserved (p17); a program that calls `millis_init()` itself reports the
second token, so the preamble stays out. The same two-token shape replaces
`_detect_async_def_usage`: `async def` is a fact the compiler knows from the AST it
just transformed.

### 4.4 Intrinsics bind where they are declared (family 4)

`intrinsicNames` stops being a global set armed by "somebody imported pymcu.types".
Each intrinsic name listed in `Core.cs:681-696/745-764` is a declaration that exists
exactly once, in `pymcu.types`; a call lowers to it when `ResolveCallee` walks the
caller's bindings to that declaration. Concretely:

- The unconditional seeds at `Core.cs:612-628` become the builtins table -- the names
  Python's builtins scope holds. They stay unconditionally resolvable (that is what a
  builtin is) but lose to any user binding (decision 4).
- The `pymcu.types` names (`ptr`, `const`, `device_info`, `inline`, `naked`,
  `interrupt`, `asm`, `compile_isr`, `claim`, `_set_irq_zca_arg`, `funcref`) resolve
  only where the program bound them: `from pymcu.types import ptr`, `import
  pymcu.types` + `types.ptr`, or a re-export chain that ends at the declaration. A
  `def ptr` in the entry file binds `ptr` for that file, full stop (p7 fixed).
- `_set_irq_zca_arg` is not a user API: it moves out of the armed set entirely and
  becomes an internal emission detail of the interrupt machinery (family 5 covers
  the naming).
- Parser decorators (`Parser.cs:610-749`) resolve against the same rule where a
  binding exists: `@inline` means `pymcu.types.inline` when the program imported it,
  and stays a CPython-shaped decorator rejection ("unknown decorator") when it did
  not. A user `def inline` used as `@inline` keeps its meaning under CPython and is
  refused under PyMCU with a diagnostic naming the shadowing -- honest, like the
  `staticmethod` no-op comment at 716-719 already is.

### 4.5 Internal names stop wearing user spellings (family 5)

Synthesized bindings carry a flag in the binding record (`BindingKind` on the name
table: `User`, `Temporary`, `InlineExpansion`, `ReceiverParam`, `InjectedPreamble`),
and every place that today calls `StartsWith("tmp_")`/`("self_")`/`("inline")`/`("_pymcu_")`
asks the flag instead. The key text can keep its shape -- it is an internal storage
key -- but no *semantic* decision reads it. `SpellingOf` (`NameResolution.cs:231`)
stays as a diagnostic printer only; it is the observer-pass explainability surface,
not a resolver.

`IsInjectedPreamble` (`Core.cs:3859`) no longer guesses from the callee spelling: the
driver already marks injected lines with `# pymcu:injected` (`build.py:640`); the
frontends record that mark on the statement and the IR reads the flag. A user
`_pymcu_init()` at module top becomes ordinary code (or a refused reserved name --
section 9), not invisible scaffolding.

### 4.6 Chip facts and introspection bind explicitly (family 6)

- `__CHIP__`, `__FREQ__`, `F_CPU`, `__TIMEBASE__` bind through `pymcu.chips`:
  `from pymcu.chips import __CHIP__` (already the stdlib's own spelling, e.g.
  `time.py`/`asyncio.py`) or `import pymcu.chips` + `chips.__FREQ__`. `F_CPU` names
  the same binding: `from pymcu.chips import F_CPU`. The unconditional seeds at
  `Core.cs:632-636` and the evaluator arms at `CompileTimeEvaluator.cs:46-54/186-189`
  move behind the import. Under CPython the compat shims already provide these names
  on the package (`test_oracle.py:384-385`), so the source stays honest in both
  interpreters.
- `sys`/`usys`/`os`/`uos`/`uname` answer the introspection table only when the
  receiver's binding *is* the compat module (decision 2 applied to modules):
  `IsModuleAlias` drops its `mods.Contains(n)` clause and asks `importedAliases`.
  `sys = Fake(); sys.platform` then reads `Fake.platform` (p25, p32 fixed), and a
  program that wants the table writes `import sys` -- which under the native flavor
  keeps today's refusal naming the compat layers (p29), because pymcu's own `sys`
  does not carry `platform`.
- `__name__` stays ambient: it is a real module dunder, not a convenience alias.

### 4.7 The driver's remaining scans become tokens or real syntax (family 7)

Following the `NEEDS_*` precedent (`Logger.cs:141-157`,
`src/driver/core/compiler.py:140-147`):

| Scan today | Becomes |
|---|---|
| `_detect_raise_with_message` | `[NEEDS_EXNMSG]` emitted by the compiler when it lowers a raise whose argument is not provably absent (the shape `programBindsExceptionObject` already computes) |
| `_detect_fstring_value_usage` | `[NEEDS_STRFMT]` from the same sites that lower f-string-as-value and `str.join` materialization -- the strfmt token exists; its coverage extends to these |
| `_detect_ticks_ms_usage`, `_sources_call("millis_init")`, `_detect_async_def_usage`, `_sources_call("clock_init")` | `[NEEDS_TIMEBASE]`/`[TIMEBASE_INIT]`/`[NEEDS_CLOCKS]` tokens (section 4.3) |
| `_detect_open_literals` | the compiler resolves `open(...)` literal arguments itself; the embed list arrives as `[EMBED name]` tokens on the token stream |
| `attr == "UART"` in `_detect_print_usage` | `[STDOUT_OWNED]` when the compiler resolves a `pymcu.hal...uart.UART` construction (the same class check `IsStdlibClass` already does in the IR); the AST walk keeps covering only the bare `print(`/`input(` question |
| `_imports_board` | `ast` walk for real `import board` nodes (the file is already parsed for `_module_level_shadows`; this is a sibling node test, not a scan) |
| `_MAIN_DEF_RE` | `ast` parse of the entry file for `FunctionDef main` (same file-read path, real syntax) |
| `_DEVICE_INFO_ARCH` (`libraries.py:491`) | `ast.parse` of the chip module and a keyword match on the `device_info(...)` call node -- the file is Python, so it parses |
| unparseable-file regex fallback (`_source_has_named_call:231`, `build.py:324-337`) | stays, documented: a file CPython's `ast` cannot read falls back to the permissive spelling match, because over-injecting is the safe direction for a reservation the compiler will DCE away when unused |

## 5. Migration plan -- one phase per family, least to most risk

Every phase lands with the **zero-cost gate** of section 6. Order rationale: phases
that change no user-visible semantics go first; the phases that flip a measured
miscompile come once the machinery they need (binding tables, tokens) exists.

- **Phase 1 -- family 7, driver tokens.** Add `[NEEDS_TIMEBASE]`, `[TIMEBASE_INIT]`,
  `[NEEDS_CLOCKS]`, `[NEEDS_EXNMSG]`, `[EMBED]`, `[STDOUT_OWNED]` to `Logger.cs`
  beside `NeedsStrfmt`/`NeedsRound2`; emit them where the IR already resolves the
  call; teach `build.py` to consume them and delete the seven regex scans. No IR
  node changes; ROM must be byte-identical because identical programs get identical
  preambles. Files: `Logger.cs`, `Core.cs` (emission sites), `Assign.cs` (strfmt),
  `ControlFlow.cs` (raise), `commands/build.py`, `core/libraries.py`.
  - The emission site is the *resolved callee*, with no test on which file wrote
    the call: a `micros()` or `ticks_ms()` inside an installed library or an
    `@inline` helper it expands (e.g. the CircuitPython `keypad` queue reading
    `ticks_ms()`) arms the counter exactly as the same call in the program would,
    and a `raise X(msg)` in a library keeps its message runtime. The fixpoint
    stages a preamble only when a pass that ran to the end reports the absence --
    a pass interrupted by an embed failure cannot prove the user's `UART()` or
    `millis_init()` is not further down the file.
  - Deliberate behavior change (accepted): user code that *spells* the trigger
    names without *binding* them no longer counts -- a program-defined
    `ticks_ms`/`ticks_us`/`micros`/`monotonic`/`monotonic_ns`/`millis_init`/
    `clock_init` (method or function) reserves nothing and satisfies nothing
    (p16/p17 measured the same flip for `monotonic`/`UART`). Conversely
    `open(name)` where `name` is a compile-time constant the compiler resolves
    now auto-embeds, where the literal-only scan missed it.
- **Phase 2 -- family 6, explicit chip/introspection bindings.** `__CHIP__`,
  `__FREQ__`/`F_CPU`, `__TIMEBASE__` resolve only through `pymcu.chips`; `sys`/`os`
  aliases bind through `importedAliases`. Stdlib modules that rely on the ambient
  names (none may; audit `lib/` for bare `F_CPU`/`__FREQ__`) gain the import. ROM
  identical: the same folds produce the same constants.
- **Phase 3 -- family 4 (b), shadow ordering.** `IsBuiltinShadowed` generalizes to
  every builtin in the ladder and to the `pymcu.types` intrinsic names when the user
  binding is module-level; the ladder's `callee == "x"` checks run only on bindings
  that resolve to the builtin/intrinsic. Programs that never shadow are
  byte-identical; p5/p6/p15/p30 flip to the user function.
- **Phase 4 -- family 5, binding flags.** `BindingKind` lands on the name table;
  the `tmp_`/`self_`/`inline`/`_pymcu_` `StartsWith` reads move to it; the
  `pymcu:injected` mark is recorded on statements by both front ends. Pure
  internals; ROM identical by construction.
- **Phase 5 -- family 2, stream resolution through the binding.** The nine
  `Resolve*Fn` suffix scans become `ResolveCallee` through the injected alias; the
  ladder's builtin names gain the phase-3 shadow check. ROM identical: the same
  console functions resolve, now by binding.
- **Phase 6 -- family 1, `ptr[T]` by type.** `types.py` gains the four
  `__mmio_*__` declarations and the `ptr` property/`__getitem__`/`__setitem__`
  bodies; the IR resolves `.value`/`[i]` through the member ladder on the `ptr[T]`
  binding; the MMIO emission moves into the intrinsic lowering (same `LDS`/`STS`/
  `IN`/`OUT`/`SBI`/`CBI` paths, same constant-bit rule); the guard stack of section
  3.1 is deleted. ROM identical on the snapshot corpus: every `REG.value`/`REG[i]`
  lowers to the same instructions it lowers to now. p4/p19 flip to the correct
  values; p27 answers `True`.
- **Phase 7 -- family 4 (a), intrinsic arming by binding.** `intrinsicNames`
  dissolves: the unconditional seeds become the builtins table, the `pymcu.types`
  names resolve only where bound. Byte-identical for every program that already
  imports what it calls; a program that used a bare intrinsic spelling without the
  import gets a compile error naming the import, which is the intended break.

A phase lands only with its gate green; phases may interleave with unrelated work
because each is self-contained.

## 6. The zero-cost gate, run once per phase

Identical procedure every phase; a phase does not merge without all of it:

1. `tests/tools/rom_snapshot.py capture --file <scratch>/pre.json` with the pre-phase
   compiler, then `check --file <scratch>/pre.json` with the phase's compiler: every
   (program, chip) cell reports identical IR hash, identical asm hash, identical ROM
   figure, identical build outcome. Deliberate-behavior-change probes (section 7)
   are oracle probes, not snapshot cells; the snapshot corpus must not move.
2. `just test-unit`, `just test-stdlib`, `pytest tests/driver` in the PyMCU worktree.
3. Oracle corpus in pymcu-avr with the phase's `pymcuc`, both front ends:
   `pytest tests/oracle -q` and `PYMCU_PY_PARSER=1 pytest tests/oracle -q`.
4. Full AVR integration suite (`dotnet test tests/integration`) and
   `tools/verify_ir.py` with zero new warnings.
5. No feature-using program may pay a byte for the mechanism's existence, and no
   non-using program may change at all -- the snapshot check measures exactly this.

## 7. Risks and the test matrix

Regression probes that must keep passing (all measured green on main today):

- Field spelled `value` on a user class, slotted and single-field-collapsed
  (`p1`, `p2`); through a produced instance (`p14`); through a parameter (`p23`).
- Descriptor spelled `value`, data and non-data (`p3`, `p13`); property getter and
  setter (`p20`, `p24`); method spelled `value()` (`p11`); `__getitem__` on a user
  class (`p26`); class attribute `value = 66` write-through (`p22`).
- `ptr[uint8]` full-register and bit access, constant and runtime index
  (`p18`); `REG.value OP=`.
- User `def hex`/`def str`/`def len`/`def print`/`def ptr`/`def asm`
  (`p5`, `p6`, `p12`, `p15`, `p21`, `p30`) -- the corrected answers land with
  phase 3.
- `sleep_ms` on a user class (`p10`); `await asyncio.sleep_ms` still waits.
- `sys`/`os` answered only through the binding (`p9`, `p25`, `p28`, `p29`, `p31`,
  `p32`); `import sys` refusal on the native flavor kept.
- Driver: `x.UART()` on a user class does not suppress console init (`p16`);
  `x.monotonic()` on a user class reserves nothing (`p17`, the 1605 -> 1114 B
  diff); a real `UART()`/`ticks_ms()` still triggers the preamble.

New oracle probes to add (one per measured failure, so the flip is pinned):

- `probe`: `Port().value` property read == 10 (phase 6).
- `probe`: class attribute `value` read through an instance == 66 (phase 6).
- `probe`: `def len`/`def str`/`def asm`/`def ptr` run the user def (phase 3/7).
- `probe`: `def print` compiles and runs the user def (phase 3).
- `probe`: `sys`/`os` bound to a user object reads the user member (phase 2).
- `probe`: `hasattr(GPIOR0, "value")` == True (phase 6, if kept -- section 9).

Risks:

- **The guard stack is load-bearing in ways the comments only partially record.**
  Each deleted guard (e.g. `IsCollapsedFieldAnchor`, `Expr.cs:585`) protected a real
  class of programs; phase 6 keeps the *checks* as assertions on the binding ladder
  during bring-up, then removes them once the member path demonstrably covers every
  observed shape. The snapshot corpus plus the probe matrix is the evidence bar.
- **Alias-prefix machinery** (`Core.cs:841-900`, `functionModulePrefix`,
  `perModuleImportedAliases`) was built name-first; binding-keyed resolution sits on
  top of it in phases 3/7, and a miss there is a *compile error*, never silent.
- **The injected-alias channel** (phase 5) assumes the driver always injects the
  console module when print is used; a hand-invoked `pymcuc` without the preamble
  gets a missing-name error for `print_str`, same failure shape as today.
- **`pymcu.types` import removal programs**: today an unused `from pymcu.types
  import ptr` arms the intrinsic anyway. After phase 7 it binds `ptr` to the
  declaration -- same outcome for a program that calls `ptr(...)`, different for a
  program that *redefined* `ptr` (which is the fix, p7).

## 8. Out of scope

- Streaming for *user-defined* stream classes (`lcd.write_str(f"...")` on a user's
  own LCD type): the stdlib-UART shortcut stays stdlib-UART; a protocol/structural
  stream type is a separate RFC if it is wanted.
- Non-native flavors' per-arch `sys`/`os` module surfaces beyond the introspection
  names listed.
- Reordering or renaming the storage-key text (`tmp_`/`self_` keys may keep their
  spellings once nothing reads them semantically).
- `IsModuleAlias`'s remaining uses that are already binding-correct, and
  diagnostic-only name mentions (error text may keep naming `delay_ms`).
- The compat layers' own file contents (`pymcu-micropython`/`pymcu-circuitpython`
  `sys.py`/`os.py` placeholders stay placeholders).
- Documentation-site edits: nothing user-visible lands in this branch; each
  implementation phase updates `~/Repos/pymcu-docs` with the phase that ships it.

## 9. Open questions

1. Exact spelling of the reserved intrinsics: `__mmio_load__`/`__mmio_store__`/
   `__mmio_bit_load__`/`__mmio_bit_store__` here; any dunder-shaped name the parser
   reserves works. Alternatives: a `@compiler_intrinsic` marker on the declaration,
   which would make "compiler-reserved" itself a declared property rather than a
   name list.
2. How the element width reaches `__mmio_load__`/`__mmio_bit_*`: a constant the
   `value` accessor bakes from `T`, or the intrinsic reads `self`'s declared
   `ptr[T]` argument. The latter keeps `types.py` simpler and matches how `ptr`
   already knows its width; preferred unless the inline expansion loses it.
3. Whether a user-defined `_pymcu_*` name is (a) allowed and invisible to the
   preamble logic, or (b) refused as a reserved spelling. (a) is more Python; (b)
   catches a class of collisions cheaply. Decision 6 reads neutral either way.
4. `hasattr(ptr_instance, "value")`: under the property model it is a member and
   answers `True`; whether anything depends on today's `False` is unmeasured --
   the oracle corpus does not exercise it.
5. Whether `open()`'s `[EMBED]` token should carry the resolved *path* (compiler
   knows the literal, driver owns filesystem lookup) or keep the driver's
   discovery under `ast` where the literal is a real `Constant` node. The token
   shape is preferred for symmetry with `NEEDS_*`.
