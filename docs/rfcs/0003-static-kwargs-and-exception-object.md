# RFC 0003: static `**kwargs` and a bounded exception object

- Status: **PROPOSED.** Not implemented.
- Issues: [#368](https://github.com/PyMCU/PyMCU/issues/368) (`**kwargs` / `*args`),
  [#369](https://github.com/PyMCU/PyMCU/issues/369) (`except X as e`)
- Date: 2026-09-14
- Affects: `src/compiler/Frontend/Parser.cs`, `Ast.cs`, `AstJsonWriter.cs`,
  `PythonAstReader.cs`, `PyParser/pymcu_translate.py`, `src/compiler/IR/IRGenerator/Call.cs`,
  `ControlFlow.cs`, `Scan.cs`, `extensions/pymcu-sdk/csharp/IR/Tacky.cs`, and the AVR backend
  in `pymcu-avr`.

Two features, one principle. Both are forms Python resolves at run time with a heap, and both
have a shape here in which the answer is already known when the compiler is looking at them.
Neither adds a container.

---

## Part 1: `**kwargs` and `*args` as compile-time mappings and sequences

### 1.1 Problem

Both forms are refused at the `def`:

```
Parser.cs:888   '**kwargs' is not supported: it collects arguments into a run-time
                dictionary, and there is no heap for one. [...]
Parser.cs:878   Variadic '*args' parameters are not supported; '*' is only valid as a
                keyword-only separator
```

The reason given is true of CPython and false of this target. A function taking `**kwargs`
is already specialised per call site, exactly as a function taking a ZCA instance is
(`Scan.cs:1236-1250` registers both in `inlineFunctions` for the same reason: an instance has
no subroutine ABI, so the callee is expanded where it is called). At each expansion the extra
keyword arguments are literals in the source. Nothing has to be collected, because nothing is
unknown.

Three Adafruit libraries stop here and report nothing else:

| Library | Line | Shape |
|---|---|---|
| `adafruit_debouncer` | 162 | `Button.__init__(..., **kwargs)` forwarded by `super().__init__(pin, **kwargs)` |
| `adafruit_74hc595` | 73 | `switch_to_output(self, value=False, **kwargs)`, never read |
| `adafruit_pcf8574` | 126 | `switch_to_output(self, value=False, **kwargs)`, never read |

The last two collect keyword arguments only to discard them, for signature compatibility with
`digitalio.DigitalInOut`. They need an empty mapping and nothing more.

### 1.2 Model

At a call site, `f(a=1, b=2)` where `f` declares `**kwargs` binds `kwargs` to a **closed
compile-time mapping**: keys are string literals, values are the argument expressions. That is
the same object a dict literal already lowers to, so the whole read side exists:

| Form | Where it already lives |
|---|---|
| `kwargs["k"]` | `Expr.cs:1732` `EmitDictLookup` |
| `kwargs.get("k", d)` | `Call.cs:2401` `TryEmitDictMethod` |
| `"k" in kwargs` | `Expr.cs:890-916` |
| `len(kwargs)` | `Call.cs:3383` |
| `for k, v in kwargs.items()` | `Iteration.cs:840-859` |

`*args` binds to the other half of the same machinery: a list of numbers is a
`constSequenceBindings` entry (`State.cs:825`) and a list of instances is a base key with
`arraySizes` (`Sequences.cs:354`). The positional binding loop already reaches both
(`Call.cs:1669`, `Call.cs:1697`).

**Splicing** is the one new operation. `f(**kwargs)` and `super().__init__(self, **kwargs)`
expand the known keys into `KeywordArgExpr`s before the callee is bound, which turns
forwarding into the call the program would have written by hand. Correspondingly, the
firmware must be the one that call produces. The two-class forwarding example in #368,
written out by hand, builds to **272 bytes of code** on atmega328p. The `**kwargs` spelling
of the same program has to match that number, not approach it.

### 1.3 Surfaces to change

**Front ends.** `CallExpr.Args` is a single heterogeneous list (`Ast.cs:237`) already holding
`KeywordArgExpr` and `StarArgExpr`, so `**d` is a third element kind, not a new field. The
`def` side has no vararg or kwarg field at all (`Param`, `Ast.cs:635`), so it needs a flag on
`Param`. Both front ends and both JSON directions move together: `Parser.cs:2622` (the
argument loop) and `:863` (the parameter loop), `pymcu_translate.py:559` and `:1111`,
`AstJsonWriter.cs:579`, `PythonAstReader.cs:465`.

**Three binders, not one.** This is the part that is easy to get half right. Arguments are
bound to parameters in three independent places:

| Binder | Line | Used by |
|---|---|---|
| `ReorderCallArgs` | `Call.cs:759` | ordinary subroutine calls |
| the keyword loop in `EmitInlineFunctionCall` | `Call.cs:2019-2209` | `@inline` and ZCA expansion |
| `BindMethodArgs` | `Call.cs:2810` | `super().m()` and `Base.m(self, ...)` |

`super().__init__(**kwargs)` goes through the third. Splicing therefore has to happen before
the callee is chosen, and today it does not: `SpliceStarArgs` runs at `Call.cs:163`, after
`TryEmitSuperMethodCall` at `Call.cs:150`, so `super().__init__(*a)` is never spliced. The
splice point moves above line 150 and gains the `**` case.

**Specialisation is keyed by depth, not by call site.** The expansion prefix is
`$"inline{newDepth}.{func?.Name}."` (`Call.cs:1215`), so two calls to the same function at the
same depth reuse the same names. Every piece of per-parameter state in the generator is
therefore cleared explicitly at each binding (`constantAddressVariables` at `Call.cs:1664` and
`:2035`, `listLiteralParams` at `:1691`, `constSequenceBindings` at `:1697`, `noneValuedNames`
at `:2048`). A `kwargs` binding is per-parameter state and must be cleared in all three loops
or an expansion inherits the previous call's keys. That is the failure mode of #194 and #324,
and it is silent.

### 1.4 Refusals

Three, each a located sentence:

- A `**` argument whose operand is a run-time mapping (`FixedDict`), saying the keys have to
  be known at compile time.
- A key no callee accepts, **naming the key**. This is the whole value of splicing statically:
  a misspelled keyword is a build error rather than an argument silently dropped. The existing
  sentence at `Call.cs:2136-2143` already names it and is the one to reach.
- A `kwargs` used where a run-time mapping would be needed.

### 1.5 Two front-end defects that fall out

The CPython bridge raises `Unsupported("**kwargs")` and `Unsupported("*args")`, which surface
as the bare feature name where the C# parser writes a sentence. The same refused program is
explained on one front end and merely named on the other.

`f(**d)` on the C# front end has no branch in the argument loop, walks off the end, and blames
line 1 of the file, which is a comment the driver injected:

```
main.py:1:3: error: SyntaxError: Expected expression
1 | # Auto-injected by pymcu build: stdout=uart0 at 115200 baud for print()  # pymcu:injected
```

Both disappear with the form itself.

---

## Part 2: `except X as e` with a bounded exception object

### 2.1 Problem

The form is refused in both front ends (`Parser.cs:1380`, `pymcu_translate.py:981`) with a
sentence that states the model accurately: a raise carries which exception was raised and
nothing else.

The second half is not stated anywhere, and it is silent. A payload written at a raise is
parsed and thrown away. This builds, and prints `caught`:

```python
class SensorError(Exception):
    pass

def read() -> uint8:
    raise SensorError(3)
```

`ParseRaiseStatement` accepts any non-string expression and discards it
(`Parser.cs:1252-1281`). A user-defined exception class is registered as an integer code and
its body is never scanned (`Scan.cs:1274-1282`, the `continue` on line 1281), so `__init__` is
not compiled and a field it sets exists nowhere. Nothing in the build says so.

### 2.2 Model

One exception is live at a time here. That is not a restriction added for this feature, it is
what the T-flag model already is: a code in R22 travelling up through returns. So the
exception object is static storage, not an allocation.

**(a) The message.** A raise with a string-literal message records the static string id of
that message alongside the code. `VisitRaise` already resolves the text at compile time
(`ControlFlow.cs:1832-1845`, `resolvedMessage`) and then drops it on the floor at line 1955.
The string interning that `print` uses is `InternStringAsFlash` (`Core.cs:2046`), which emits
a `FlashData` record and gives back a name a `FlashStrAddr` can point at. One store per raise,
two bytes of RAM.

The store is emitted **only when some handler in the program binds a name**. A program with no
`as e` anywhere is unchanged, which is the gate in 2.4. The decision is whole-program, so it
belongs to a pre-pass over the AST, not to `VisitRaise` in isolation.

`print(e)`, `str(e)` and `e.args[0]` read the slot. A message that is not a literal stays
refused in the words it is refused in today, because there is no run-time string to record:

```
raise ValueError(code): 'code' is not a string constant known at compile time. [...]
```

**(b) The type.** `type(e)`, `isinstance(e, X)` and a bare re-raise fold against the code the
dispatcher already copies into `__exn_code_N` (`ControlFlow.cs:2066`). No new storage: the
comparison the handler chain performs is the comparison `isinstance` needs.

**(c) User-defined fields.** A user exception class whose `__init__` sets fields gets **one
static slot per class**, not per raise: one exception is live at a time, so the slots of two
different classes never have to coexist. `raise Mine(err=3)` writes the slot, `e.err` reads
it. This requires `Scan.cs:1274-1282` to stop skipping the class body, which is the change
with the largest blast radius in Part 2, because that `continue` is what keeps a user
exception out of `classNames`, `classFieldLayout` and the vtables. A field whose type has no
static width is refused by name.

Every other use of `e` refuses with a located sentence naming the four things `e` supports.

### 2.3 Surfaces to change

- `Ast.cs:610` carries the bound name. `TryStmt.Handlers` is `List<(string ExnType, List<Statement> Handler)>` and
  gains the bound name; `AstJsonWriter.cs:329` and `PythonAstReader.cs:338` carry it.
- `Parser.cs:1380-1387` and `pymcu_translate.py:981-985`. The refusals move together, or the
  AST contract the two front ends share stops holding.
- `Tacky.cs:259`. `SignalError(Val Code, string? CatchLabel = null)` is the only carrier
  today. A payload is a second `Val`. Everything that pattern-matches the record moves with
  it: `AvrGpiorPromotion.cs:201`, `AvrLinearScan.cs:170`, `AvrCodeGen.cs:4563`,
  `CanFailAnalyzer.cs:141`, and seven sites in `Optimizer.cs`.
- `AvrCodeGen.cs:4949` `CompileSignalError`. R22 carries the code; the payload needs a second
  register or a static location. A register costs nothing at the raise and has to survive the
  callee's return, which R22 does only because the ABI says so.

### 2.4 Gates

ROM differential byte-identical for every program with no `as e`. For programs that use it,
the exact cost is reported rather than asserted: the reference is **388 bytes of code** on
atmega328p for the `adafruit_dht` shape written without the binding, message repeated at the
handler.

---

## Sequencing

Part 1 first. It is smaller, it touches no backend, and it moves three libraries. Part 2
changes an IR record that five files in `pymcu-avr` pattern-match, so it wants the compiler
tree to itself.

## Fixtures

In `pymcu-avr/tests/integration/fixtures`:

- `kwargs-forwarding`, with three levels of forwarding, a default two levels down that nothing
  writes, the discard shape, the four folding reads, `items()` unrolling, `*args`.
- `compat-mp-kwargs-forwarding`, the same splice through the machine layer, where the base
  `__init__` is `@inline` and takes a ZCA `Pin`.
- `exception-object`, with the `adafruit_dht` pattern, a user exception with a field, a re-raise
  from a binding handler into a differently-named one, a nested try with two live bindings,
  `isinstance`.

A key rejected by name cannot be a fixture, because a fixture has to build. It is held in
`tests/unit/Frontend/StaticKwargsFormTests.cs` alongside the run-time-mapping refusal and the
front-end parity assertion.
