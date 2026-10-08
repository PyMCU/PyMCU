# Tests for `pymcu install`/`pymcu libraries` on an upstream ("kind":
# "upstream") index entry: no pymcu.toml, no pymcu.libraries entry point --
# just a plain PyPI distribution the index vouches for.

import json
from pathlib import Path

import pytest
import tomlkit as _tomlkit

from src.driver.commands import libraries as cmd

UPSTREAM_ENTRY = {
    "kind": "upstream",
    "name": "adafruit_hcsr04",
    "distribution": "adafruit-circuitpython-hcsr04",
    "version": "0.4.25",
    "provides": ["adafruit_hcsr04"],
    "layer": "circuitpython",
    "repository": "https://github.com/adafruit/Adafruit_CircuitPython_HCSR04",
    "status": "active",
    "measured": {"targets": {"atmega328p": {"build": "ok", "flash": 4264}}},
}

INDEX = {"libraries": [UPSTREAM_ENTRY]}


def _project(tmp_path: Path, *, board: str = "arduino_uno",
            flavors: tuple = ("circuitpython",)) -> cmd.Project:
    stdlib = ("stdlib = [" + ", ".join(f'"{f}"' for f in flavors) + "]\n") if flavors else ""
    config = tmp_path / "pyproject.toml"
    config.write_text(
        "[project]\nname = 'demo'\nversion = '0.1.0'\ndependencies = []\n\n"
        f'[tool.pymcu]\nboard = "{board}"\n' + stdlib
    )
    return cmd.Project(config, _tomlkit.loads(config.read_text()))


def _serve_index(tmp_path: Path, monkeypatch, index=INDEX) -> None:
    index_file = tmp_path / "index.json"
    index_file.write_text(json.dumps(index))
    monkeypatch.setenv("PYMCU_LIBRARY_INDEX", index_file.as_uri())
    monkeypatch.setattr(cmd, "CACHE_FILE", tmp_path / "cache.json")
    monkeypatch.setattr(cmd, "CACHE_DIR", tmp_path / "cache")
    # Both defaults are the same Path object in production (see
    # core.libraries.library_index_cache_file); a test isolating one from the
    # real home directory has to isolate the other the same way, or a build's
    # read of the cache (core.upstream_libraries) and the CLI's write of it
    # (this module) would resolve to two different files under the override.
    monkeypatch.setattr(cmd.core_libraries, "LIBRARY_INDEX_CACHE_FILE", tmp_path / "cache.json")
    monkeypatch.setattr(cmd.core_libraries, "LIBRARY_INDEX_CACHE_DIR", tmp_path / "cache")


