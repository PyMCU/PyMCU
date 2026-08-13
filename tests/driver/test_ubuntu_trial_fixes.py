# tests/driver/test_ubuntu_trial_fixes.py
#
# The rest of what the Ubuntu 26.04 ARM64 trial turned up, minus the markup
# (that one has its own file, and its own sweep).
#
#   - `pymcu new` aborted when git was absent, after writing the whole project
#   - `pymcu version` answered "No such command"
#   - the zip-slip guard compared path strings instead of paths

import subprocess
import tarfile
from pathlib import Path

import pytest
from typer.testing import CliRunner

from src.driver.main import app

runner = CliRunner()


class TestGitIsOptional:
    """A minimal server image has no git; the scaffold does not need one."""

    def _new(self, tmp_path, monkeypatch, git):
        real_run = subprocess.run

        def fake_run(cmd, *args, **kwargs):
            if cmd and cmd[0] == "git" and not git:
                raise FileNotFoundError(2, "No such file or directory", "git")
            return real_run(cmd, *args, **kwargs)

        # CliRunner has no tty, so every prompt takes its default and the git
        # question would answer no. Say yes to that one only: the dependency
        # install must stay off, it reaches the network.
        def answer(prompt, *args, **kwargs):
            return "git" in prompt.lower()

        monkeypatch.setattr("src.driver.commands.new.subprocess.run", fake_run)
        monkeypatch.setattr("src.driver.commands.new._confirm", answer)
        monkeypatch.chdir(tmp_path)
        return runner.invoke(
            app,
            ["new", "blinky", "--board", "arduino_uno", "--stdlib", "micropython"],
        )

    def test_a_missing_git_does_not_fail_the_command(self, tmp_path, monkeypatch, unwrapped):
        result = self._new(tmp_path, monkeypatch, git=False)
        assert result.exit_code == 0, result.output
        assert "git is not installed" in unwrapped(result.output)
        # The point: the project it just wrote is still there and complete.
        assert (tmp_path / "blinky" / "pyproject.toml").exists()

    def test_the_errno_is_not_what_the_user_reads(self, tmp_path, monkeypatch, unwrapped):
        result = self._new(tmp_path, monkeypatch, git=False)
        assert "Errno 2" not in unwrapped(result.output)
        assert "No such file or directory" not in unwrapped(result.output)


class TestVersionIsACommand:
    def test_pymcu_version_runs(self):
        result = runner.invoke(app, ["version"])
        assert result.exit_code == 0
        assert "Version" in result.output

    def test_it_agrees_with_the_flag(self):
        assert (runner.invoke(app, ["version"]).output
                == runner.invoke(app, ["--version"]).output)

    @pytest.mark.parametrize("name", ["bench", "profile", "coffee"])
    def test_the_hidden_ones_still_work(self, name):
        # Hidden on purpose, but reachable -- the trial reported them as
        # unregistered, which they are not.
        assert runner.invoke(app, [name, "--help"]).exit_code == 0


class TestArchiveContainment:
    """A sibling whose name merely starts the same must not be writable."""

    def _extract(self, tmp_path, member_name):
        from rich.console import Console

        from pymcu.toolchain.sdk.base_tool import CacheableTool

        class _Tool(CacheableTool):
            def get_name(self):
                return "test-tool"

            def install(self):
                raise NotImplementedError

            def is_cached(self):
                return True

        archive = tmp_path / "payload.tar.gz"
        smuggled = tmp_path / "smuggled.txt"
        smuggled.write_text("payload")
        with tarfile.open(archive, "w:gz") as tar:
            tar.add(smuggled, arcname=member_name)

        target = tmp_path / "tools"
        target.mkdir()

        tool = _Tool.__new__(_Tool)
        tool.console = Console()
        tool._extract_archive(archive, target, "tar.gz")
        return target

    def test_a_prefix_sibling_is_rejected(self, tmp_path):
        # "tools-evil" starts with "tools": str.startswith let this through.
        self._extract(tmp_path, "../tools-evil/payload.txt")
        assert not (tmp_path / "tools-evil").exists()

    def test_a_parent_escape_is_rejected(self, tmp_path):
        self._extract(tmp_path, "../escaped.txt")
        assert not (tmp_path / "escaped.txt").exists()

    def test_an_ordinary_member_still_extracts(self, tmp_path):
        target = self._extract(tmp_path, "bin/tool")
        assert (target / "bin" / "tool").read_text() == "payload"
