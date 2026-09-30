# tests/driver/test_round2_detection.py
#
# P2 AVR gaps bundle, item 1 / RFC 0014 decision 5. The driver no longer scans the
# source text for round(x, n): that regex could not tell a real call to the builtin
# from the same spelling in a comment or a user's own `def round` (which now shadows
# the builtin, like every other Python builtin PyMCU provides -- RFC 0014 decision 4).
# The COMPILER reports on its stdout token stream whether a call it actually resolved
# to the round(x, n)-on-a-float builtin needs the pymcu.round2 helper: [NEEDS_ROUND2]
# when it does and the import is missing (the compile fails), nothing when the helper
# is not needed (an int x, or the call is shadowed). pymcu build answers the token by
# injecting the import and running the frontend once more -- the same protocol
# tests/driver/test_arena_detection.py exercises for pymcu.arena.
#
# The compiler's own shadowing and round(x, n) lowering are covered in
# tests/unit/IR/ -- nothing here re-tests that; this file is about what the DRIVER
# does with the token.

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
# round2_aware_compiler -- a fake pymcuc that speaks the round2 token protocol:
# fails with [NEEDS_ROUND2] while the entry file carries no pymcu.round2 import,
# succeeds once the import is there -- user-written or injected, the compiler
# cannot tell them apart either, and it never asks twice.
# ---------------------------------------------------------------------------

_ROUND2_AWARE_SCRIPT_POSIX = textwrap.dedent("""\
    #!/bin/sh
    entry="$1"
    echo "[PHASE_START] Lexer"
    echo "[PHASE_END] Lexer 10"
    echo "[PHASE_START] IRGen"
    if ! grep -q "import pymcu.round2" "$entry" 2>/dev/null; then
        echo "[NEEDS_ROUND2]"
        echo "[BUILD_FAIL] IRGen"
        exit 1
    fi
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

_ROUND2_AWARE_SCRIPT_WIN = textwrap.dedent("""\
    @echo off
    findstr /c:"import pymcu.round2" "%~1" >nul 2>&1
    if errorlevel 1 (
        echo [NEEDS_ROUND2]
        echo [BUILD_FAIL] IRGen
        exit /b 1
    )
    echo [BUILD_OK] done
""")


@pytest.fixture
def round2_aware_compiler(tmp_path, monkeypatch):
    """Install the token-speaking fake pymcuc on PATH, like arena_aware_compiler."""
    bin_dir = tmp_path / "round2_mock_bin"
    bin_dir.mkdir(exist_ok=True)

    if sys.platform == "win32":
        exe = bin_dir / "pymcuc.cmd"
        exe.write_text(_ROUND2_AWARE_SCRIPT_WIN)
    else:
        exe = bin_dir / "pymcuc"
        exe.write_text(_ROUND2_AWARE_SCRIPT_POSIX)
        exe.chmod(exe.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

    monkeypatch.setenv("PATH", str(bin_dir) + os.pathsep + os.environ.get("PATH", ""))
    return exe


def _project(tmp_path: Path, main_body: str) -> None:
    (tmp_path / "src").mkdir(exist_ok=True)
    (tmp_path / "src" / "main.py").write_text(main_body)
    (tmp_path / "pyproject.toml").write_text(
        "[tool.pymcu]\n"
        'target = "atmega328p"\n'
        "frequency = 16000000\n"
        'sources = "src"\n'
        'entry = "main.py"\n'
    )


# ---------------------------------------------------------------------------
# No real call to round(x, n): a comment mentioning it, or the spelling sitting in a
# string, used to be enough to match the old regex. Neither reaches the compiler as a
# call, so the mock (which always succeeds without the token) never sees
# [NEEDS_ROUND2] and no import is injected.
# ---------------------------------------------------------------------------

class TestNoRealCallInjectsNothing:
    def test_comment_mentioning_round_does_not_inject(
            self, tmp_path, monkeypatch, mock_toolchain, mock_compiler):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, "x: int = 1  # round(x, 2) just a comment\n")
        result = _invoke_build()
        generated_entry = tmp_path / "dist" / "_generated" / "main.py"
        if generated_entry.exists():
            assert "import pymcu.round2" not in generated_entry.read_text()

    def test_string_mentioning_round_does_not_inject(
            self, tmp_path, monkeypatch, mock_toolchain, mock_compiler):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, 's: str = "round(x, 2)"\n')
        result = _invoke_build()
        generated_entry = tmp_path / "dist" / "_generated" / "main.py"
        if generated_entry.exists():
            assert "import pymcu.round2" not in generated_entry.read_text()

    def test_own_round_definition_does_not_inject(
            self, tmp_path, monkeypatch, mock_toolchain, mock_compiler):
        # A user's own def round(a, b) shadows the builtin (RFC 0014 decision 4):
        # every call in the file resolves to it, never to pymcu.round2.
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, (
            "def round(a: int, b: int) -> int:\n"
            "    return a + b\n"
            "x: int = round(1, 2)\n"
        ))
        result = _invoke_build()
        generated_entry = tmp_path / "dist" / "_generated" / "main.py"
        if generated_entry.exists():
            assert "import pymcu.round2" not in generated_entry.read_text()


# ---------------------------------------------------------------------------
# round(x, n) on a run-time float -- the compiler emits [NEEDS_ROUND2] and fails; the
# driver injects the import and compiles again.
# ---------------------------------------------------------------------------

_RUNTIME_FLOAT_ROUND_MAIN = (
    "from pymcu.chips.atmega328p import GPIOR0\n"
    "from pymcu.types import uint16\n"
    "n: uint16 = uint16(GPIOR0.value)\n"
    "x: float = 1.0 + float(n)\n"
    "y: float = round(x, 2)\n"
)


class TestRuntimeRoundInjectsOnCompilerRequest:
    def test_needs_round2_retries_with_injection(
            self, tmp_path, monkeypatch, mock_toolchain, round2_aware_compiler,
            unwrapped):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, _RUNTIME_FLOAT_ROUND_MAIN)
        result = _invoke_build()

        # The transient missing-import diagnostic from the first attempt is not
        # shown -- the retry answers it.
        assert "needs the pymcu.round2 helper" not in unwrapped(result.output)

        generated_entry = tmp_path / "dist" / "_generated" / "main.py"
        assert generated_entry.exists()
        assert "import pymcu.round2 as _pymcu_round2" in generated_entry.read_text()

    def test_a_real_compile_error_is_not_retried_as_round2(
            self, tmp_path, monkeypatch, mock_toolchain, tmp_path_factory):
        # A compile that fails WITHOUT the token is an ordinary error: no
        # injection, no retry, the diagnostic passes through.
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
        generated_entry = tmp_path / "dist" / "_generated" / "main.py"
        if generated_entry.exists():
            assert "import pymcu.round2" not in generated_entry.read_text()
