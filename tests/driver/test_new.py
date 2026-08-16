# tests/driver/test_new.py
#
# Tests for the `pymcu new` scaffolder command.
# All I/O is exercised via typer's test client — no real prompts needed.

import json
import sys
from pathlib import Path
import pytest
from typer.testing import CliRunner
from src.driver.main import app

runner = CliRunner()


def _invoke_new(project_name: str, *extra_args: str, input_text: str = ""):
    """Run `pymcu new <name> [extra_args]` through the test client."""
    args = ["new", project_name] + list(extra_args)
    return runner.invoke(app, args, input=input_text, catch_exceptions=False)


# ---------------------------------------------------------------------------
# Error cases
# ---------------------------------------------------------------------------

class TestNewErrors:
    def test_existing_directory_exits_1(self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "my_project").mkdir()
        result = _invoke_new(
            "my_project",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
        )
        assert result.exit_code == 1
        assert "already exists" in unwrapped(result.output).lower()

    def test_invalid_frequency_exits_1(self, tmp_path, monkeypatch):
        # --freq is a hidden advanced flag; 0 must be rejected immediately.
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "my_project",
            "--chip", "atmega328p",
            "--freq", "0",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 1


# ---------------------------------------------------------------------------
# Programmer defaults
# ---------------------------------------------------------------------------

class TestNewProgrammerDefaults:
    def test_avr_board_uses_avrdude(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "avr_proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 0
        toml_text = (tmp_path / "avr_proj" / "pyproject.toml").read_text()
        assert "avrdude" in toml_text

    def test_pic_chip_uses_pk2cmd(self, tmp_path, monkeypatch):
        # PIC chips are accessed via the hidden --chip flag (no board mapping).
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "pic_proj",
            "--chip", "pic16f84a",
            "--freq", "4000000",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="none\nn\n",
        )
        assert result.exit_code == 0
        toml_text = (tmp_path / "pic_proj" / "pyproject.toml").read_text()
        assert "pk2cmd" in toml_text


# ---------------------------------------------------------------------------
# No star imports in generated entry file
# ---------------------------------------------------------------------------

class TestNoStarImport:
    def test_no_star_import_compat_board(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 0
        content = (tmp_path / "proj" / "src" / "main.py").read_text()
        assert "import *" not in content

    def test_no_star_import_advanced_chip(self, tmp_path, monkeypatch):
        # Advanced (hidden) chip path must also avoid star imports.
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--chip", "pic16f84a",
            "--freq", "4000000",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="none\nn\n",
        )
        assert result.exit_code == 0
        content = (tmp_path / "proj" / "src" / "main.py").read_text()
        assert "import *" not in content


# ---------------------------------------------------------------------------
# Layout options
# ---------------------------------------------------------------------------

