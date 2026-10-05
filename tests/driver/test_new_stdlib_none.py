# tests/driver/test_new_stdlib_none.py
#
# `--stdlib none` means "no compat layer" -- the same answer the advanced
# mode prompt already accepts when you type "none" at it. Passed as a flag
# it used to stay in the flavor list, so every dependency list the command
# writes gained a `pymcu-none` requirement -- a distribution that does not
# exist -- and `pip install -r requirements.txt` on the scaffolded project
# failed before it reached anything real.

import pytest
import tomlkit
from typer.testing import CliRunner

from src.driver.main import app

runner = CliRunner()


def _scaffold(tmp_path, monkeypatch, *extra):
    monkeypatch.chdir(tmp_path)
    result = runner.invoke(
        app,
        ["new", "proj", *extra, "--no-git"],
        input="n\n",
        catch_exceptions=False,
    )
    assert result.exit_code == 0, result.output
    return tmp_path / "proj"


class TestStdlibNoneFlag:
    @pytest.mark.parametrize("manager", ["pip", "uv", "poetry"])
    def test_no_pymcu_none_dependency_any_manager(self, tmp_path, monkeypatch, manager):
        project = _scaffold(
            tmp_path, monkeypatch,
            "--board", "arduino_uno", "--stdlib", "none",
            "--pkg-manager", manager,
        )
        assert "pymcu-none" not in (project / "pyproject.toml").read_text()
        if manager == "pip":
            assert "pymcu-none" not in (project / "requirements.txt").read_text()

    def test_no_stdlib_key_in_pyproject(self, tmp_path, monkeypatch):
        # `pymcu build` resolves every entry in [tool.pymcu].stdlib to a
        # pymcu_<flavor> import -- "none" would send it looking for pymcu_none.
        project = _scaffold(
            tmp_path, monkeypatch,
            "--board", "arduino_uno", "--stdlib", "none", "--pkg-manager", "pip",
        )
        doc = tomlkit.parse((project / "pyproject.toml").read_text())
        assert "stdlib" not in doc["tool"]["pymcu"]

    def test_the_real_dependencies_are_still_written(self, tmp_path, monkeypatch):
        # "No compat layer" is not "no packages": the stdlib and the compiler
        # are still required to build anything.
        project = _scaffold(
            tmp_path, monkeypatch,
            "--board", "arduino_uno", "--stdlib", "none", "--pkg-manager", "pip",
        )
        reqs = (project / "requirements.txt").read_text().splitlines()
        assert any(r.startswith("pymcu-stdlib>=") for r in reqs)
        assert any(r.startswith("pymcu-compiler[") for r in reqs)

    def test_scaffolds_the_register_level_template(self, tmp_path, monkeypatch):
        # With no layer, the entry file is the bare-metal blink, not a compat
        # import that would fail on the first build.
        project = _scaffold(
            tmp_path, monkeypatch,
            "--board", "arduino_uno", "--stdlib", "none", "--pkg-manager", "pip",
        )
        main = (project / "src" / "main.py").read_text()
        assert "from pymcu.chips.atmega328p import" in main
        assert "machine" not in main and "import board" not in main

    def test_works_in_advanced_chip_mode(self, tmp_path, monkeypatch):
        project = _scaffold(
            tmp_path, monkeypatch,
            "--chip", "atmega328p", "--freq", "16000000",
            "--stdlib", "none", "--pkg-manager", "pip",
        )
        doc = tomlkit.parse((project / "pyproject.toml").read_text())
        assert "stdlib" not in doc["tool"]["pymcu"]
        assert "pymcu-none" not in (project / "requirements.txt").read_text()

    def test_none_alongside_a_real_flavor_is_dropped(self, tmp_path, monkeypatch):
        # `--stdlib none --stdlib micropython` asks for both a layer and no
        # layer; "none" is the vacuous half and simply adds nothing.
        project = _scaffold(
            tmp_path, monkeypatch,
            "--board", "arduino_uno",
            "--stdlib", "none", "--stdlib", "micropython",
            "--pkg-manager", "pip",
        )
        doc = tomlkit.parse((project / "pyproject.toml").read_text())
        assert doc["tool"]["pymcu"]["stdlib"] == ["micropython"]
        assert "pymcu-none" not in (project / "requirements.txt").read_text()
