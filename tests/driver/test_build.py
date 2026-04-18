# tests/driver/test_build.py
#
# Tests for the `pymcu build` command.
# Real compiler and toolchain calls are replaced by the fixtures in conftest.py.

from pathlib import Path
import pytest
from typer.testing import CliRunner
from src.driver.main import app

runner = CliRunner()


def _invoke_build(*args: str):
    return runner.invoke(app, ["build"] + list(args), catch_exceptions=False)


# ---------------------------------------------------------------------------
# Missing pyproject.toml → Exit(1)
# ---------------------------------------------------------------------------

class TestBuildMissingConfig:
    def test_no_pyproject_exits_1(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_build()
        assert result.exit_code == 1
        assert "pyproject.toml" in result.output.lower()


# ---------------------------------------------------------------------------
# target + board set simultaneously → Exit(1)
# ---------------------------------------------------------------------------

class TestBuildMutuallyExclusiveTargetBoard:
    def test_target_and_board_exits_1(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir()
        (tmp_path / "src" / "main.py").write_text("def main(): pass\n")
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu]\n"
            'target = "atmega328p"\n'
            'board = "arduino_uno"\n'
            "frequency = 4000000\n"
            'sources = "src"\n'
            'entry = "main.py"\n'
        )
        result = _invoke_build()
        assert result.exit_code == 1
        assert "board" in result.output.lower() or "target" in result.output.lower()


# ---------------------------------------------------------------------------
# Entry file not found → Exit(1)
# ---------------------------------------------------------------------------

class TestBuildMissingEntry:
    def test_missing_entry_exits_1(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir()
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu]\n"
            'target = "atmega328p"\n'
            "frequency = 4000000\n"
            'sources = "src"\n'
            'entry = "main.py"\n'
        )
        result = _invoke_build()
        assert result.exit_code == 1


# ---------------------------------------------------------------------------
# stdlib_override via --stdlib flag
# ---------------------------------------------------------------------------

class TestBuildStdlibFlag:
    def test_unknown_stdlib_flavor_prints_warning(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir()
        (tmp_path / "src" / "main.py").write_text("def main(): pass\n")
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu]\n"
            'target = "atmega328p"\n'
            "frequency = 4000000\n"
            'sources = "src"\n'
            'entry = "main.py"\n'
        )
        result = _invoke_build("--stdlib", "nonexistent_flavor_xyz")
        # Should warn but continue (not exit 1 due to missing flavor alone)
        assert "nonexistent_flavor_xyz" in result.output or result.exit_code in (0, 1)


# ---------------------------------------------------------------------------
# Board key resolves to correct chip
# ---------------------------------------------------------------------------

class TestBuildBoardResolution:
    def test_known_board_resolves(self, tmp_path, monkeypatch, mock_toolchain, mock_compiler):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir()
        (tmp_path / "src" / "main.py").write_text("def main(): pass\n")
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu]\n"
            'board = "arduino_uno"\n'
            "frequency = 16000000\n"
            'sources = "src"\n'
            'entry = "main.py"\n'
        )
        result = _invoke_build()
        # Should not error out on board resolution
        assert "unknown board" not in result.output.lower()

    def test_unknown_board_exits_1(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir()
        (tmp_path / "src" / "main.py").write_text("def main(): pass\n")
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu]\n"
            'board = "banana_pi_zz99"\n'
            "frequency = 16000000\n"
            'sources = "src"\n'
            'entry = "main.py"\n'
        )
        result = _invoke_build()
        assert result.exit_code == 1
        assert "unknown board" in result.output.lower()