class TestLayout:
    def test_src_layout_creates_src_main_py(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 0
        assert (tmp_path / "proj" / "src" / "main.py").exists()

    def test_no_src_layout_creates_app_py(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-src",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 0
        assert (tmp_path / "proj" / "app.py").exists()
        assert not (tmp_path / "proj" / "src").exists()


# ---------------------------------------------------------------------------
# .gitignore / VS Code tasks
# ---------------------------------------------------------------------------

class TestGitignore:
    def test_vscode_tasks_not_ignored(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        gi = (tmp_path / "proj" / ".gitignore").read_text()
        assert ".vscode/\n" not in gi and gi != ".vscode/"
        assert ".vscode/settings.json" in gi

    def test_vscode_tasks_json_exists(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        tasks = tmp_path / "proj" / ".vscode" / "tasks.json"
        assert tasks.exists()
        data = json.loads(tasks.read_text())
        labels = [t["label"] for t in data["tasks"]]
        assert "pymcu: build" in labels


# ---------------------------------------------------------------------------
# pymcu: sync VS Code task (new)
# ---------------------------------------------------------------------------

class TestVSCodeSyncTask:
    def test_sync_task_present_with_folder_open(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        data = json.loads((tmp_path / "proj" / ".vscode" / "tasks.json").read_text())
        sync_tasks = [
            t for t in data["tasks"]
            if t["label"] == "pymcu: sync"
        ]
        assert len(sync_tasks) == 1
        assert sync_tasks[0].get("runOptions", {}).get("runOn") == "folderOpen"


# ---------------------------------------------------------------------------
# Board selection recorded in pyproject.toml (new)
# ---------------------------------------------------------------------------

class TestBoardSelection:
    def test_board_only_in_standard_mode(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 0
        toml = (tmp_path / "proj" / "pyproject.toml").read_text()
        assert 'board = "arduino_uno"' in toml
        # Standard mode must NOT write target — build.py rejects both being set.
        assert 'target = ' not in toml

    def test_arduino_nano_derives_atmega328p(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--board", "arduino_nano",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 0
        toml = (tmp_path / "proj" / "pyproject.toml").read_text()
        assert 'board = "arduino_nano"' in toml
        # Standard mode: target is resolved by build.py from the board, not written here.
        assert 'target = ' not in toml


# ---------------------------------------------------------------------------
# Makefile generation (new)
# ---------------------------------------------------------------------------

class TestMakefileGeneration:
    def test_makefile_created_for_uv(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "uv",
            "--no-git",
            input_text="n\n",
        )
        makefile = (tmp_path / "proj" / "Makefile").read_text()
        assert "uv sync" in makefile
        assert "pymcu sync" in makefile

    def test_makefile_created_for_poetry(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "poetry",
            "--no-git",
            input_text="n\n",
        )
        makefile = (tmp_path / "proj" / "Makefile").read_text()
        assert "poetry install" in makefile
        assert "pymcu sync" in makefile

    def test_makefile_created_for_pip(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        makefile = (tmp_path / "proj" / "Makefile").read_text()
        assert "requirements.txt" in makefile
        assert "pymcu sync" in makefile


# ---------------------------------------------------------------------------
# Package manager auto-detection (new)
# ---------------------------------------------------------------------------

class TestPkgManagerDetection:
    def test_uv_detected_skips_prompt(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        # Simulate uv in PATH; no --pkg-manager flag supplied.
        monkeypatch.setattr("shutil.which", lambda cmd: "/usr/bin/uv" if cmd == "uv" else None)
        result = _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 0
        # When uv is detected, the Makefile uses `uv sync`.
        makefile = (tmp_path / "proj" / "Makefile").read_text()
        assert "uv sync" in makefile

    def test_poetry_detected_when_uv_absent(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)

        def _which(cmd: str):
            if cmd == "uv":
                return None
            if cmd == "poetry":
                return "/usr/local/bin/poetry"
            return None

        monkeypatch.setattr("shutil.which", _which)
        result = _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 0
        toml = (tmp_path / "proj" / "pyproject.toml").read_text()
        assert "pk2cmd" not in toml  # programmer check; project was created OK


# ---------------------------------------------------------------------------
# stdlib flavor in pyproject.toml
# ---------------------------------------------------------------------------

class TestStdlibFlavor:
    def test_stdlib_added_to_pyproject(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 0
        toml = (tmp_path / "proj" / "pyproject.toml").read_text()
        assert "micropython" in toml

    def test_no_stdlib_when_not_specified_advanced_mode(self, tmp_path, monkeypatch):
        # In advanced (--chip) mode, "none" is a valid stdlib answer.
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--chip", "atmega328p",
            "--freq", "16000000",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="none\nn\n",
        )
        assert result.exit_code == 0
        toml = (tmp_path / "proj" / "pyproject.toml").read_text()
        assert "stdlib = [" not in toml


# ---------------------------------------------------------------------------
# Frequency derived from board
# ---------------------------------------------------------------------------

class TestFrequency:
    def test_arduino_uno_gets_16mhz(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--board", "arduino_uno",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 0
        toml = (tmp_path / "proj" / "pyproject.toml").read_text()
        assert "16000000" in toml

    def test_custom_freq_via_advanced_flag(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--chip", "atmega328p",
            "--freq", "8000000",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="none\nn\n",
        )
        assert result.exit_code == 0
        toml = (tmp_path / "proj" / "pyproject.toml").read_text()
        assert "8000000" in toml


# ---------------------------------------------------------------------------
# target key in pyproject (not legacy chip key)
# ---------------------------------------------------------------------------

class TestTargetKey:
    def test_uses_target_not_chip_key_advanced_mode(self, tmp_path, monkeypatch):
        # In advanced (--chip) mode the TOML must use 'target = ' not 'chip = '.
        monkeypatch.chdir(tmp_path)
        _invoke_new(
            "proj",
            "--chip", "atmega328p",
            "--freq", "16000000",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="none\nn\n",
        )
        toml = (tmp_path / "proj" / "pyproject.toml").read_text()
        assert 'target = "atmega328p"' in toml
        assert 'chip = ' not in toml


# ---------------------------------------------------------------------------
# [tool.pymcu.flash] — the section `pymcu flash` actually reads
# ---------------------------------------------------------------------------

class TestFlashSection:
    def _scaffold(self, tmp_path, monkeypatch, *args: str) -> dict:
        import tomlkit
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj", *args, "--pkg-manager", "pip", "--no-git", input_text="n\n",
        )
        assert result.exit_code == 0
        doc = tomlkit.loads((tmp_path / "proj" / "pyproject.toml").read_text())
        return doc["tool"]["pymcu"]

    def test_programmer_goes_under_the_flash_table(self, tmp_path, monkeypatch):
        cfg = self._scaffold(
            tmp_path, monkeypatch, "--board", "arduino_uno", "--stdlib", "micropython",
        )
        assert cfg["flash"]["programmer"] == "avrdude"
        # The pre-0.15 [tool.pymcu.programmer] table is no longer scaffolded.
        assert "programmer" not in cfg

    def test_rp_board_scaffolds_the_rp2040_programmer(self, tmp_path, monkeypatch):
        cfg = self._scaffold(
            tmp_path, monkeypatch,
            "--board", "raspberry_pi_pico", "--stdlib", "micropython",
        )
        assert cfg["flash"]["programmer"] == "rp2040"


# ---------------------------------------------------------------------------
# Scaffolded frequency per board (ARM boards must not inherit the AVR default)
# ---------------------------------------------------------------------------

class TestBoardFrequencies:
    @pytest.mark.parametrize(
        "board,expected",
        [
            ("arduino_uno", 16_000_000),
            ("raspberry_pi_pico", 125_000_000),
            ("raspberry_pi_pico2", 150_000_000),
            ("attiny85", 8_000_000),
        ],
    )
    def test_frequency_matches_the_board_clock(
        self, tmp_path, monkeypatch, board, expected
    ):
        import tomlkit
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--board", board,
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 0
        doc = tomlkit.loads((tmp_path / "proj" / "pyproject.toml").read_text())
        assert doc["tool"]["pymcu"]["frequency"] == expected


# ---------------------------------------------------------------------------
# uv resolution
# ---------------------------------------------------------------------------


def test_resolve_uv_finds_binary_next_to_the_interpreter(tmp_path, monkeypatch):
    """A uv that is not on PATH but sits in the venv's bin/ must still be found.

    This is the pipx case: `pip install uv` from the CLI's own venv drops the
    binary there, and pipx only puts the app's declared entry points on PATH.
    Trusting PATH alone made `pymcu new` install uv, report success and then die
    with `[Errno 2] No such file or directory: 'uv'` when it ran `uv sync`.
    """
    from src.driver.commands import new as new_mod

    bindir = tmp_path / "bin"
    bindir.mkdir()
    fake_python = bindir / "python"
    fake_python.write_text("")
    fake_uv = bindir / ("uv.exe" if sys.platform == "win32" else "uv")
    fake_uv.write_text("")

    monkeypatch.setattr(new_mod.shutil, "which", lambda _name: None)
    monkeypatch.setattr(new_mod.sys, "executable", str(fake_python))

    assert new_mod._resolve_uv() == str(fake_uv)


def test_resolve_uv_returns_none_when_there_is_no_uv(tmp_path, monkeypatch):
    """No uv anywhere means None, so the caller can fall back to pip."""
    from src.driver.commands import new as new_mod

    bindir = tmp_path / "bin"
    bindir.mkdir()
    fake_python = bindir / "python"
    fake_python.write_text("")

    monkeypatch.setattr(new_mod.shutil, "which", lambda _name: None)
    monkeypatch.setattr(new_mod.sys, "executable", str(fake_python))
    monkeypatch.setitem(sys.modules, "uv", None)   # import uv -> ImportError

    assert new_mod._resolve_uv() is None
