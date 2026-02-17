# PyMCU Language Features Roadmap

## Currently Supported Features

### Statements
| Feature | Status | Notes |
|---------|--------|-------|
| `if` / `elif` / `else` | Complete | Compile-time dead code elimination for `__CHIP__` branches |
| `while` | Complete | Full with `break` / `continue` support |
| `match` / `case` | Complete | Literal + wildcard `_` patterns; compile-time DCE for `__CHIP__` |
| `def` (functions) | Complete | Typed parameters, default values, keyword arguments |
| `class` | Complete | Static class flattening, `@inline` methods, constructors |
| `return` | Complete | With and without value |
| `break` / `continue` | Complete | |
| `pass` | Complete | |
| `raise` | Complete | Compile-time only (error propagates to user call site) |
| `import` / `from ... import` | Complete | Relative imports, multi-level |
| `global` | Complete | Cross-function variable access |

### Expressions
| Feature | Status | Notes |
|---------|--------|-------|
| Integer literals | Complete | Decimal, hex (`0xFF`), binary (`0b1010`), octal (`0o17`), underscores |
| Boolean `True` / `False` | Complete | Folds to `Constant{0/1}` |
| String literals (double-quoted) | Complete | Mapped to stable integer IDs for compile-time folding |
| Arithmetic (`+`, `-`, `*`, `/`, `%`) | Complete | Full constant folding |
| Comparison (`==`, `!=`, `<`, `<=`, `>`, `>=`) | Complete | |
| Bitwise (`&`, `\|`, `^`, `~`, `<<`, `>>`) | Complete | |
| Logical (`and`, `or`, `not`) | Partial | **No short-circuit** — mapped to bitwise ops |
| Unary (`-`, `~`, `not`) | Complete | Constant folding |
| Augmented assignment (`+=`, `-=`, etc.) | Partial | Variable targets only (not `port[0] += 1`) |
| Bit indexing (`port[n]`) | Complete | Index must be compile-time constant |
| Member access (`obj.x`) | Complete | Flattened/mangled for zero-cost |
| Method calls (`obj.method()`) | Complete | Inline expansion |
| Keyword arguments (`f(key=val)`) | Complete | Matched by name in inline binding |

### MCU-Specific Extensions
| Feature | Status | Notes |
|---------|--------|-------|
| Typed parameters (`x: uint8`) | Complete | Required — untyped params error |
| Type annotations (`x: ptr[uint16]`) | Complete | `uint8/16/32`, `int8/16/32`, `ptr[T]`, `const[T]` |
| Memory-mapped I/O (`ptr(addr)`) | Complete | |
| Bit manipulation (`port[0] = 1`) | Complete | |
| `.value` dereference | Complete | 8/16-bit memory read/write |
| `delay_ms(n)` / `delay_us(n)` | Complete | Intrinsic statement |
| `const()` / `const[T]` | Complete | Compile-time constant enforcement |
| `@inline` decorator | Complete | Zero-cost abstraction |
| `@interrupt(vector)` | Complete | ISR handler generation |
| `device_info()` | Complete | Chip/architecture configuration |
| Conditional compilation (`__CHIP__`) | Complete | `if`/`elif` and `match`/`case` both supported |

### Decorators
| Decorator | Status |
|-----------|--------|
| `@inline` | Complete |
| `@interrupt(vector)` | Complete |
| `@staticmethod` | Silently ignored (all class methods are static) |

---

## Roadmap: Missing Python Features

### Phase 1 — Essential (High Priority)

These features are commonly needed in MCU firmware and have clear MCU-relevant use cases.

#### 1.1 `for` loop
- **Effort:** Medium
- **Why:** Iterating over pin arrays, lookup tables, initialization sequences
- **Scope:** `for i in range(n):` first, then `for x in iterable:`
- **Notes:** Token already exists, parser has TODO stub. Needs `ForStmt` AST node + IR visitor. `range()` can be compiled to a counter loop (no heap allocation).

#### 1.2 `None` literal
- **Effort:** Low
- **Why:** Default parameter sentinel, optional return values
- **Scope:** Add `parsePrimary()` case → `Constant{-1}` or dedicated `NoneVal`
- **Notes:** Token already exists in lexer

#### 1.3 Single-quoted strings (`'...'`)
- **Effort:** Low
- **Why:** Standard Python — IDE and linter compatibility
- **Scope:** Lexer change to accept `'` as string delimiter

#### 1.4 Short-circuit `and` / `or`
- **Effort:** Medium
- **Why:** Correctness — `if pin and pin.value():` must not evaluate RHS when LHS is false
- **Scope:** Change `visitBinary` for `And`/`Or` to emit `JumpIfZero`/`JumpIfNotZero` chains instead of bitwise ops

#### 1.5 Floor division (`//`)
- **Effort:** Low
- **Why:** Integer division is the common case on MCUs
- **Scope:** Add `DoubleSlash` token + `BinaryOp::FloorDiv` + codegen. Current `/` already does integer division on uint8/16, so this may just need the token + alias.

#### 1.6 Augmented assignment on index/member targets
- **Effort:** Medium
- **Why:** `PORTA[0] ^= 1` (toggle bit) is idiomatic MCU code
- **Scope:** Extend `visitAugAssign` to handle `IndexExpr` and `MemberAccessExpr` targets

#### 1.7 `import ... as` alias
- **Effort:** Low
- **Why:** Standard Python import pattern
- **Scope:** Parse `as` keyword in `parseImportStatement()`, store alias mapping

### Phase 2 — Important (Medium Priority)

Features that improve code quality and enable more complex firmware.

