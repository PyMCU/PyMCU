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

5. **Each commit must leave the test suites green.** Run before committing:
   ```bash
   just test-unit        # compiler unit tests
   just test-stdlib      # stdlib suite (tests/stdlib, both front ends)
   pytest tests/driver   # driver tests
   ```
   The AVR integration suite and the language oracle corpus (`just test-oracle`,
   both front ends) live in the `pymcu-avr` repo — run them there when you touch
   codegen.

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

```bash
# Compiler unit tests (must stay green)
just test-unit

# Stdlib suite: whole programs through both front ends (must stay green)
just test-stdlib    # = .venv/bin/python -m pytest tests/stdlib -q

# Driver tests
pytest tests/driver

# 15 driver tests use the mock_toolchain fixture, which needs the external PIC
# backend; without it they skip with "pymcu-pic (external PIC backend) not
# installed". Install it with:
uv pip install --pre --no-deps pymcu-pic

# The AVR8Sharp integration suite and the language oracle corpus live in the
# pymcu-avr repo -- run them there when you touch codegen:
#   dotnet test tests/integration   # in the pymcu-avr checkout
#   just test-oracle                # oracle corpus, both front ends
#
# Before bisecting a miscompilation, run `just verify` in the pymcu-avr
# checkout: it compiles the whole corpus with PYMCU_VERIFY_IR=1 and ratchets
# the verifier's warnings. When the verifier already sees the violation the
# message names the guilty pass, which is a better bisection start than the
# wrong output.

# Install the stdlib editable once; lib/src edits are then picked up live.
# Do NOT rsync a copy into site-packages/pymcu/ — it shadows the editable .pth.
just sync-stdlib   # = uv pip install --no-deps -e lib/

# tests/stdlib/test_hal_parity.py also scans the two compat layers. Each
# layer's src dir resolves via $PYMCU_COMPAT_CIRCUITPYTHON /
# $PYMCU_COMPAT_MICROPYTHON first, then the installed pymcu_circuitpython /
# pymcu_micropython package, then the ~/Repos/<name>/src sibling checkout.
# A layer that resolves nowhere skips with the list of what was tried.
```

Add a test for every new compiler or HAL feature in `tests/unit/` (compiler
lowering) and in `tests/integration/` of the `pymcu-avr` checkout (wire-visible
behaviour).

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
- Do not use `avra`-specific assembly syntax. The AVR toolchain uses `avr-as` (GNU
  binutils 14.1.0) as the sole assembler. `avra` is deprecated and not included in any
  build pipeline.

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
