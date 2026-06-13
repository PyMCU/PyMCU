<!--
Thanks for contributing to PyMCU! Please read CONTRIBUTING.md and AGENTS.md
first. Keep PRs focused — split unrelated changes into separate PRs/commits.
-->

## Summary

<!-- What does this PR do and why? One or two sentences. -->

Fixes #<!-- issue number, or remove this line if N/A -->

## Type of change

- [ ] Bug fix (compiler, codegen, runtime, or HAL)
- [ ] New feature (language, HAL/driver, CLI, tooling)
- [ ] New chip / board / peripheral support
- [ ] Refactor / cleanup (no behavior change)
- [ ] Documentation
- [ ] Other:

## Area affected

<!-- Tick all that apply -->

- [ ] Compiler / code generation (`src/compiler/`)
- [ ] CLI driver (`src/driver/`)
- [ ] Standard library & HAL (`lib/src/pymcu/`)
- [ ] A backend (AVR / PIC / RISC-V / PIO)
- [ ] CircuitPython / MicroPython compatibility layer
- [ ] IDE integration (JetBrains / VS Code)
- [ ] Examples / docs / CI

## How it was tested

<!--
Describe what you ran. For most changes, the integration suite must be green:
    dotnet test tests/integration/PyMCU.IntegrationTests.csproj
-->

- [ ] `dotnet test tests/integration/PyMCU.IntegrationTests.csproj` passes
- [ ] Verified on a real board or the AVR8Sharp simulator (describe below)

<!-- Notes, output, or board/wiring used: -->

## Checklist

- [ ] Commits follow [Conventional Commits](https://www.conventionalcommits.org/) and are small and focused.
- [ ] After a compiler change, I rebuilt: `dotnet publish src/compiler/PyMCU.csproj -c Release -o build/bin --nologo`.
- [ ] The stdlib is installed editable (`just sync-stdlib`); I did **not** rsync a copy into `site-packages/pymcu/` (a physical copy shadows the editable `.pth`).
- [ ] Stdlib sources are **ASCII-only** (no em dashes / non-ASCII — the lexer rejects them).
- [ ] I fixed root causes in the compiler rather than working around codegen bugs in Python source.
- [ ] New source files carry the MIT SPDX license header.
- [ ] I updated the docs where relevant: `LANGUAGE_ROADMAP.md`, `docs/language/roadmap.md`, `docs/language/limitations.md`.
