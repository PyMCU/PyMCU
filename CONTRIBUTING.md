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
   dotnet publish src/compiler/PyMCU.csproj -c Release -o build/bin --nologo
   ```

4. **Run integration tests:**
   ```bash
   dotnet test tests/integration/PyMCU.IntegrationTests.csproj
   ```

All tests must stay green. Add a new test in `tests/integration/Tests/AVR/` for any new
compiler feature before merging.

### Driver tests run against CI's dependency set

`just test-driver-ci` builds a throwaway venv with exactly the packages CI installs and
runs `tests/driver/` in it. `just test` includes it, and it is the one that counts before
you push.

A development checkout has every backend and stdlib flavor installed; CI installs a short
list and nothing else. A test that reads the difference — asserting that a package appears
in a table, or importing the build backend — passes here and fails on all four runners.
That has happened three times, so if you write a test that depends on what is installed,
supply the environment yourself with fakes rather than asking the machine.

### Building on Windows

Step 3 needs three things that are easy to get subtly wrong. All of them were hit
setting up a Windows 11 ARM64 machine, and none of them fails with a message that
points at the real cause.

- **A .NET SDK from the 10.0.1xx band.** `winget` currently installs 10.0.400, which
  the AOT publish rejects. Check with `dotnet --list-sdks` and install the matching
  band from the [.NET downloads page](https://dotnet.microsoft.com/download) if it
  is missing.
- **Visual Studio Build Tools with the C++ workload for your architecture** — on an
  ARM64 machine that means the ARM64 C++ tools specifically, not just the x64 ones.
  Native AOT links with the platform linker, so the compiler alone is not enough.
- **The directory containing `vswhere.exe` on `PATH`.** ILCompiler invokes it by bare
  name. Without it the build fails as `MSB3073`, which reads like a linker error and
  sends you looking in the wrong place entirely. It normally lives in
  `C:\Program Files (x86)\Microsoft Visual Studio\Installer`.

An architecture note while you are here: building with an emulated x64 Python on an
ARM64 machine used to produce an ARM64 binary inside a wheel tagged `win_amd64`. The
build now refuses to do that and tells you which half to fix, but the shortest path
is to install a Python that matches the machine.

---

## Repository Layout

```
pymcu/
  src/compiler/           # C# compiler (pymcuc)
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
6. Install the stdlib editable once; after that, `lib/src/pymcu/` edits are live:
   ```bash
   just sync-stdlib   # = uv pip install --no-deps -e lib/
   ```
   Do not rsync a copy into `site-packages/pymcu/` — a physical copy there shadows
   the editable `.pth` and your stdlib edits silently stop taking effect.

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

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
