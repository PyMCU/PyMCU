# tests/driver/test_arena_detection.py
#
# docs/rfcs/0004-arena-allocator.md. The driver no longer guesses from the source text:
# the COMPILER reports on its stdout token stream whether a bytearray(n) actually
# allocated from the arena -- [NEEDS_ARENA] when a runtime-sized allocation met a
# missing pymcu.arena import (the compile fails), [ARENA_USED] when the allocation
# lowered against an import the program wrote itself. Either way pymcu build stages
# the generated shim + import and runs the frontend once more. A bytearray() size the
# compiler folds -- bytearray(20), bytearray(((h // 8) * w) + 1) -- is a fixed SRAM
# array: no token, no injection, no reservation.
#
# The compiler's own fold/once-rule enforcement and IR shape are covered in
# tests/unit/IR/ArenaAllocatorTests.cs -- nothing here re-tests that; this file is
# about what the DRIVER does with the tokens.

import os
import stat
import sys
import textwrap
from pathlib import Path

import pytest
from typer.testing import CliRunner
from src.driver.main import app

runner = CliRunner()


def _invoke_build(*args: str):
    return runner.invoke(app, ["build"] + list(args), catch_exceptions=False)


# ---------------------------------------------------------------------------
# arena_aware_compiler — a fake pymcuc that speaks the arena token protocol:
# fails with [NEEDS_ARENA] while the entry file carries no pymcu.arena import
# (what the real frontend does to a runtime-sized bytearray(n)), emits
# [ARENA_USED] and succeeds once the import is there -- user-written or
# injected, the compiler cannot tell them apart either.
# ---------------------------------------------------------------------------

_ARENA_AWARE_SCRIPT_POSIX = textwrap.dedent("""\
    #!/bin/sh
    entry="$1"
    echo "[PHASE_START] Lexer"
    echo "[PHASE_END] Lexer 10"
    echo "[PHASE_START] IRGen"
    if ! grep -q "import pymcu.arena" "$entry" 2>/dev/null; then
        echo "[NEEDS_ARENA]"
        echo "[BUILD_FAIL] IRGen"
        exit 1
    fi
    echo "[ARENA_USED]"
    echo "[PHASE_END] IRGen 20"
    echo "[PHASE_START] CodeGen"
    echo "[PHASE_END] CodeGen 30"
    output=""
    prev=""
    for arg in "$@"; do
        if [ "$prev" = "-o" ]; then
            output="$arg"
        fi
        prev="$arg"
    done
    if [ -n "$output" ]; then
        mkdir -p "$(dirname "$output")"
        echo "; fake asm" > "$output"
        echo "[BUILD_OK] $output"
    else
        echo "[BUILD_FAIL] CodeGen"
        exit 1
    fi
""")

_ARENA_AWARE_SCRIPT_WIN = textwrap.dedent("""\
    @echo off
    findstr /c:"import pymcu.arena" "%~1" >nul 2>&1
    if errorlevel 1 (
        echo [NEEDS_ARENA]
        echo [BUILD_FAIL] IRGen
        exit /b 1
    )
    echo [ARENA_USED]
    echo [BUILD_OK] done
""")


@pytest.fixture
def arena_aware_compiler(tmp_path, monkeypatch):
    """Install the token-speaking fake pymcuc on PATH, like mock_compiler."""
    bin_dir = tmp_path / "arena_mock_bin"
    bin_dir.mkdir(exist_ok=True)

    if sys.platform == "win32":
        exe = bin_dir / "pymcuc.cmd"
        exe.write_text(_ARENA_AWARE_SCRIPT_WIN)
    else:
        exe = bin_dir / "pymcuc"
        exe.write_text(_ARENA_AWARE_SCRIPT_POSIX)
        exe.chmod(exe.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

    monkeypatch.setenv("PATH", str(bin_dir) + os.pathsep + os.environ.get("PATH", ""))
    return exe


def _project(tmp_path: Path, main_body: str, extra_keys: str = "") -> None:
    (tmp_path / "src").mkdir(exist_ok=True)
    (tmp_path / "src" / "main.py").write_text(main_body)
    (tmp_path / "pyproject.toml").write_text(
        "[tool.pymcu]\n"
        'target = "atmega328p"\n'
        "frequency = 16000000\n"
        'sources = "src"\n'
        'entry = "main.py"\n'
        + extra_keys
    )


# ---------------------------------------------------------------------------
# Foldable bytearray(...) -- the field report: the driver used to reserve arena
# SRAM for any bytearray( call it could not fold with its narrow +/* heuristic.
# The compiler folds far more (names, //, constructors' constants), and whatever
# folds is a fixed SRAM array that never touches the arena -- so no token, no
# Arena line, no generated shim, no injected import.
# ---------------------------------------------------------------------------

class TestFoldableBytearrayIsNotArenaUsage:
    def test_no_bytearray_call_at_all_prints_no_arena_line(
            self, tmp_path, monkeypatch, mock_toolchain, mock_compiler):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, "x: int = 1\n")
        result = _invoke_build()
        assert "Arena:" not in result.output
        assert not (tmp_path / "dist" / "_generated" / "pymcu" / "arena.py").exists()

    def test_literal_sized_bytearray_prints_no_arena_line(
            self, tmp_path, monkeypatch, mock_toolchain, mock_compiler):
        # bytearray(20) folds to a fixed 20-byte SRAM array. The old lexical scan
        # reserved 20 B "exactly" for it anyway -- arena SRAM on top of the array.
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, "buf: bytearray = bytearray(20)\n")
        result = _invoke_build()
        assert "Arena:" not in result.output
        assert not (tmp_path / "dist" / "_generated" / "pymcu" / "arena.py").exists()
        generated_entry = tmp_path / "dist" / "_generated" / "main.py"
        if generated_entry.exists():
            assert "import pymcu.arena" not in generated_entry.read_text()

    def test_folded_constructor_expression_prints_no_arena_line(
            self, tmp_path, monkeypatch, mock_toolchain, mock_compiler):
        # The field report's shape: adafruit_ssd1306's
        # bytearray(((height // 8) * width) + 1) -- '//' never folded under the
        # driver's +/*-only heuristic, so 256 B was reserved for nothing. The
        # compiler folds the whole expression (the constructor's constants in the
        # report, plain literals here); a module-level `w: int = 128` would NOT
        # be this case -- a mutable global is a genuinely runtime-sized read and
        # the arena path is correct for it.
        monkeypatch.chdir(tmp_path)
        _project(tmp_path,
                 "buf: bytearray = bytearray(((32 // 8) * 128) + 1)\n")
        result = _invoke_build()
        assert "Arena:" not in result.output
        assert not (tmp_path / "dist" / "_generated" / "pymcu" / "arena.py").exists()


