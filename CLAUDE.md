# Whipsnake — Claude Code Instructions

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

Each commit must leave `dotnet test tests/integration/Whipsnake.IntegrationTests.csproj` green.

---

## Known Compiler Gotchas

- **`+=` in unrolled loops fails** — `for x in [1,2,3]: acc += x` produces wrong code.
  Use `acc = acc + x`. This applies to: `for x in list`, `for x in b"..."`,
  `for x, y in zip(...)`, `for x in reversed(...)`, `for i, x in enumerate(list)`.
- **`e: uint16 = CONST; e += 1` fails** when `e` is tracked in `constant_variables`.
  Use `e = e + 1` until the IRGenerator AugAssign path is fixed.
- **ASCII-only stdlib** — the compiler lexer rejects non-ASCII. Do not use Unicode in
  any file under `lib/src/whipsnake/`.
- **HAL match rules** — see `AGENTS.md` for dotted-name vs capture-name patterns.

---

## Workflow When Adding a Feature

1. Read the relevant compiler files before touching them.
2. Make a plan — identify all files to change (Parser, IRGenerator, AVRCodeGen, HAL, tests).
3. Implement in small commits (one logical change each).
4. After each compiler change: `cmake --build build --target whipc -j$(sysctl -n hw.ncpu)`.
5. After each stdlib change: `rsync lib/src/whipsnake/ .venv/lib/python3.X/site-packages/whipsnake/`.
6. Run integration tests: `dotnet test tests/integration/Whipsnake.IntegrationTests.csproj`.
7. Update `LANGUAGE_ROADMAP.md`, `docs/docs/roadmap.md`, and `docs/docs/limitations.md`.

---

## Memory

Auto-memory is stored at `~/.claude/projects/-Users-begeistert-Repos-pymcu/memory/`.
Update memories when you learn non-obvious compiler behaviour or project decisions.
Do not save ephemeral task state to memory.
