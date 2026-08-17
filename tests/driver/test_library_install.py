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


class TestIndexFetching:
    """
    Two hosts and a diagnosis. The mirror exists because the primary answers 403
    from data centres; the diagnosis exists because a macOS python.org build
    without its certificates fails in a way that reads exactly like the index
    being down.
    """

    def _no_cache(self, tmp_path, monkeypatch):
        monkeypatch.setattr(cmd, "CACHE_FILE", tmp_path / "cache.json")
        monkeypatch.setattr(cmd, "CACHE_DIR", tmp_path / "cache")
        monkeypatch.delenv("PYMCU_LIBRARY_INDEX", raising=False)

    def test_the_mirror_is_used_when_the_primary_fails(self, tmp_path, monkeypatch):
        self._no_cache(tmp_path, monkeypatch)
        tried = []

        def _fake_download(url):
            tried.append(url)
            return None if url == cmd.DEFAULT_INDEX_URL else {"v": 1, "libraries": []}

        monkeypatch.setattr(cmd, "_download_index", _fake_download)
        index, source = cmd.fetch_index(refresh=True)

        assert tried == [cmd.DEFAULT_INDEX_URL, cmd.MIRROR_INDEX_URL]
        assert source == "network"
        assert index == {"v": 1, "libraries": []}

    def test_an_override_replaces_both(self, tmp_path, monkeypatch):
        self._no_cache(tmp_path, monkeypatch)
        monkeypatch.setenv("PYMCU_LIBRARY_INDEX", "https://example.test/i.json")
        assert cmd._index_urls() == ["https://example.test/i.json"]

    def test_a_certificate_failure_says_it_is_local(self, tmp_path, monkeypatch):
        self._no_cache(tmp_path, monkeypatch)

        def _boom(url, timeout=10, context=None):
            raise cmd.urllib.error.URLError(
                "[SSL: CERTIFICATE_VERIFY_FAILED] certificate verify failed")

        monkeypatch.setattr(cmd.urllib.request, "urlopen", _boom)
        monkeypatch.setattr(cmd, "_ssl_context", lambda: None)

        index, source = cmd.fetch_index(refresh=True)
        assert index == {} and source == ""
        assert "local trust store" in cmd.last_index_error()

    def test_the_error_is_cleared_on_success(self, tmp_path, monkeypatch):
        self._no_cache(tmp_path, monkeypatch)
        monkeypatch.setattr(cmd, "_download_index", lambda url: {"v": 1, "libraries": []})
        cmd.fetch_index(refresh=True)
        assert cmd.last_index_error() == ""

    def test_a_cached_copy_survives_both_hosts_failing(self, tmp_path, monkeypatch):
        self._no_cache(tmp_path, monkeypatch)
        cache = tmp_path / "cache.json"
        cache.write_text(json.dumps({"v": 1, "libraries": [{"name": "dht11"}]}))
        monkeypatch.setattr(cmd, "_download_index", lambda url: None)

        index, source = cmd.fetch_index(refresh=True)
        assert source == "cache"
        assert index["libraries"][0]["name"] == "dht11"


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




class TestVerifyImports:
    """
    What `--verify` compiles, now that no example ships in the wheel.

    It used to build the library's example, which is why examples were in the
    package in the first place -- and being in the package is what put them on
    the include path for every project in the environment. Importing the
    declared modules asks a narrower question that needs nothing but the
    wheel: do these modules resolve, and does their code compile, here.
    """

    def _library(self, tmp_path: Path) -> core.Library:
        return core.Library(
            name="dht11",
            distribution="pymcu-lib-dht11",
            version="0.2.0",
            package_dir=tmp_path,
            modules=["dht11", "_dht11"],
            arch=["avr"],
            layer="native",
        )

    def _project(self, tmp_path: Path, flavors=()):
        config = tmp_path / "pyproject.toml"
        stdlib = ("stdlib = [" + ", ".join(f'"{f}"' for f in flavors) + "]\n") if flavors else ""
        config.write_text(
            "[project]\nname = 'app'\nversion = '0.1.0'\n\n"
            '[tool.pymcu]\ntarget = "atmega328p"\n' + stdlib
        )
        import tomlkit as _tomlkit
        return cmd.Project(config, _tomlkit.loads(config.read_text()))

    def test_it_compiles_a_program_importing_every_public_module(self, tmp_path,
                                                                 monkeypatch):
        written = {}

        class _Result:
            returncode = 0
            stdout = ""
            stderr = ""

        def _fake_run(cmd_args, **kwargs):
            work = Path(kwargs["cwd"])
            written["main"] = (work / "src" / "main.py").read_text()
            written["config"] = (work / "pyproject.toml").read_text()
            return _Result()

        monkeypatch.setattr(cmd, "_pymcu_executable", lambda: Path("pymcu"))
        monkeypatch.setattr(cmd.subprocess, "run", _fake_run)

        ok, detail = cmd.verify_imports(self._library(tmp_path), self._project(tmp_path))

        assert ok, detail
        assert "import dht11" in written["main"]
        # Private modules are the library's own business; importing one
        # directly is not something a user is promised.
        assert "import _dht11" not in written["main"]
        assert 'target = "atmega328p"' in written["config"]

    def test_the_projects_layers_are_carried_over(self, tmp_path, monkeypatch):
        """A library resolves differently per layer, so the check must match."""
        written = {}

        class _Result:
            returncode = 0
            stdout = ""
            stderr = ""

        def _fake_run(cmd_args, **kwargs):
            written["config"] = (Path(kwargs["cwd"]) / "pyproject.toml").read_text()
            return _Result()

        monkeypatch.setattr(cmd, "_pymcu_executable", lambda: Path("pymcu"))
        monkeypatch.setattr(cmd.subprocess, "run", _fake_run)

        cmd.verify_imports(self._library(tmp_path),
                           self._project(tmp_path, flavors=["micropython"]))

        assert "micropython" in written["config"]

    def test_a_failed_build_reports_the_last_line(self, tmp_path, monkeypatch):
        class _Result:
            returncode = 1
            stdout = "compiling\nerror: DHT11 is not supported here\n"
            stderr = ""

        monkeypatch.setattr(cmd, "_pymcu_executable", lambda: Path("pymcu"))
        monkeypatch.setattr(cmd.subprocess, "run", lambda *_a, **_k: _Result())

        ok, detail = cmd.verify_imports(self._library(tmp_path), self._project(tmp_path))

        assert not ok
        assert "not supported here" in detail
