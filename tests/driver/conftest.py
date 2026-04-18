# tests/driver/conftest.py
#
# Shared fixtures for PyMCU driver tests.
# All tests use only mocks so no real toolchain, compiler, or hardware is
# required.  Tests are safe to run on Linux, macOS, and Windows in CI.

import os
import sys
import stat
import textwrap
import shutil
from pathlib import Path
import pytest


# ---------------------------------------------------------------------------
# tmp_project — creates a minimal pyproject.toml + entry file in a tmp_path
# ---------------------------------------------------------------------------

MINIMAL_PYPROJECT = """\
[project]
name = "test-project"
version = "0.1.0"
dependencies = []

[tool.pymcu]
target = "{chip}"
frequency = 4000000
sources = "src"
entry = "main.py"
"""

MINIMAL_MAIN_PY = """\
from pymcu.chips.{chip} import PORTB, RB0


def main():
    PORTB[RB0] = 1
"""


@pytest.fixture
def tmp_project(tmp_path, monkeypatch):
    """
    Create a minimal PyMCU project in *tmp_path* and chdir into it.
    Returns a factory callable:  project_dir = tmp_project("atmega328p")
    """
    def _make(chip: str = "atmega328p") -> Path:
        (tmp_path / "src").mkdir(parents=True, exist_ok=True)
        (tmp_path / "pyproject.toml").write_text(
            MINIMAL_PYPROJECT.format(chip=chip)
        )
        (tmp_path / "src" / "main.py").write_text(
            MINIMAL_MAIN_PY.format(chip=chip)
        )
        monkeypatch.chdir(tmp_path)
        return tmp_path

    return _make


# ---------------------------------------------------------------------------
# mock_compiler — fake pymcuc that emits the expected structured tokens
# ---------------------------------------------------------------------------

_COMPILER_SCRIPT_POSIX = textwrap.dedent("""\
    #!/bin/sh
    # Fake pymcuc — emits structured progress tokens then writes a golden .asm
    echo "[PHASE_START] Lexer"
    echo "[PHASE_END] Lexer 10"
    echo "[PHASE_START] Parser"
    echo "[PHASE_END] Parser 15"
    echo "[PHASE_START] IRGen"
    echo "[PHASE_END] IRGen 20"
    echo "[PHASE_START] CodeGen"
    echo "[PHASE_END] CodeGen 30"
    # Find -o argument
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

_COMPILER_SCRIPT_WIN = textwrap.dedent("""\
    @echo off
    echo [PHASE_START] Lexer
    echo [PHASE_END] Lexer 10
    echo [BUILD_OK] done
""")


@pytest.fixture
def mock_compiler(tmp_path, monkeypatch):
    """
    Install a fake pymcuc script into *tmp_path/bin/* and prepend it to PATH.
    The fake compiler writes a minimal .asm file to its -o argument.
    Returns the path to the fake binary.
    """
    bin_dir = tmp_path / "mock_bin"
    bin_dir.mkdir(exist_ok=True)

    if sys.platform == "win32":
        exe = bin_dir / "pymcuc.cmd"
        exe.write_text(_COMPILER_SCRIPT_WIN)
    else:
        exe = bin_dir / "pymcuc"
        exe.write_text(_COMPILER_SCRIPT_POSIX)
        exe.chmod(exe.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

    # Prepend bin_dir to PATH so shutil.which finds the fake binary
    monkeypatch.setenv("PATH", str(bin_dir) + os.pathsep + os.environ.get("PATH", ""))
    return exe


# ---------------------------------------------------------------------------
# mock_toolchain — patches AvrgasToolchain / GputilsToolchain
# ---------------------------------------------------------------------------

@pytest.fixture
def mock_toolchain(monkeypatch, tmp_path):
    """
    Patch ExternalToolchain so is_cached() returns True and assemble() copies
    a golden hex file to dist/firmware.hex without invoking any real binary.
    Returns a dict with 'is_cached_calls' and 'assemble_calls' counters.
    """
    calls = {"is_cached": 0, "install": 0, "assemble": 0}

    def fake_is_cached(self):
        calls["is_cached"] = calls["is_cached"] + 1
        return True

    def fake_install(self):
        calls["install"] = calls["install"] + 1

    def fake_assemble(self, asm_file, output_file=None):
        calls["assemble"] = calls["assemble"] + 1
        hex_out = Path(asm_file).with_suffix(".hex")
        hex_out.write_text(":00000001FF\n")  # minimal valid HEX EOF record
        return hex_out

    from src.driver.toolchains import avrgas, gputils
    monkeypatch.setattr(avrgas.AvrgasToolchain, "is_cached", fake_is_cached)
    monkeypatch.setattr(avrgas.AvrgasToolchain, "install", fake_install)
    monkeypatch.setattr(avrgas.AvrgasToolchain, "assemble", fake_assemble)
    monkeypatch.setattr(gputils.GputilsToolchain, "is_cached", fake_is_cached)
    monkeypatch.setattr(gputils.GputilsToolchain, "install", fake_install)
    monkeypatch.setattr(gputils.GputilsToolchain, "assemble", fake_assemble)
    return calls


# ---------------------------------------------------------------------------
# mock_download — patches urllib so no real network calls are made
# ---------------------------------------------------------------------------

@pytest.fixture
def mock_download(monkeypatch):
    """
    Patch urllib.request.urlretrieve to write a minimal 1-byte file.
    Also patches CacheableTool.verify_sha256 to always return True.
    """
    import urllib.request

    def fake_retrieve(url, dest, reporthook=None):
        Path(dest).parent.mkdir(parents=True, exist_ok=True)
        Path(dest).write_bytes(b"\x00")
        if reporthook:
            reporthook(1, 1, 1)

    def fake_verify(self, path, expected):
        return True

    monkeypatch.setattr(urllib.request, "urlretrieve", fake_retrieve)
    from src.driver.core.base_tool import CacheableTool
    monkeypatch.setattr(CacheableTool, "verify_sha256", fake_verify)
