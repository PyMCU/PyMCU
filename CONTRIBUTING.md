# Contributing to PyMCU

Thank you for your interest in contributing to PyMCU! We welcome bug reports, feature requests,
and pull requests.

---

## Development Setup

1. **Clone the repository:**
   ```bash
   git clone https://github.com/begeistert/pymcu.git
   cd pymcu
   ```

2. **Install Python dependencies:**
   ```bash
   uv sync --dev
   ```

3. **Build the compiler:**
   ```bash
   cmake -B build -S src/compiler -DCMAKE_BUILD_TYPE=Release
   cmake --build build --target pymcuc -j$(sysctl -n hw.ncpu)
   ```

4. **Run integration tests:**
   ```bash
   dotnet test tests/integration/PyMCU.IntegrationTests.csproj
   ```

All tests must stay green. Add a new test in `tests/integration/Tests/AVR/` for any new
compiler feature before merging.

---

## Repository Layout

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
  docs/                   # MkDocs documentation site
```

---

## Commit Guidelines

PyMCU uses the **Conventional Commits** specification. Every commit must follow this format:

```
<type>(<scope>): <short description>

[optional body]

[optional footer: BREAKING CHANGE or issue refs]
```

### Types

| Type | When to use |
|------|-------------|
| `feat` | A new feature visible to users or firmware developers |
| `fix` | A bug fix |
| `docs` | Documentation only |
| `test` | Adding or correcting integration / unit tests |
| `refactor` | Code change that neither fixes a bug nor adds a feature |
| `perf` | Performance improvement |
| `chore` | Build system, CI, dependency updates |
| `style` | Formatting only (whitespace, indentation) |

### Scopes

| Scope | Area |
|-------|------|
| `avr` | AVR codegen or AVR-specific backend |
| `ir` | IR generator, optimizer, or Tacky IR |
| `parser` | Lexer / Parser / AST |
| `hal` | Any HAL module (`gpio`, `uart`, `spi`, ...) |
| `driver` | Python CLI driver (`pymcu build/flash/new`) |
| `stdlib` | Any module under `lib/src/pymcu/` |
| `drivers` | Device drivers (`dht11`, `neopixel`, `lcd`, ...) |
| `test` | Integration or unit test files |
| `docs` | Documentation site or Markdown files |
| `ci` | GitHub Actions workflows |
| `deps` | Dependency bumps |

### Examples

```
feat(avr): add PROGMEM flash array support
fix(ir): wrong register spill in 16-bit aug-assign with constant RHS
test(avr): add NestedListCompTests for filtered comprehension
docs: update limitations table — bytearray and with are now supported
refactor(hal): extract SPI CS logic into select()/deselect() helpers
chore(deps): bump avr8sharp to 1.4.0
```

---

## Splitting Feature Commits

**Each distinct compiler or HAL feature must be implemented in small, focused commits.**
Never bundle multiple unrelated changes in one commit.

A typical feature lands as 2-5 commits, for example:

```
feat(parser): parse @extern decorator on function definitions
feat(ir): emit Extern IR instruction and register extern symbols
feat(avr): emit .extern directive and CALL with AVR ABI for @extern
feat(stdlib): add pymcu/ffi.py re-exporting extern
test(avr): add ExternCallTests for @extern C interop
docs: add @extern / C interop to roadmap and limitations
```

**Rules:**
- One logical change per commit. If you need `and` to describe it, split it.
- Each commit must leave the test suite green (or mark WIP in the message body).
- Use the imperative mood in the description: "add", "fix", "remove" — not "added" or "fixes".
- Keep descriptions under 72 characters.

---

## Adding a stdlib Module

1. Add the implementation in `lib/src/pymcu/hal/` (or `drivers/`).
2. Use `@inline` for all public methods (zero-cost abstraction rule).
3. Use `match __CHIP__.arch:` for architecture dispatch.
4. No non-ASCII characters in source (compiler lexer is ASCII-only).
5. No multiline docstrings with code examples — use `# comments` instead.
6. After editing stdlib, rsync to the local virtualenv:
   ```bash
   rsync lib/src/pymcu/ .venv/lib/python3.X/site-packages/pymcu/
   ```

## HAL Coding Rules

- No em dashes (U+2014) or other non-ASCII in source.
- No statements after `match` blocks — put defaults in `case _:` inside the match.
- Dotted names (`ClassName.ATTR`) are value patterns in `match/case`; bare names are capture patterns.
- `@inline` functions containing `asm()` with labels must delegate to a non-inline sub-helper
  (to avoid label name collision across inline expansion sites).

---

## Pull Requests

1. Fork the repository and create your branch from `main`.
2. Each commit on the branch must follow the Conventional Commits format above.
3. All integration tests must pass: `dotnet test tests/integration/PyMCU.IntegrationTests.csproj`.
4. Add a new test for any new compiler or HAL feature.
5. If the PR adds a feature, update `LANGUAGE_ROADMAP.md` and `docs/docs/roadmap.md`.
6. If the PR changes supported/unsupported features, update `docs/docs/limitations.md`.

---

## Docs Site

```bash
cd docs
pip install -r requirements.txt
mkdocs build --strict    # must pass with no warnings
mkdocs serve             # preview at http://127.0.0.1:8000
```

---

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