class TestResolveFromIndexAcceptsUpstream:
    def test_an_upstream_entry_resolves_by_name_or_distribution(self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        _serve_index(tmp_path, monkeypatch)

        entry, distribution, error = cmd.resolve_from_index(project, "adafruit_hcsr04")
        assert error == ""
        assert entry["kind"] == "upstream"
        assert distribution == "adafruit-circuitpython-hcsr04"

        entry2, dist2, error2 = cmd.resolve_from_index(
            project, "adafruit-circuitpython-hcsr04")
        assert error2 == ""
        assert dist2 == distribution

    def test_refused_when_the_layer_is_not_declared(self, tmp_path, monkeypatch):
        project = _project(tmp_path, flavors=())
        _serve_index(tmp_path, monkeypatch)

        _, _, error = cmd.resolve_from_index(project, "adafruit_hcsr04")
        assert "circuitpython" in error

    def test_no_index_entry_has_no_offline_driver_fallback(self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        monkeypatch.setattr(cmd, "fetch_index", lambda refresh=False: ({}, ""))
        monkeypatch.setattr(cmd, "last_index_error", lambda: "network is offline")

        entry, distribution, error = cmd.resolve_from_index(
            project, "adafruit_ssd1306"
        )

        assert entry is None
        assert distribution == ""
        assert "no cached copy" in error


class TestInstallUpstreamLibrary:
    def test_post_install_accepts_exact_version_among_duplicate_metadata(
            self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        environment = tmp_path / ".venv"
        site = environment / "lib" / "python3.12" / "site-packages"
        site.mkdir(parents=True)
        for version in ("0.9", "1.0"):
            info = site / f"sensor_dist-{version}.dist-info"
            info.mkdir()
            (info / "METADATA").write_text(
                "Metadata-Version: 2.1\n"
                "Name: sensor-dist\n"
                f"Version: {version}\n"
            )
        monkeypatch.setattr(cmd, "project_environment", lambda root: environment)
        entry = dict(UPSTREAM_ENTRY)
        entry.update({"name": "sensor", "distribution": "sensor-dist", "version": "1.0"})

        result = cmd._finish_upstream_install(
            project, entry, "sensor-dist", verify=False,
            result=cmd.ChangeResult(True, ""),
        )

        assert result.ok, result.message
        assert result.message == "sensor 1.0 installed"

    def test_poetry_cached_environment_is_used_for_post_install_discovery(
            self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        (tmp_path / "poetry.lock").touch()
        environment = tmp_path / "poetry-cache" / "demo-123"
        site = environment / "lib" / "python3.12" / "site-packages"
        site.mkdir(parents=True)
        monkeypatch.setattr(cmd, "project_environment", lambda root: environment)
        monkeypatch.setattr(
            cmd, "resolve_from_index",
            lambda project, name, refresh=False: (
                UPSTREAM_ENTRY, UPSTREAM_ENTRY["distribution"], ""),
        )
        monkeypatch.setattr(cmd, "last_index_source", lambda: "bundled")
        monkeypatch.setattr(cmd, "_needs_environment", lambda project: False)
        monkeypatch.setattr(cmd, "install_command", lambda *args, **kwargs: ["true"])
        monkeypatch.setattr(cmd, "_run", lambda *args, **kwargs: True)
        searches = []

        def installed_version(distribution, search):
            searches.append(search)
            return "0.4.25"

        monkeypatch.setattr(cmd, "installed_distribution_version", installed_version)

        result = cmd.install_library(project, "adafruit_hcsr04", verify=False)

        assert result.ok, result.message
        assert searches == [[str(site)]]

    @pytest.mark.parametrize(
        ("manager", "executable", "verb"),
        (("pip", "pip", "install"), ("uv", "/usr/bin/uv", "add"),
         ("poetry", "/usr/bin/poetry", "add")),
    )
    def test_uses_the_project_manager_recording_path(
        self, tmp_path, monkeypatch, manager, executable, verb
    ):
        project = _project(tmp_path)
        (tmp_path / ".venv" / "bin").mkdir(parents=True)
        (tmp_path / ".venv" / "bin" / "python").touch()
        if manager == "pip":
            (tmp_path / "requirements.txt").write_text("pymcu-stdlib>=0.1.0b1\n")
        elif manager == "uv":
            (tmp_path / "uv.lock").touch()
            monkeypatch.setattr(cmd, "_uv_bin", lambda: executable)
        else:
            (tmp_path / "poetry.lock").touch()
            monkeypatch.setattr(cmd, "_poetry_bin", lambda: executable)

        monkeypatch.setattr(
            cmd, "resolve_from_index",
            lambda project, name, refresh=False: (
                UPSTREAM_ENTRY, UPSTREAM_ENTRY["distribution"], ""),
        )
        monkeypatch.setattr(cmd, "last_index_source", lambda: "bundled")
        calls = []
        monkeypatch.setattr(cmd, "_run", lambda args, cwd: calls.append(args) or True)
        monkeypatch.setattr(
            cmd, "installed_distribution_version", lambda dist, search: "0.4.25"
        )

        result = cmd.install_library(project, "adafruit_hcsr04", verify=False)

        assert result.ok, result.message
        assert calls and verb in calls[0]
        assert "adafruit-circuitpython-hcsr04==0.4.25" in calls[0]
        if manager == "pip":
            assert calls[0][1:4] == ["-m", "pip", "install"]
            assert "adafruit-circuitpython-hcsr04==0.4.25" in (
                tmp_path / "requirements.txt").read_text()
        else:
            assert calls[0][:2] == [executable, "add"]
            # The mocked manager did not edit the file. The driver must not
            # duplicate the dependency behind its back.
            assert "adafruit-circuitpython-hcsr04" not in project.path.read_text()

    def test_successful_install(self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        _serve_index(tmp_path, monkeypatch)

        monkeypatch.setattr(cmd, "_needs_environment", lambda p: False)
        monkeypatch.setattr(cmd, "install_command",
                            lambda p, dist, pre: ["true"])
        monkeypatch.setattr(cmd, "_run", lambda *a, **k: True)
        monkeypatch.setattr(cmd, "installed_distribution_version",
                            lambda dist, search: "0.4.25")
        # `uv add` would have already recorded the dependency itself; forcing
        # the plain-pip path here is what makes this test's own recording
        # deterministic regardless of whether uv happens to be on this PATH.
        monkeypatch.setattr(cmd, "_uses_uv_add", lambda p: False)

        result = cmd.install_library(project, "adafruit_hcsr04", verify=False)

        assert result.ok, result.message
        assert result.library is None
        assert result.entry["kind"] == "upstream"
        assert "0.4.25" in result.message
        # Recorded as a project dependency, the same as a manifest library.
        assert "adafruit-circuitpython-hcsr04==0.4.25" in (
            (tmp_path / "pyproject.toml").read_text())

    def test_rejects_an_installed_version_the_index_did_not_measure(
            self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        _serve_index(tmp_path, monkeypatch)
        monkeypatch.setattr(cmd, "_needs_environment", lambda p: False)
        monkeypatch.setattr(cmd, "install_command", lambda p, dist, pre: ["true"])
        monkeypatch.setattr(cmd, "_run", lambda *a, **k: True)
        monkeypatch.setattr(
            cmd, "installed_distribution_version", lambda dist, search: "2.0"
        )
        rolled_back = []
        monkeypatch.setattr(
            cmd, "uninstall_command",
            lambda project, dist: rolled_back.append(dist) or None,
        )

        result = cmd.install_library(project, "adafruit_hcsr04", verify=False)

        assert not result.ok
        assert "index measured 0.4.25" in result.message
        assert rolled_back == ["adafruit-circuitpython-hcsr04"]

    def test_not_actually_installed_is_rolled_back(self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        _serve_index(tmp_path, monkeypatch)

        monkeypatch.setattr(cmd, "_needs_environment", lambda p: False)
        monkeypatch.setattr(cmd, "install_command", lambda p, dist, pre: ["true"])
        monkeypatch.setattr(cmd, "_run", lambda *a, **k: True)
        monkeypatch.setattr(cmd, "installed_distribution_version",
                            lambda dist, search: None)
        monkeypatch.setattr(cmd, "uninstall_command", lambda p, dist: None)

        result = cmd.install_library(project, "adafruit_hcsr04", verify=False)

        assert not result.ok
        assert "did not install" in result.message
        assert "adafruit-circuitpython-hcsr04" not in (
            (tmp_path / "pyproject.toml").read_text())

    def test_verify_true_calls_the_upstream_import_check(self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        _serve_index(tmp_path, monkeypatch)

        monkeypatch.setattr(cmd, "_needs_environment", lambda p: False)
        monkeypatch.setattr(cmd, "install_command", lambda p, dist, pre: ["true"])
        monkeypatch.setattr(cmd, "_run", lambda *a, **k: True)
        monkeypatch.setattr(cmd, "installed_distribution_version",
                            lambda dist, search: "0.4.25")

        captured = {}

        def fake_verify(entry, proj):
            captured["provides"] = list(entry.get("provides", []))
            return True, "adafruit_hcsr04 compiles for this chip"

        monkeypatch.setattr(cmd, "verify_upstream_imports", fake_verify)

        result = cmd.install_library(project, "adafruit_hcsr04", verify=True)

        assert result.ok
        assert captured["provides"] == ["adafruit_hcsr04"]
        assert any("compiles for this chip" in note for note in result.log)

    def test_a_failing_verify_rolls_back(self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        _serve_index(tmp_path, monkeypatch)

        monkeypatch.setattr(cmd, "_needs_environment", lambda p: False)
        monkeypatch.setattr(cmd, "install_command", lambda p, dist, pre: ["true"])
        monkeypatch.setattr(cmd, "_run", lambda *a, **k: True)
        monkeypatch.setattr(cmd, "installed_distribution_version",
                            lambda dist, search: "0.4.25")
        monkeypatch.setattr(cmd, "verify_upstream_imports",
                            lambda entry, proj: (False, "boom"))
        monkeypatch.setattr(cmd, "uninstall_command", lambda p, dist: None)

        result = cmd.install_library(project, "adafruit_hcsr04", verify=True)

        assert not result.ok
        assert "boom" in result.message


class TestRollbackPreservesAPreexistingDependency:
    """
    `uv add` / `poetry add` rewrite pyproject.toml and the lock file as part
    of installing, before verification ever runs. A failed post-install
    verification used to roll back with an unconditional `uv remove` /
    `poetry remove`, which deletes the dependency entirely -- correct for a
    brand-new one, wrong when it was already declared before this command
    ran for some other reason. The fix snapshots the dependency files before
    the install command runs and, for a pre-existing dependency, restores
    that snapshot instead of removing anything.
    """

    def _pyproject(self, pin: str) -> str:
        return (
            '[project]\nname = "demo"\nversion = "0.1.0"\n'
            f'dependencies = ["adafruit-circuitpython-hcsr04{pin}"]\n\n'
            '[tool.pymcu]\nboard = "arduino_uno"\nstdlib = ["circuitpython"]\n'
        )

    def test_uv_rollback_restores_rather_than_removes(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        original_pyproject = self._pyproject(">=0.4.20")
        (tmp_path / "pyproject.toml").write_text(original_pyproject)
        original_lock = "version = 1\n# pre-existing lock, before this command\n"
        (tmp_path / "uv.lock").write_text(original_lock)

        monkeypatch.setattr(cmd, "_uv_bin", lambda: "/usr/bin/uv")
        monkeypatch.setattr(
            cmd, "resolve_from_index",
            lambda project, name, refresh=False: (
                UPSTREAM_ENTRY, UPSTREAM_ENTRY["distribution"], ""),
        )
        monkeypatch.setattr(cmd, "last_index_source", lambda: "bundled")
        monkeypatch.setattr(cmd, "_needs_environment", lambda project: False)

        def fake_uv_add(args, cwd):
            # What `uv add` actually does: bump the pin and re-lock.
            assert args[:2] == ["/usr/bin/uv", "add"]
            (tmp_path / "pyproject.toml").write_text(self._pyproject("==0.4.25"))
            (tmp_path / "uv.lock").write_text("version = 1\n# re-locked by uv add\n")
            return True

        monkeypatch.setattr(cmd, "_run", fake_uv_add)
        monkeypatch.setattr(cmd, "installed_distribution_version",
                            lambda dist, search: "0.4.25")
        monkeypatch.setattr(cmd, "verify_upstream_imports",
                            lambda entry, proj: (False, "boom"))

        remove_calls = []
        monkeypatch.setattr(
            cmd.subprocess, "run",
            lambda *a, **k: remove_calls.append(a) or pytest.fail(
                "must not shell out to roll back a pre-existing dependency"),
        )

        project = cmd._load_project()
        result = cmd.install_library(project, "adafruit_hcsr04", verify=True)

        assert not result.ok
        assert "boom" in result.message
        assert "already a dependency before this command" in result.message
        assert remove_calls == []
        assert (tmp_path / "pyproject.toml").read_text() == original_pyproject
        assert (tmp_path / "uv.lock").read_text() == original_lock

    def test_poetry_rollback_restores_rather_than_removes(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        original_pyproject = self._pyproject(">=0.4.20")
        (tmp_path / "pyproject.toml").write_text(original_pyproject)
        original_lock = "# pre-existing poetry lock, before this command\n"
        (tmp_path / "poetry.lock").write_text(original_lock)

        monkeypatch.setattr(cmd, "_poetry_bin", lambda: "/usr/bin/poetry")
        monkeypatch.setattr(
            cmd, "resolve_from_index",
            lambda project, name, refresh=False: (
                UPSTREAM_ENTRY, UPSTREAM_ENTRY["distribution"], ""),
        )
        monkeypatch.setattr(cmd, "last_index_source", lambda: "bundled")
        monkeypatch.setattr(cmd, "_needs_environment", lambda project: False)

        def fake_poetry_add(args, cwd):
            assert args[:2] == ["/usr/bin/poetry", "add"]
            (tmp_path / "pyproject.toml").write_text(self._pyproject("==0.4.25"))
            (tmp_path / "poetry.lock").write_text("# re-locked by poetry add\n")
            return True

        monkeypatch.setattr(cmd, "_run", fake_poetry_add)
        monkeypatch.setattr(cmd, "installed_distribution_version",
                            lambda dist, search: "0.4.25")
        monkeypatch.setattr(cmd, "verify_upstream_imports",
                            lambda entry, proj: (False, "boom"))

        remove_calls = []
        monkeypatch.setattr(
            cmd.subprocess, "run",
            lambda *a, **k: remove_calls.append(a) or pytest.fail(
                "must not shell out to roll back a pre-existing dependency"),
        )

        project = cmd._load_project()
        result = cmd.install_library(project, "adafruit_hcsr04", verify=True)

        assert not result.ok
        assert "boom" in result.message
        assert "already a dependency before this command" in result.message
        assert remove_calls == []
        assert (tmp_path / "pyproject.toml").read_text() == original_pyproject
        assert (tmp_path / "poetry.lock").read_text() == original_lock


class TestVerifyUpstreamImports:
    def test_builds_a_program_importing_the_declared_modules(self, tmp_path, monkeypatch):
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

        ok, detail = cmd.verify_upstream_imports(UPSTREAM_ENTRY, _project(tmp_path))

        assert ok, detail
        assert "import adafruit_hcsr04" in written["main"]
        assert "circuitpython" in written["config"]

    def test_poetry_cached_environment_runs_the_verification_build(
            self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        (tmp_path / "poetry.lock").touch()
        environment = tmp_path / "poetry-cache" / "demo-123"
        executable = environment / "bin" / "pymcu"
        executable.parent.mkdir(parents=True)
        executable.touch()
        (environment / "lib" / "python3.14" / "site-packages").mkdir(parents=True)
        monkeypatch.setattr(cmd, "project_environment", lambda root: environment)
        monkeypatch.setattr(
            cmd, "_pymcu_executable",
            lambda: (_ for _ in ()).throw(
                AssertionError("must not use the CLI environment")
            ),
        )
        commands = []

        class _Result:
            returncode = 0
            stdout = ""
            stderr = ""

        monkeypatch.setattr(
            cmd.subprocess, "run",
            lambda args, **kwargs: commands.append(args) or _Result(),
        )

        ok, detail = cmd.verify_upstream_imports(UPSTREAM_ENTRY, project)

        assert ok, detail
        assert commands == [[str(executable), "build"]]

    def test_global_pymcu_verifies_against_poetry_site_packages(
            self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        (tmp_path / "poetry.lock").touch()
        environment = tmp_path / "poetry-cache" / "demo-123"
        site = environment / "lib" / "python3.14" / "site-packages"
        site.mkdir(parents=True)
        global_pymcu = tmp_path / "global" / "pymcu"
        global_pymcu.parent.mkdir()
        global_pymcu.touch()
        monkeypatch.setattr(cmd, "project_environment", lambda root: environment)
        monkeypatch.setattr(cmd, "_pymcu_executable", lambda: global_pymcu)
        calls = []

        class _Result:
            returncode = 0
            stdout = ""
            stderr = ""

        monkeypatch.setattr(
            cmd.subprocess, "run",
            lambda args, **kwargs: calls.append((args, kwargs)) or _Result(),
        )

        ok, detail = cmd.verify_upstream_imports(UPSTREAM_ENTRY, project)

        assert ok, detail
        assert calls[0][0] == [str(global_pymcu), "build"]
        assert calls[0][1]["env"][cmd.PROJECT_ENVIRONMENT_OVERRIDE] == str(environment)


class TestLibrariesListingIncludesUpstream:
    def test_installed_upstream_reads_the_cached_index(self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        _serve_index(tmp_path, monkeypatch)
        # fetch_index() populates the cache; installed_upstream() only reads it.
        cmd.fetch_index()

        from src.driver.core import upstream_libraries as up
        monkeypatch.setattr(up, "discover_installed_upstream",
                            lambda entries, search: entries)

        found = cmd._installed_upstream(project)
        assert any(
            entry.distribution == "adafruit-circuitpython-hcsr04"
            for entry in found
        )

    def test_no_cache_means_nothing_upstream(self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        monkeypatch.setattr(cmd.core_libraries, "read_cached_library_index", lambda: {})
        assert cmd._installed_upstream(project) == []


class TestUninstallUpstreamLibrary:
    def test_short_name_resolves_to_cached_upstream_distribution(
            self, tmp_path, monkeypatch):
        project = _project(tmp_path)
        monkeypatch.setattr(cmd, "_installed_libraries", lambda project: ([], []))
        monkeypatch.setattr(
            cmd.core_libraries, "read_cached_library_index", lambda: INDEX
        )
        commands = []
        monkeypatch.setattr(
            cmd, "uninstall_command",
            lambda project, distribution: ["remove", distribution],
        )
        monkeypatch.setattr(
            cmd, "_run", lambda args, cwd: commands.append(args) or True
        )
        monkeypatch.setattr(cmd, "_manager_records_dependencies", lambda project: True)

        result = cmd.uninstall_library(project, "adafruit_hcsr04")

        assert result.ok
        assert commands == [["remove", "adafruit-circuitpython-hcsr04"]]
        assert result.message == "adafruit-circuitpython-hcsr04 removed"
