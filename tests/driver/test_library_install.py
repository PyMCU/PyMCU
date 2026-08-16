# tests/driver/test_library_install.py
#
# Tests for `pymcu install`: the index filter, the package-manager choice and
# how the dependency is recorded.  No network and no real package installs.

import json
from pathlib import Path

import pytest
from typer.testing import CliRunner

from src.driver.core import libraries as core
from src.driver.commands import libraries as cmd
from src.driver.main import app

runner = CliRunner()


MANIFEST = """
[library]
name = "dht11"
summary = "DHT11 temperature and humidity sensor"
license = "MIT"

[library.provides]
modules = ["dht11"]

[library.supports]
arch = ["avr"]
layer = "native"
adapters = ["micropython"]

[library.requires]
language-level = 1
"""


def _make_package(tmp_path: Path, manifest: str = MANIFEST, *, name: str = "pymcu_lib_dht11") -> Path:
    pkg = tmp_path / "src" / name
    pkg.mkdir(parents=True)
    (tmp_path / "pyproject.toml").write_text("[project]\nname = 'pymcu-lib-dht11'\n")
    (pkg / "pymcu.toml").write_text(manifest)
    (pkg / "dht11.py").write_text(
        "from pymcu.chips import __CHIP__\n"
        "from pymcu.exceptions import CompileError\n"
        "from pymcu.types import uint16, inline\n"
        "\n"
        "\n"
        "class DHT11:\n"
        "\n"
        "    @inline\n"
        "    def __init__(self, pin: str):\n"
        "        self.name = pin\n"
        "\n"
        "    @inline\n"
        "    def read(self) -> uint16:\n"
        "        match __CHIP__.arch:\n"
        "            case \"avr\":\n"
        "                return 0\n"
        "            case _:\n"
        "                raise CompileError(\"DHT11 is not supported here\")\n"
    )
    adapter = pkg / "compat" / "micropython"
    adapter.mkdir(parents=True)
    (adapter / "dht11.py").write_text("from dht11 import DHT11\n")
    return pkg


def _library(pkg: Path, manifest: str = MANIFEST) -> core.Library:
    return core.parse_manifest(
        pkg / "pymcu.toml", distribution="pymcu-lib-dht11", version="0.2.0", package_dir=pkg
    )


# ---------------------------------------------------------------------------
# Index filtering
# ---------------------------------------------------------------------------

INDEX = {
    "libraries": [
        {
            "name": "dht11",
            "distribution": "pymcu-lib-dht11",
            "version": "0.2.0",
            "summary": "DHT11 sensor",
            "arch": ["avr"],
            "layer": "native",
            "status": "active",
            "measured": {"targets": {"atmega328p": {"build": "ok", "flash": 412, "ram": 6},
                                     "rp2040": {"build": "unsupported"}}},
        }
    ]
}


class TestIndexVerdict:
    def test_measured_ok_passes(self):
        assert cmd.entry_verdict(INDEX["libraries"][0], "atmega328p", []) == []

    def test_measured_unsupported_is_refused(self):
        reasons = cmd.entry_verdict(INDEX["libraries"][0], "rp2040", [])
        assert reasons and "unsupported" in reasons[0]

    def test_broken_status_is_refused(self):
        entry = dict(INDEX["libraries"][0], status="broken")
        assert cmd.entry_verdict(entry, "atmega328p", [])

    def test_lookup_by_short_name_and_distribution(self):
        assert cmd.find_entry(INDEX, "dht11") is not None
        assert cmd.find_entry(INDEX, "pymcu-lib-dht11") is not None
        assert cmd.find_entry(INDEX, "nope") is None


