# justfile — PyMCU build orchestration
# Requires: just (brew install just), dotnet >=10, uv

set shell := ["bash", "-c"]

repo_root    := justfile_directory()
compiler_out := repo_root / "build/bin"

# ─── Default ────────────────────────────────────────────────────────────────
default:
    @just --list

# ─── build ──────────────────────────────────────────────────────────────────
# Compile the .NET compiler and publish to build/bin/ (version-agnostic path).
build:
    dotnet publish "{{repo_root}}/src/compiler/PyMCU.csproj" \
        -c Release -o "{{compiler_out}}" --nologo

# ─── build-backend ──────────────────────────────────────────────────────────
# Compile a single backend plugin binary. Usage: just build-backend avr
# Expects: extensions/pymcu-backend-{name}/src/csharp/cli/PyMCU.Backend.Cli.csproj
# (The AVR backend keeps its legacy name PyMCU.Backend.AVR.Cli.csproj — use build-backend-avr.)
build-backend name:
    dotnet publish "{{repo_root}}/extensions/pymcu-backend-{{name}}/src/csharp/cli/PyMCU.Backend.Cli.csproj" \
        -c Release -o "{{compiler_out}}" --nologo

# Named backend shorthands (each has its own .csproj name).
build-backend-avr:
    dotnet publish "{{repo_root}}/extensions/pymcu-avr/src/csharp/cli/PyMCU.Backend.AVR.Cli.csproj" \
        -c Release -o "{{compiler_out}}" --nologo

build-backend-riscv:
    dotnet publish "{{repo_root}}/extensions/pymcu-backend-riscv/src/csharp/cli/PyMCU.Backend.RiscV.Cli.csproj" \
        -c Release -o "{{compiler_out}}" --nologo

build-backend-pio:
    dotnet publish "{{repo_root}}/extensions/pymcu-backend-pio/src/csharp/cli/PyMCU.Backend.PIO.Cli.csproj" \
        -c Release -o "{{compiler_out}}" --nologo

# ─── build-all ──────────────────────────────────────────────────────────────
# Compile the compiler and all registered backend plugin binaries.
build-all: build
    just build-backend-avr
    just build-backend-riscv
    just build-backend-pio

# ─── test-backend ───────────────────────────────────────────────────────────
# Run unit and integration tests for a backend. Usage: just test-backend avr
test-backend name: (build-backend name)
    dotnet test "{{repo_root}}/extensions/pymcu-backend-{{name}}/tests/unit/" \
        --logger "console;verbosity=normal" --nologo
    dotnet test "{{repo_root}}/extensions/pymcu-backend-{{name}}/tests/integration/" \
        --logger "console;verbosity=normal" \
        --blame-hang-timeout 120s --nologo \
        -- NUnit.NumberOfTestWorkers=1

# ─── test ───────────────────────────────────────────────────────────────────
# Run unit tests then integration tests (requires build first).
test: build
    just test-unit
    just test-integration

# ─── test-unit ──────────────────────────────────────────────────────────────
# Run unit tests only.
test-unit:
    dotnet test "{{repo_root}}/tests/unit/PyMCU.Tests.csproj" \
        --logger "console;verbosity=normal" --nologo

# ─── test-integration ───────────────────────────────────────────────────────
# Run integration tests only (requires build first).
test-integration: build
    dotnet test "{{repo_root}}/tests/integration/PyMCU.IntegrationTests.csproj" \
        --logger "console;verbosity=normal" \
        --blame-hang-timeout 120s --nologo \
        -- NUnit.NumberOfTestWorkers=1

# ─── test-driver-ci ─────────────────────────────────────────────────────────
# Run the driver tests against CI's dependency set, in a throwaway venv.
#
# The dev venv has every backend and flavor installed; CI installs a handful of
# packages and nothing else. Tests that quietly depend on the difference pass
# here and fail there -- which is exactly how main went red once. Keep the pip
# lines in step with the "Install driver dependencies" step in ci.yml.
test-driver-ci:
    rm -rf "{{repo_root}}/.venv-ci"
    python3 -m venv "{{repo_root}}/.venv-ci"
    "{{repo_root}}/.venv-ci/bin/pip" -q install --upgrade pip
    "{{repo_root}}/.venv-ci/bin/pip" -q install pytest pytest-mock tomlkit rich typer questionary
    "{{repo_root}}/.venv-ci/bin/pip" -q install "{{repo_root}}/extensions/pymcu-sdk"
    "{{repo_root}}/.venv-ci/bin/pip" -q install --pre --no-deps pymcu-pic
    cd "{{repo_root}}" && "{{repo_root}}/.venv-ci/bin/python" -m pytest tests/driver/ -q

# ─── build-stdlib ───────────────────────────────────────────────────────────
# Build the pymcu-stdlib wheel into lib/dist/.
build-stdlib:
    cd "{{repo_root}}/lib" && uv build

# ─── sync-stdlib ────────────────────────────────────────────────────────────
# Sync the stdlib source tree into the active .venv (mirrors rsync workflow).
sync-stdlib:
    uv pip install --no-deps -e "{{repo_root}}/lib/"

# ─── package ────────────────────────────────────────────────────────────────
# Build the PyPI wheel (hatch_build.py copies pymcuc into src/driver/ first).
package: build
    uv build --out-dir "{{repo_root}}/dist" "{{repo_root}}"

# ─── install-dev ────────────────────────────────────────────────────────────
# Editable install: compiler binary is symlinked so driver finds it immediately.
install-dev: build link-dev
    uv pip install -e "{{repo_root}}" --no-build-isolation
    uv pip install --no-deps -e "{{repo_root}}/lib/"

# ─── link-dev ───────────────────────────────────────────────────────────────
# Point every packaged binary at the current build output.
#
# Both the driver and each backend plugin look for their binary *inside* the
# Python package first, and a real file there wins over build/bin. A stale copy
# is therefore invisible: the build succeeds and silently uses an old compiler.
# Run this after any dotnet publish, or whenever a wheel build leaves a copy
# behind (the hatch hook writes one next to the module).
link-dev:
    ln -sf "{{compiler_out}}/pymcuc" "{{repo_root}}/src/driver/pymcuc"
    @for pkg in "{{repo_root}}"/extensions/*/src/python/pymcu/backend/*/; do \
        name="pymcuc-$(basename "$pkg")"; \
        if [ -f "{{compiler_out}}/$name" ]; then \
            ln -sf "{{compiler_out}}/$name" "$pkg$name"; \
            echo "linked $name -> {{compiler_out}}/$name"; \
        fi; \
    done

# ─── clean ──────────────────────────────────────────────────────────────────
# Remove all build artifacts (compiler binary, .NET obj/bin, Python dist).
clean:
    rm -rf "{{compiler_out}}" \
           "{{repo_root}}/src/compiler/bin" \
           "{{repo_root}}/src/compiler/obj" \
           "{{repo_root}}/dist"
