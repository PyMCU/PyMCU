# tests/driver/test_library_install.py
#
# Tests for `pymcu install`: the index filter, the package-manager choice and
# how the dependency is recorded.  No network and no real package installs.

import json
import time
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

    def test_a_cache_older_than_24_hours_is_refreshed(self, tmp_path, monkeypatch):
        self._no_cache(tmp_path, monkeypatch)
        cache = tmp_path / "cache.json"
        cache.write_text(json.dumps({"v": 1, "libraries": []}))

        now = time.time()
        cmd.os.utime(cache, (now - 25 * 60 * 60,) * 2)
        tried = []

        def _fake_download(url):
            tried.append(url)
            return {"v": 2, "libraries": [{"name": "dht"}]}

        monkeypatch.setattr(cmd, "_download_index", _fake_download)
        index, source = cmd.fetch_index()

        assert tried == [cmd.DEFAULT_INDEX_URL]
        assert source == "network"
        assert index["v"] == 2

    def test_a_stale_cache_is_used_when_refresh_fails(self, tmp_path, monkeypatch):
        self._no_cache(tmp_path, monkeypatch)
        cache = tmp_path / "cache.json"
        cache.write_text(json.dumps({"v": 1, "libraries": [{"name": "dht11"}]}))
        now = time.time()
        cmd.os.utime(cache, (now - 25 * 60 * 60,) * 2)
        monkeypatch.setattr(cmd, "_download_index", lambda url: None)

        index, source = cmd.fetch_index()

        assert source == "stale-cache"
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

    def test_unknown_name_from_cache_suggests_refresh(self, tmp_path, monkeypatch):
        self._project(tmp_path)
        config = tmp_path / "pyproject.toml"
        project = cmd.Project(
            config,
            cmd.tomlkit.loads(config.read_text()),
        )
        monkeypatch.setattr(cmd, "fetch_index", lambda refresh=False: ({"libraries": []}, "cache"))

        _, _, error = cmd.resolve_from_index(project, "dht")

        assert "not in the PyMCU library index" in error
        assert "--refresh" in error

    def test_install_reports_when_it_uses_a_stale_cache(self, tmp_path, monkeypatch):
        self._project(tmp_path)
        config = tmp_path / "pyproject.toml"
        project = cmd.Project(
            config,
            cmd.tomlkit.loads(config.read_text()),
        )
        entry = INDEX["libraries"][0]
        monkeypatch.setattr(
            cmd, "resolve_from_index",
            lambda project, name, refresh=False: (entry, "pymcu-lib-dht11", ""),
        )
        monkeypatch.setattr(cmd, "last_index_source", lambda: "stale-cache", raising=False)
        monkeypatch.setattr(cmd, "_needs_environment", lambda project: True)

        result = cmd.install_library(project, "dht11", verify=False)

        assert any("older than 24 hours" in note for note in result.log)

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

    def test_pip_project_records_in_requirements(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "pyproject.toml").write_text(
            '[project]\nname = "demo"\ndependencies = []\n\n'
            '[tool.pymcu]\nboard = "arduino_uno"\n'
        )
        (tmp_path / "requirements.txt").write_text("pymcu-stdlib>=0.1.0b1\n")

        project = cmd._load_project()
        cmd._add_dependency(project, "pymcu-lib-dht11>=0.2.0")

        assert "pymcu-lib-dht11>=0.2.0" in (
            tmp_path / "requirements.txt").read_text()
        assert "pymcu-lib-dht11" not in (tmp_path / "pyproject.toml").read_text()

        cmd._remove_dependency(project, "pymcu-lib-dht11")
        assert "pymcu-lib-dht11" not in (tmp_path / "requirements.txt").read_text()

    def test_requirements_edits_match_normalized_names_not_prefixes(
            self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "pyproject.toml").write_text(
            '[project]\nname = "demo"\ndependencies = []\n\n'
            '[tool.pymcu]\nboard = "arduino_uno"\n'
        )
        requirements = tmp_path / "requirements.txt"
        requirements.write_text(
            "sensor-dist-extra>=1\nSensor_Dist==0.9\n--extra-index-url https://example.test/simple\n"
        )
        project = cmd._load_project()

        cmd._add_dependency(project, "sensor-dist>=1.0")

        assert requirements.read_text() == (
            "sensor-dist-extra>=1\n"
            "sensor-dist>=1.0\n"
            "--extra-index-url https://example.test/simple\n"
        )

        cmd._remove_dependency(project, "SENSOR_DIST")

        assert requirements.read_text() == (
            "sensor-dist-extra>=1\n"
            "--extra-index-url https://example.test/simple\n"
        )

    def test_requirements_parser_handles_comments_markers_hashes_and_options(
            self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        (tmp_path / "pyproject.toml").write_text(
            '[project]\nname = "demo"\ndependencies = []\n\n'
            '[tool.pymcu]\nboard = "arduino_uno"\n'
        )
        requirements = tmp_path / "requirements.txt"
        requirements.write_text(
            "--index-url https://example.test/simple\n"
            "sensor-dist>=1; python_version >= '3.11' --hash=sha256:abc  "
            "# hardware dependency\n"
            "sensor-dist-extra>=1  # a different project\n"
        )
        project = cmd._load_project()

        cmd._add_dependency(project, "sensor-dist==2.0")

        assert requirements.read_text() == (
            "--index-url https://example.test/simple\n"
            "sensor-dist==2.0  # hardware dependency\n"
            "sensor-dist-extra>=1  # a different project\n"
        )
        assert requirements.read_text().count("sensor-dist==2.0") == 1

        cmd._remove_dependency(project, "sensor-dist")

        assert requirements.read_text() == (
            "--index-url https://example.test/simple\n"
            "sensor-dist-extra>=1  # a different project\n"
        )


class TestInstallerChoice:
    """
    The project's lock or Poetry marker owns manager selection. Merely finding
    uv on PATH must never turn a pip or Poetry project into a uv project.
    """

    def _project(self, tmp_path: Path, body: str) -> cmd.Project:
        (tmp_path / "pyproject.toml").write_text(body)
        return cmd._load_project()

    def test_uv_add_is_used_for_a_uv_locked_project(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        monkeypatch.setattr(cmd, "_uv_bin", lambda: "/usr/bin/uv")
        project = self._project(tmp_path, '[project]\nname = "demo"\n\n[tool.pymcu]\nboard = "arduino_uno"\n')
        (tmp_path / "uv.lock").touch()
        assert cmd._uses_uv_add(project)
        assert cmd.install_command(project, "pymcu-lib-dht11", pre=True)[1] == "add"
        assert cmd.uninstall_command(project, "pymcu-lib-dht11")[1] == "remove"

    def test_poetry_add_is_used_for_a_poetry_locked_project(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        monkeypatch.setattr(cmd, "_poetry_bin", lambda: "/usr/bin/poetry")
        project = self._project(
            tmp_path,
            '[project]\nname = "demo"\n\n[tool.pymcu]\nboard = "arduino_uno"\n',
        )
        (tmp_path / "poetry.lock").touch()

        assert cmd._project_package_manager(project) == "poetry"
        assert cmd.install_command(project, "pymcu-lib-dht11", pre=True) == [
            "/usr/bin/poetry", "add", "pymcu-lib-dht11", "--allow-prereleases",
        ]
        assert cmd.uninstall_command(project, "pymcu-lib-dht11") == [
            "/usr/bin/poetry", "remove", "pymcu-lib-dht11",
        ]

    def test_poetry_table_is_used_before_the_first_lock(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        monkeypatch.setattr(cmd, "_poetry_bin", lambda: "/usr/bin/poetry")
        project = self._project(
            tmp_path,
            '[project]\nname = "demo"\n\n'
            '[tool.poetry]\npackage-mode = false\n\n'
            '[tool.pymcu]\nboard = "arduino_uno"\n',
        )

        assert cmd._project_package_manager(project) == "poetry"
        assert cmd.install_command(project, "pymcu-lib-dht11", pre=False)[1] == "add"

    def test_legacy_poetry_dependencies_identify_poetry(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        monkeypatch.setattr(cmd, "_poetry_bin", lambda: "/usr/bin/poetry")
        project = self._project(
            tmp_path,
            '[tool.poetry]\nname = "demo"\nversion = "0.1.0"\n'
            '[tool.poetry.dependencies]\npython = ">=3.11"\n\n'
            '[tool.pymcu]\nboard = "arduino_uno"\n',
        )

        assert cmd._project_package_manager(project) == "poetry"
        assert cmd.install_command(project, "pymcu-lib-dht11", pre=False)[1] == "add"

    def test_pip_is_used_for_a_pip_managed_pep621_project(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        monkeypatch.setattr(cmd, "_uv_bin", lambda: "/usr/bin/uv")
        project = self._project(
            tmp_path,
            '[project]\nname = "demo"\n\n[tool.pymcu]\nboard = "arduino_uno"\n',
        )
        python = tmp_path / ".venv" / "bin" / "python"
        python.parent.mkdir(parents=True)
        python.touch()

        assert cmd._project_package_manager(project) == "pip"
        assert cmd.install_command(project, "pymcu-lib-dht11", pre=True) == [
            str(project.venv / "bin" / "python"), "-m", "pip", "install",
            "pymcu-lib-dht11", "--pre",
        ]

    def test_pip_is_used_without_a_project_table(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        monkeypatch.setattr(cmd, "_uv_bin", lambda: "/usr/bin/uv")
        project = self._project(tmp_path, '[tool.pymcu]\nboard = "arduino_uno"\n')
        python = tmp_path / ".venv" / "bin" / "python"
        python.parent.mkdir(parents=True)
        python.touch()
        assert cmd._project_package_manager(project) == "pip"
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