class TestInstallCommand:
    def _project(self, tmp_path: Path, board: str = "arduino_uno") -> None:
        (tmp_path / "pyproject.toml").write_text(
            "[project]\n"
            'name = "demo"\n'
            'version = "0.1.0"\n'
            "dependencies = []\n"
            "\n"
            "[tool.pymcu]\n"
            f'board = "{board}"\n'
        )

    def _serve_index(self, tmp_path: Path, monkeypatch) -> None:
        index_file = tmp_path / "index.json"
        index_file.write_text(json.dumps(INDEX))
        monkeypatch.setenv("PYMCU_LIBRARY_INDEX", index_file.as_uri())
        monkeypatch.setattr(cmd, "CACHE_FILE", tmp_path / "cache.json")
        monkeypatch.setattr(cmd, "CACHE_DIR", tmp_path / "cache")

    def test_unknown_name_is_refused_without_installing(self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        self._project(tmp_path)
        self._serve_index(tmp_path, monkeypatch)
        monkeypatch.setattr(cmd, "_run", lambda *a, **k: pytest.fail("must not install"))

        result = runner.invoke(app, ["install", "nope"], catch_exceptions=False)
        assert result.exit_code == 1
        assert "not in the PyMCU library index" in unwrapped(result.output)

    def test_incompatible_chip_is_refused_before_download(self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        self._project(tmp_path, board="raspberry_pi_pico")
        self._serve_index(tmp_path, monkeypatch)
        monkeypatch.setattr(cmd, "_run", lambda *a, **k: pytest.fail("must not install"))

        result = runner.invoke(app, ["install", "dht11"], catch_exceptions=False)
        assert result.exit_code == 1
        assert "does not fit this project" in unwrapped(result.output)

    def test_project_without_target_is_refused(self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "pyproject.toml").write_text("[tool.pymcu]\nfrequency = 16000000\n")
        self._serve_index(tmp_path, monkeypatch)

        result = runner.invoke(app, ["install", "dht11"], catch_exceptions=False)
        assert result.exit_code == 1
        assert "no board or target" in unwrapped(result.output)

    def test_no_pyproject_is_refused(self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        result = runner.invoke(app, ["install", "dht11"], catch_exceptions=False)
        assert result.exit_code == 1
        assert "pyproject.toml" in unwrapped(result.output).lower()


class TestDependencyRecording:
    def test_dependency_is_added_and_replaced(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "pyproject.toml").write_text(
            "[project]\n"
            'name = "demo"\n'
            'dependencies = ["pymcu-stdlib>=0.1.0a5"]\n'
            "\n"
            "[tool.pymcu]\n"
            'board = "arduino_uno"\n'
        )
        project = cmd._load_project()
        cmd._add_dependency(project, "pymcu-lib-dht11>=0.2.0")
        text = (tmp_path / "pyproject.toml").read_text()
        assert "pymcu-lib-dht11>=0.2.0" in text
        assert "pymcu-stdlib>=0.1.0a5" in text

        project = cmd._load_project()
        cmd._add_dependency(project, "pymcu-lib-dht11>=0.3.0")
        text = (tmp_path / "pyproject.toml").read_text()
        assert text.count("pymcu-lib-dht11") == 1
        assert "0.3.0" in text

        project = cmd._load_project()
        cmd._remove_dependency(project, "pymcu-lib-dht11")
        assert "pymcu-lib-dht11" not in (tmp_path / "pyproject.toml").read_text()


class TestInstallerChoice:
    """
    `uv add` records the dependency itself; the driver must not write it twice,
    and a rollback has to undo the pyproject edit uv already made.
    """

    def _project(self, tmp_path: Path, body: str) -> cmd.Project:
        (tmp_path / "pyproject.toml").write_text(body)
        return cmd._load_project()

    def test_uv_add_is_used_for_a_pep621_project(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        monkeypatch.setattr(cmd, "_uv_bin", lambda: "/usr/bin/uv")
        project = self._project(tmp_path, '[project]\nname = "demo"\n\n[tool.pymcu]\nboard = "arduino_uno"\n')
        assert cmd._uses_uv_add(project)
        assert cmd.install_command(project, "pymcu-lib-dht11", pre=True)[1] == "add"
        assert cmd.uninstall_command(project, "pymcu-lib-dht11")[1] == "remove"

    def test_uv_pip_is_used_without_a_project_table(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        monkeypatch.setattr(cmd, "_uv_bin", lambda: "/usr/bin/uv")
        project = self._project(tmp_path, '[tool.pymcu]\nboard = "arduino_uno"\n')
        assert not cmd._uses_uv_add(project)
        assert "pip" in cmd.install_command(project, "pymcu-lib-dht11", pre=True)

    def test_no_environment_and_no_uv_is_reported(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        monkeypatch.setattr(cmd, "_uv_bin", lambda: None)
        project = self._project(tmp_path, '[tool.pymcu]\nboard = "arduino_uno"\n')
        assert cmd._needs_environment(project)
        assert cmd.install_command(project, "pymcu-lib-dht11", pre=True) is None