#### 2.1 Ternary expression (`x if cond else y`)
- **Effort:** Medium
- **Why:** Common in register configuration: `val = 0xFF if mode == 1 else 0x00`
- **Scope:** New AST node `ConditionalExpr`, parsed in expression chain, emits conditional jump sequence

#### 2.2 Tuple support (basic)
- **Effort:** Medium
- **Why:** Multiple return values: `def read_sensor() -> (uint8, uint8)`
- **Scope:** `TupleExpr` AST node, tuple packing/unpacking for function returns. On MCU, tuples are stack-allocated structs.

#### 2.3 `assert` statement
- **Effort:** Low
- **Why:** Compile-time validation (like `raise`, can be compile-time only)
- **Scope:** `assert condition, "message"` → `AssertStmt` → compile-time check or stripped

#### 2.4 `Enum` class support
- **Effort:** Medium
- **Why:** Pin modes, error codes, state machines — cleaner than integer constants
- **Scope:** Recognize `class Mode(Enum):` → fold members to integer constants. Zero-cost.

#### 2.5 Chained member access in calls (`a.b.c()`)
- **Effort:** Medium
- **Why:** Nested module/object access: `hal.gpio.Pin()`
- **Scope:** Currently throws. Need recursive resolution in `visitCall`.

#### 2.6 Float support (basic)
- **Effort:** High
- **Why:** Sensor calibration, scaling factors
- **Scope:** `FloatLiteral` already parsed. Need IR + codegen for software float (PIC already has `math/pic16/float.inc`). Consider fixed-point alternative.

#### 2.7 Module-level code emission
- **Effort:** High
- **Why:** Module-level `Pin()` calls should emit setup code at start of `main()`
- **Scope:** IR generator needs a "module init" pass before `main()` body. Important for IDE-compatible code (avoids cross-scope ISR references).

### Phase 3 — Nice to Have (Lower Priority)

Features that enhance expressiveness but aren't critical for MCU firmware.

#### 3.1 List / array support
- **Effort:** High
- **Why:** Lookup tables, pin arrays, DMA buffers
- **Scope:** Fixed-size arrays allocated in RAM or ROM. `ListExpr` + `IndexExpr` for runtime access. No dynamic allocation (MCU constraint).

#### 3.2 `try` / `except` (compile-time only)
- **Effort:** Medium
- **Why:** Error handling patterns. On MCUs, exceptions can't be runtime (no stack unwinding). Could be compile-time only like `raise`.
- **Scope:** Parse `try`/`except` blocks. At compile-time, if the `try` block raises, take the `except` branch. No runtime exception mechanism.

#### 3.3 `with` statement (resource management)
- **Effort:** Medium
- **Why:** SPI transactions, critical sections: `with spi_bus: ...`
- **Scope:** `__enter__`/`__exit__` inline expansion. Can generate interrupt-disable/enable pairs.

#### 3.4 F-strings (`f"value={x}"`)
- **Effort:** High
- **Why:** Debug output over UART
- **Scope:** Requires string formatting runtime. Possibly compile-time only for constant expressions.

#### 3.5 `yield` / generators
- **Effort:** Very High
- **Why:** Cooperative multitasking, state machines
- **Scope:** AST node exists, IR throws. Requires coroutine transformation (state machine rewrite). Very useful for MCU cooperative schedulers.

#### 3.6 Dataclasses
- **Effort:** Medium
- **Why:** Structured sensor data, configuration
- **Scope:** `is_dataclass` flag already in AST. Auto-generate `__init__` from field declarations. Compile to RAM struct.

#### 3.7 Type unions / Optional
- **Effort:** Low-Medium
- **Why:** `Optional[uint8]` for nullable return values
- **Scope:** Sentinel-based (e.g., -1 for None). No heap allocation.

### Not Planned

These Python features don't make sense for MCU firmware:

| Feature | Reason |
|---------|--------|
| Dynamic typing | MCU requires static type resolution for RAM allocation |
| Heap allocation / `malloc` | Most MCUs have 32-256 bytes RAM |
| Garbage collection | No runtime |
| Metaclasses | Overkill for embedded |
| Multiple inheritance | Complexity vs. benefit |
| `*args` / `**kwargs` | Requires heap |
| List comprehensions | Requires dynamic allocation |
| Closures / nested functions | Requires heap for captured variables |
| `async` / `await` | Use `@interrupt` + `yield` instead |
| Reflection / `getattr` | No runtime type info |
| `eval()` / `exec()` | No interpreter on MCU |

---

## Implementation Priority Matrix

```
        High Value
            |
   for loop | enum support
   None lit | ternary expr
   '...'    | tuple returns
   short-   | assert
   circuit  | module-level
            | code emission
   ─────────┼─────────────
            |
   // div   | float support
   aug idx  | list/array
   as alias | with stmt
            | f-strings
            | yield/gen
            |
        Low Value
   Low Effort          High Effort
```

---

## Version Targets (Suggested)

### v0.2 — Core Language Completeness
- `for i in range(n):` loops
- `None` literal
- Single-quoted strings
- Short-circuit `and`/`or`
- `//` floor division
- `import X as Y`

### v0.3 — Expressiveness
- Ternary expressions
- Augmented assignment on index/member targets
- `assert` (compile-time)
- Chained member access calls
- Module-level code emission

### v0.4 — Data Structures
- Basic tuple support (multiple return values)
- Enum classes (zero-cost)
- Fixed-size array/list support
- Dataclasses

### v0.5 — Advanced
- Float support (software / fixed-point)
- `with` statement (resource management)
- `yield` / generators (cooperative multitasking)
- `try`/`except` (compile-time)
