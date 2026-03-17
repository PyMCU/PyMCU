# Contributing

Contributions to PyMCU are welcome. Please read this guide before opening a PR.

## Repository layout

```
pymcu/
  src/compiler/           # C++ compiler (pymcuc)
    frontend/             # Lexer, Parser, AST
    ir/                   # IRGenerator, Optimizer, Tacky IR
    backend/targets/avr/  # AVR codegen, peephole, register allocator
  lib/src/pymcu/          # Python stdlib (compiled into firmware)
    hal/                  # GPIO, UART, ADC, Timer, PWM, SPI, I2C
    drivers/              # DHT11 and other device drivers
    boards/               # Board pin name constants
    chips/                # Chip configuration and __CHIP__
  src/driver/             # Python CLI driver (pymcu build/flash/new)
  tests/integration/      # .NET / AVR8Sharp integration tests
  examples/avr/           # Firmware examples (each with a full test suite)
  docs/                   # Markdown reference docs
  docs-site/              # MkDocs + Material documentation site
```

## Building the compiler

```bash
cmake -B build -S src/compiler -DCMAKE_BUILD_TYPE=Release
cmake --build build --target pymcuc -j$(sysctl -n hw.ncpu)
```

## Running integration tests

```bash
dotnet test tests/integration/PyMCU.IntegrationTests.csproj
```

All 175+ tests must stay green. Add new tests in `tests/integration/Tests/AVR/` for any new
compiler feature.

## Adding a stdlib module

1. Add the implementation in `lib/src/pymcu/hal/` (or `drivers/`).
2. Use `@inline` for all public methods (zero-cost abstraction rule).
3. Use `match __CHIP__.arch:` for architecture dispatch.
4. No non-ASCII characters in source (compiler lexer is ASCII-only).
5. No multiline docstrings with code examples — use `# comments` instead.
6. After editing stdlib, rsync to the local virtualenv:
   ```bash
   rsync lib/src/pymcu/ .venv/lib/python3.X/site-packages/pymcu/
   ```

## HAL coding rules

- No em dashes (U+2014) or other non-ASCII in source.
- No statements after `match` blocks — put defaults in `case _:` inside the match.
- Dotted names (`ClassName.ATTR`) are value patterns in `match/case`.
- Bare names (`CONST_NAME`) are capture patterns — use dotted names for named constants.
- `@inline` functions containing `asm()` with labels must delegate to a non-inline sub-helper.

## Docs site

```bash
cd docs
pip install -r requirements.txt
mkdocs build --strict    # must pass with no warnings
mkdocs serve             # preview at http://127.0.0.1:8000
```

## Commit style

```
feat: add ternary expression (T1.1)
fix: peephole pattern A corrupts inline multi-return
docs: add LANGUAGE_REFERENCE.md for alpha release
test: add TupleOpsTests for divmod8 multi-return
```
