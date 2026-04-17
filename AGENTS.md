# AI Coding Agent Guidelines for PyMCU

This file is loaded automatically by AI coding assistants (Claude Code, OpenAI Codex CLI,
GitHub Copilot, Cursor, and others). It defines how agents should behave when working on
this repository.

---

## Project Summary

PyMCU is a Python-to-MCU compiler. It takes a statically-typed subset of Python and compiles
it to bare-metal firmware for microcontrollers (currently AVR/ATmega328P, PIC, RISC-V, PIO).

Key components:
- `src/compiler/` — C# compiler (`pymcuc`): Lexer, Parser, IRGenerator, AVR codegen
- 
- `lib/src/pymcu/` — Python stdlib compiled into firmware (HAL, drivers, boards)
- `src/driver/` — Python CLI driver (`pymcu build/flash/new`)
- `tests/integration/` — .NET/AVR8Sharp integration tests (must always pass)

---

## Commit Rules (MANDATORY)

Every commit you create must:

1. **Follow Conventional Commits format:**
   ```
   <type>(<scope>): <short description under 72 chars>
   ```

2. **Use one of these types:**
   - `feat` — new feature
   - `fix` — bug fix
   - `docs` — documentation only
   - `test` — tests only
   - `refactor` — no behaviour change
   - `perf` — performance improvement
   - `chore` — build, CI, dependencies
   - `style` — formatting only

3. **Use a relevant scope:**
   `avr`, `ir`, `parser`, `hal`, `driver`, `stdlib`, `drivers`, `test`, `docs`, `ci`

4. **Split features into small, focused commits.** Never bundle multiple features or
   unrelated fixes in one commit. A typical feature implementation is 2-5 commits:

   ```
   feat(parser): parse @extern decorator on function definitions
   feat(ir): emit Extern IR instruction and register extern symbols
   feat(avr): emit .extern and CALL with AVR ABI for @extern
   test(avr): add ExternCallTests for @extern C interop
   docs: add @extern to roadmap and limitations
   ```

5. **Each commit must leave the test suite green.** Run before committing:
   ```bash
   dotnet test tests/integration/PyMCU.IntegrationTests.csproj
   ```

---

## Before Modifying Code

- Read the file before editing it. Never guess at structure.
- Check `LANGUAGE_ROADMAP.md` to understand what is and is not implemented.
- Check `docs/docs/limitations.md` before adding workarounds for "missing" features — many
  are already supported.
- Read `docs/docs/contributing.md` for HAL coding rules and stdlib conventions.

---

## HAL and stdlib Rules (Critical)

Violations of these rules cause compile errors in the PyMCU compiler itself:

- **ASCII only** — no non-ASCII characters (no em dashes, no Unicode) in any `.py` file
  under `lib/src/pymcu/`. The compiler lexer is ASCII-only.
- **No multiline docstrings with code examples** — use `# comments` only.
- **No statements after `match` blocks** — always place the default in `case _:` inside the match.
- **Dotted names in match/case** (`ClassName.ATTR`) are value patterns; bare names are
  capture patterns. Use dotted names for named constants.
- **`@inline` + `asm()` with labels** — must delegate to a non-inline sub-helper to avoid
  label duplication across inline expansion sites.

---

## Testing

The integration test suite uses AVR8Sharp (cycle-accurate AVR simulator) and .NET:

```bash
# Run all tests (must stay green — currently 269 passing)
dotnet test tests/integration/PyMCU.IntegrationTests.csproj

# After any stdlib change, rsync to the virtualenv first:
rsync lib/src/pymcu/ .venv/lib/python3.X/site-packages/pymcu/
```

Add a test for every new compiler or HAL feature in `tests/integration/Tests/AVR/`.

---

## Documentation

When implementing a feature:
- Mark it complete in `LANGUAGE_ROADMAP.md` (root) **and** `docs/docs/roadmap.md`.
- If the feature was previously listed as unsupported, update `docs/docs/limitations.md`.
- Keep both roadmap files in sync — they must describe the same version history.

---

## What Not to Do

- Do not add features, refactor code, or improve unrelated code beyond what was asked.
- Do not add docstrings, comments, or type annotations to code you did not change.
- Do not add error handling for scenarios that cannot happen at compile time.
- Do not use `+=` / augmented assignment inside compile-time unrolled `for` loops
  (e.g., `for x in [1,2,3]:`, `for x, y in zip(...):`). Use `acc = acc + x` instead.
  This is a known compiler limitation where constant-variable tracking interacts with
  AugAssign emission.
- Do not change `avra`-specific assembly output without confirming the change does not
  break the avra assembler syntax (e.g., avra does not support `.extern`).

---

## Architecture Quick Reference

| Layer | Location | Notes |
|-------|----------|-------|
| Lexer / Parser / AST | `src/compiler/Frontend/` | Tokens, AST nodes |
| IR Generator | `src/compiler/IR/IRGenerator/` | Python AST → Tacky IR |
| AVR Codegen | `src/compiler/Backend/Targets/AVR/AvrCodeGen.cs` | IR → AVR asm |
| Peephole | `src/compiler/Backend/Targets/AVR/AvrPeephole.cs` | Post-codegen optimisation |
| AVR Register ABI | — | arg0→R24, arg1→R22, arg2→R20, return→R24:R25 |
| Stack | — | Y (R28:R29) base; LDD/STD Y+offset for locals |
| IO access | — | 0x20-0x3F → SBI/CBI; 0x40-0x5F → IN/OUT; >0x5F → LDS/STS |
