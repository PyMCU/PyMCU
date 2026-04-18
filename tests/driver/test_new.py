# tests/driver/test_new.py
#
# Tests for the `pymcu new` scaffolder command.
# All I/O is exercised via typer's test client — no real prompts needed.

import json
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
    def test_existing_directory_exits_1(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "my_project").mkdir()
        result = _invoke_new("my_project", "--chip", "atmega328p")
        assert result.exit_code == 1
        assert "already exists" in result.output.lower()

    def test_invalid_frequency_exits_1(self, tmp_path, monkeypatch):
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
    def test_avr_chip_uses_avrdude(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "avr_proj",
            "--chip", "atmega328p",
            "--freq", "16000000",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="none\nn\n",   # stdlib prompt: none; install prompt: no
        )
        assert result.exit_code == 0
        toml_text = (tmp_path / "avr_proj" / "pyproject.toml").read_text()
        assert "avrdude" in toml_text
        assert "pickit2" not in toml_text

    def test_pic_chip_uses_pk2cmd(self, tmp_path, monkeypatch):
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
    @pytest.mark.parametrize("chip", ["atmega328p", "pic16f84a"])
    def test_no_star_import(self, tmp_path, monkeypatch, chip):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--chip", chip,
            "--freq", "4000000",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="none\nn\n",
        )
        assert result.exit_code == 0
        entry = tmp_path / "proj" / "src" / "main.py"
        assert entry.exists()
        content = entry.read_text()
        assert "import *" not in content


# ---------------------------------------------------------------------------
# Layout options
# ---------------------------------------------------------------------------

class TestLayout:
    def test_src_layout_creates_src_main_py(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--chip", "atmega328p",
            "--freq", "4000000",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="none\nn\n",
        )
        assert result.exit_code == 0
        assert (tmp_path / "proj" / "src" / "main.py").exists()

    def test_no_src_layout_creates_app_py(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--chip", "atmega328p",
            "--freq", "4000000",
            "--pkg-manager", "pip",
            "--no-src",
            "--no-git",
            input_text="none\nn\n",
        )
        assert result.exit_code == 0
        assert (tmp_path / "proj" / "app.py").exists()
        assert not (tmp_path / "proj" / "src").exists()


# ---------------------------------------------------------------------------
# .gitignore should track .vscode/tasks.json, not the whole .vscode/ dir
# ---------------------------------------------------------------------------

class TestGitignore:
    def test_vscode_tasks_not_ignored(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        _invoke_new(
            "proj",
            "--chip", "atmega328p",
            "--freq", "4000000",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="none\nn\n",
        )
        gi = (tmp_path / "proj" / ".gitignore").read_text()
        assert ".vscode/\n" not in gi and gi != ".vscode/"
        assert ".vscode/settings.json" in gi

    def test_vscode_tasks_json_exists(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        _invoke_new(
            "proj",
            "--chip", "atmega328p",
            "--freq", "4000000",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="none\nn\n",
        )
        tasks = tmp_path / "proj" / ".vscode" / "tasks.json"
        assert tasks.exists()
        data = json.loads(tasks.read_text())
        labels = [t["label"] for t in data["tasks"]]
        assert "pymcu: build" in labels


# ---------------------------------------------------------------------------
# stdlib flavor is recorded in pyproject.toml
# ---------------------------------------------------------------------------

class TestStdlibFlavor:
    def test_stdlib_added_to_pyproject(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--chip", "atmega328p",
            "--freq", "16000000",
            "--stdlib", "micropython",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="n\n",
        )
        assert result.exit_code == 0
        toml = (tmp_path / "proj" / "pyproject.toml").read_text()
        assert "micropython" in toml

    def test_no_stdlib_when_not_specified(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        result = _invoke_new(
            "proj",
            "--chip", "atmega328p",
            "--freq", "16000000",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="none\nn\n",  # answer 'none' at flavor prompt, 'n' at install
        )
        assert result.exit_code == 0
        toml = (tmp_path / "proj" / "pyproject.toml").read_text()
        # The [tool.pymcu] stdlib array key must be absent (not the same as pymcu-stdlib dep)
        assert "stdlib = [" not in toml


# ---------------------------------------------------------------------------
# Frequency is recorded in pyproject.toml
# ---------------------------------------------------------------------------

class TestFrequency:
    def test_custom_freq_in_pyproject(self, tmp_path, monkeypatch):
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
        assert "16000000" in toml


# ---------------------------------------------------------------------------
# target key in pyproject (not legacy chip key)
# ---------------------------------------------------------------------------

class TestTargetKey:
    def test_uses_target_not_chip_key(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        _invoke_new(
            "proj",
            "--chip", "atmega328p",
            "--freq", "4000000",
            "--pkg-manager", "pip",
            "--no-git",
            input_text="none\nn\n",
        )
        toml = (tmp_path / "proj" / "pyproject.toml").read_text()
        assert 'target = "atmega328p"' in toml
