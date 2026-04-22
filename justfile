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

# Build the AVR backend (uses its own .csproj name until renamed).
build-backend-avr:
    dotnet publish "{{repo_root}}/extensions/pymcu-backend-avr/src/csharp/cli/PyMCU.Backend.AVR.Cli.csproj" \
        -c Release -o "{{compiler_out}}" --nologo

# ─── build-all ──────────────────────────────────────────────────────────────
# Compile the compiler and all registered backend plugin binaries.
build-all: build
    just build-backend-avr

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
install-dev: build
    uv pip install -e "{{repo_root}}" --no-build-isolation
    uv pip install --no-deps -e "{{repo_root}}/lib/"
    ln -sf "{{compiler_out}}/pymcuc" "{{repo_root}}/src/driver/pymcuc"

# ─── clean ──────────────────────────────────────────────────────────────────
# Remove all build artifacts (compiler binary, .NET obj/bin, Python dist).
clean:
    rm -rf "{{compiler_out}}" \
           "{{repo_root}}/src/compiler/bin" \
           "{{repo_root}}/src/compiler/obj" \
           "{{repo_root}}/dist"