# ---------------------------------------------------------------------------
# Runtime-sized bytearray(n) -- the compiler emits [NEEDS_ARENA] and fails; the
# driver injects shim + import and compiles again. The line, the shim and the
# injected import all show up together.
#
# These programs read GPIOR0 for the size so the REAL pymcuc -- found ahead of
# PATH mocks in a dev checkout -- also takes the runtime-sized path; under a
# PATH-only environment the arena_aware_compiler fake plays the same protocol.
# ---------------------------------------------------------------------------

_RUNTIME_SIZED_MAIN = (
    "from pymcu.chips.atmega328p import GPIOR0\n"
    "from pymcu.types import uint16\n"
    "n: uint16 = uint16(GPIOR0.value) + 5\n"
    "buf: bytearray = bytearray(n)\n"
)


class TestRuntimeBytearrayInjectsOnCompilerRequest:
    def test_needs_arena_retries_with_injection(
            self, tmp_path, monkeypatch, mock_toolchain, arena_aware_compiler,
            unwrapped):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, _RUNTIME_SIZED_MAIN)
        result = _invoke_build()

        assert "Arena: reserved 256 B" in unwrapped(result.output)
        # The transient missing-import diagnostic from the first attempt is not
        # shown -- the retry answers it.
        assert "needs the pymcu.arena allocator" not in unwrapped(result.output)

        generated_entry = tmp_path / "dist" / "_generated" / "main.py"
        assert generated_entry.exists()
        assert "import pymcu.arena as _pymcu_arena" in generated_entry.read_text()

        shim = tmp_path / "dist" / "_generated" / "pymcu" / "arena.py"
        assert shim.exists()
        assert "ARENA_SIZE: uint16 = 256" in shim.read_text()

    def test_arena_size_override_is_reported_and_used(
            self, tmp_path, monkeypatch, mock_toolchain, arena_aware_compiler,
            unwrapped):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, _RUNTIME_SIZED_MAIN,
                 extra_keys="arena_size = 400\n")
        result = _invoke_build()
        assert "Arena: reserved 400 B" in unwrapped(result.output)
        shim = tmp_path / "dist" / "_generated" / "pymcu" / "arena.py"
        assert "ARENA_SIZE: uint16 = 400" in shim.read_text()

    def test_explicit_import_still_gets_the_shim(
            self, tmp_path, monkeypatch, mock_toolchain, arena_aware_compiler,
            unwrapped):
        # A program that writes `import pymcu.arena` itself compiles on the first
        # pass -- but against the shipped module's ARENA_SIZE of 0. The compiler's
        # [ARENA_USED] token tells the driver to stage the shim and recompile.
        monkeypatch.chdir(tmp_path)
        _project(tmp_path,
                 "import pymcu.arena as _arena_observe\n" + _RUNTIME_SIZED_MAIN)
        result = _invoke_build()
        assert "Arena: reserved 256 B" in unwrapped(result.output)
        shim = tmp_path / "dist" / "_generated" / "pymcu" / "arena.py"
        assert shim.exists()
        assert "ARENA_SIZE: uint16 = 256" in shim.read_text()

    def test_a_real_compile_error_is_not_retried_as_arena(
            self, tmp_path, monkeypatch, mock_toolchain, tmp_path_factory):
        # A compile that fails WITHOUT the token is an ordinary error: no
        # injection, no retry, the diagnostic passes through. The PATH fake
        # always fails; the program itself is uncompilable so the real pymcuc
        # -- which shadows PATH in a dev checkout -- fails the same way.
        bin_dir = tmp_path_factory.mktemp("fail_bin")
        if sys.platform == "win32":
            exe = bin_dir / "pymcuc.cmd"
            exe.write_text("@echo off\necho [BUILD_FAIL] IRGen\nexit /b 1\n")
        else:
            exe = bin_dir / "pymcuc"
            exe.write_text("#!/bin/sh\necho '[BUILD_FAIL] IRGen'\nexit 1\n")
            exe.chmod(exe.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
        monkeypatch.setenv("PATH", str(bin_dir) + os.pathsep + os.environ.get("PATH", ""))
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, "x: int = not_a_defined_name\n")
        result = _invoke_build()
        assert result.exit_code != 0
        assert "Arena:" not in result.output
        assert not (tmp_path / "dist" / "_generated" / "pymcu" / "arena.py").exists()
