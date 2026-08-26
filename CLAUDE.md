# PyMCU — Claude Code Instructions

This file is auto-loaded by Claude Code. It extends `AGENTS.md` with Claude-specific guidance.

Read `AGENTS.md` first — it defines the project summary, commit rules, HAL constraints,
and architecture reference that apply to all AI coding tools.

---

## Commit Discipline (Repeat for Emphasis)

Every feature implementation **must** be split into small, focused commits using
Conventional Commits format. Do not batch unrelated changes.

```
feat(parser): parse @extern on function definitions
feat(ir): emit Extern IR node for @extern-decorated functions
feat(avr): emit .extern directive and CALL for @extern calls
test(avr): add ExternCallTests — basic @extern C interop
docs: mark @extern as implemented in roadmap and limitations
```

Each commit must leave the test suites that exist in this repo green: `just test-unit`
for the compiler and `pytest tests/driver` for the driver. The AVR integration suite
lives in the `pymcu-avr` repo since the split — run it there when you touch codegen.

---

## Known Compiler Gotchas

- **ASCII-only stdlib** — the compiler lexer rejects non-ASCII. Do not use Unicode in
  any file under `lib/src/pymcu/`.
- **HAL match rules** — see `AGENTS.md` for dotted-name vs capture-name patterns.
- **No workarounds for compiler bugs** — If a language feature generates wrong code,
  fix the root cause in the C# compiler (Parser, IRGenerator, Optimizer, AVRCodeGen).
  Do NOT write Python source code that exploits broken codegen to produce the right output.
  Workarounds are invisible to users, break when the bug is fixed, and make the stdlib
  harder to read and reason about.

---

## Workflow When Adding a Feature

1. Read the relevant compiler files before touching them.
2. Make a plan — identify all files to change (Parser, IRGenerator, AVRCodeGen, HAL, tests).
3. Implement in small commits (one logical change each).
4. After each compiler change: `dotnet publish src/compiler/PyMCU.csproj -c Release -o build/bin --nologo`.
5. Install the stdlib editable once: `just sync-stdlib` (`uv pip install --no-deps -e lib/`). After that, `lib/src/pymcu/` edits are picked up live — no per-change copy. Do NOT rsync a copy of `lib/src/pymcu/` into `site-packages/pymcu/`: a physical copy there shadows the editable `.pth` and your edits silently stop taking effect.
6. Run the tests: `just test-unit` and `pytest tests/driver`.
7. Update `LANGUAGE_ROADMAP.md`, `docs/language/roadmap.md`, and `docs/language/limitations.md`.

---

## Memory

Auto-memory is stored at `~/.claude/projects/-Users-begeistert-Repos-pymcu/memory/`.
Update memories when you learn non-obvious compiler behaviour or project decisions.
Do not save ephemeral task state to memory.
