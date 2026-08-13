# tests/driver/test_new_headless.py
#
# `pymcu new` when there is no usable terminal.
#
# From the Windows 11 ARM report: the command died with "No Windows console
# found" as soon as stdout was redirected. The guard only checked
# sys.stdin.isatty(), but prompt_toolkit opens the output console too. The
# ordering made it worse than a bad error message: every file was written, the
# crash landed on the git question, and the run exited 1 without a summary and
# without ever offering to install dependencies -- a half-made project.

import sys
from unittest.mock import patch

import pytest
import tomlkit
import typer
from typer.testing import CliRunner

from src.driver.commands.new import _confirm, _interactive, _select
from src.driver.main import app

runner = CliRunner()


class _FakeStream:
    def __init__(self, tty: bool):
        self._tty = tty

    def isatty(self) -> bool:
        return self._tty


class TestInteractiveDetection:
    def test_needs_both_ends(self):
        for stdin_tty, stdout_tty, expected in [
            (True, True, True),
            (True, False, False),   # the Windows case: stdout redirected
            (False, True, False),
            (False, False, False),
        ]:
            with patch.object(sys, "stdin", _FakeStream(stdin_tty)), \
                 patch.object(sys, "stdout", _FakeStream(stdout_tty)):
                assert _interactive() is expected, (stdin_tty, stdout_tty)

    def test_survives_streams_without_isatty(self):
        with patch.object(sys, "stdout", object()):
            assert _interactive() is False


class TestConfirm:
    def test_headless_uses_the_non_interactive_answer(self):
        with patch("src.driver.commands.new._interactive", return_value=False):
            assert _confirm("go?", default=True, non_interactive=False) is False
            assert _confirm("go?", default=False, non_interactive=True) is True

    def test_headless_falls_back_to_default_when_unspecified(self):
        with patch("src.driver.commands.new._interactive", return_value=False):
            assert _confirm("go?", default=True) is True

    def test_a_broken_prompt_does_not_abort(self):
        # isatty can claim a terminal that prompt_toolkit still cannot drive.
        with patch("src.driver.commands.new._interactive", return_value=True), \
             patch("questionary.confirm", side_effect=Exception("No Windows console found")):
            assert _confirm("go?", default=True, non_interactive=False) is False


class TestSelect:
    def test_headless_refuses_rather_than_guessing(self):
        # Inventing a board would silently scaffold the wrong MCU.
        with patch("src.driver.commands.new._interactive", return_value=False):
            with pytest.raises(typer.Exit) as excinfo:
                _select("Board:", ["a", "b"], flag="--board")
        assert excinfo.value.exit_code == 1

    def test_headless_error_names_the_flag_to_pass(self, capsys):
        with patch("src.driver.commands.new._interactive", return_value=False):
            with pytest.raises(typer.Exit):
                _select("Board:", ["a", "b"], flag="--board")
        assert "--board" in capsys.readouterr().out


class TestScaffoldWithoutATerminal:
    """CliRunner gives no tty, which is exactly the reported situation."""

    def _run(self, tmp_path, monkeypatch, *extra):
        monkeypatch.chdir(tmp_path)
        return runner.invoke(
            app,
            ["new", "blinky", "--board", "arduino_uno", "--stdlib", "micropython",
             "--pkg-manager", "uv", *extra],
            catch_exceptions=False,
        )

    def test_completes_and_exits_zero(self, tmp_path, monkeypatch):
        result = self._run(tmp_path, monkeypatch)
        assert result.exit_code == 0, result.output

    def test_prints_the_summary(self, tmp_path, monkeypatch, unwrapped):
        # The crash used to swallow it entirely.
        result = self._run(tmp_path, monkeypatch)
        assert "created successfully" in unwrapped(result.output)
        assert "atmega328p" in result.output

    def test_leaves_a_complete_project(self, tmp_path, monkeypatch):
        self._run(tmp_path, monkeypatch)
        project = tmp_path / "blinky"
        for expected in ("pyproject.toml", "src/main.py", ".vscode/tasks.json"):
            assert (project / expected).exists(), expected

    def test_says_how_to_install_the_dependencies(self, tmp_path, monkeypatch, unwrapped):
        # Answering no (or being headless) leaves a project that cannot build,
        # so the way out has to be on screen.
        result = self._run(tmp_path, monkeypatch)
        assert "Dependencies are not installed" in unwrapped(result.output)
        assert "uv sync" in unwrapped(result.output)

    def test_does_not_install_unattended(self, tmp_path, monkeypatch):
        # Recommended to a human, never run behind their back: it is a slow
        # network operation.
        with patch("subprocess.run") as run:
            self._run(tmp_path, monkeypatch)
        assert not any(
            "sync" in " ".join(str(a) for a in call.args[0])
            for call in run.call_args_list if call.args
        )


class TestGeneratedPyproject:
    def test_declares_the_supported_python_floor(self, tmp_path, monkeypatch):
        # uv defaults to >=3.12 without this and refuses to sync on 3.11.
        monkeypatch.chdir(tmp_path)
        runner.invoke(
            app,
            ["new", "blinky", "--board", "arduino_uno", "--stdlib", "micropython",
             "--pkg-manager", "uv"],
            catch_exceptions=False,
        )
        doc = tomlkit.parse((tmp_path / "blinky" / "pyproject.toml").read_text())
        assert doc["project"]["requires-python"] == ">=3.11"
