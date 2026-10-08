# tests/driver/test_compiler.py
#
# Unit tests for PyMCUCompiler path-resolution logic.
# No real compiler binary is required.

import sys
import shutil
import json
from importlib.metadata import Distribution
from pathlib import Path
import pytest

from src.driver.core.compiler import PyMCUCompiler
from rich.console import Console


class TestGetCompilerPath:
    def _compiler(self, tmp_path):
        """Return a compiler whose start_path is deeply nested so parent.parent is empty."""
        c = PyMCUCompiler(Console(quiet=True))
        # Use a 3-level deep path so that parent.parent = tmp_path (fresh, empty)
        base = tmp_path / "src" / "driver"
        base.mkdir(parents=True)
        c._get_start_path = lambda: base
        return c, base

    def test_returns_adjacent_binary(self, tmp_path):
        c, base = self._compiler(tmp_path)
        exe = base / "pymcuc"
        exe.write_text("")
        assert c.get_compiler_path() == exe

    def test_prefers_adjacent_over_build_bin(self, tmp_path):
        c, base = self._compiler(tmp_path)
        adjacent = base / "pymcuc"
        adjacent.write_text("")
        build_bin = tmp_path / "build" / "bin" / "pymcuc"
        build_bin.parent.mkdir(parents=True, exist_ok=True)
        build_bin.write_text("")
        assert c.get_compiler_path() == adjacent

    def test_finds_in_bin_subdir(self, tmp_path):
        c, base = self._compiler(tmp_path)
        bin_exe = base / "bin" / "pymcuc"
        bin_exe.parent.mkdir()
        bin_exe.write_text("")
        assert c.get_compiler_path() == bin_exe

    def test_falls_back_to_build_bin(self, tmp_path):
        c, base = self._compiler(tmp_path)
        build_bin = tmp_path / "build" / "bin" / "pymcuc"
        build_bin.parent.mkdir(parents=True, exist_ok=True)
        build_bin.write_text("")
        assert c.get_compiler_path() == build_bin

    def test_falls_back_to_path(self, tmp_path, monkeypatch):
        c, base = self._compiler(tmp_path)
        # Nothing adjacent and nothing in build/bin
        monkeypatch.setattr(shutil, "which", lambda name: "/usr/bin/pymcuc")
        assert c.get_compiler_path() == Path("/usr/bin/pymcuc")

    def test_relative_fallback_when_nothing_found(self, tmp_path, monkeypatch):
        c, base = self._compiler(tmp_path)
        monkeypatch.setattr(shutil, "which", lambda name: None)
        assert c.get_compiler_path() == Path("pymcuc")

    def test_cmake_paths_not_checked(self, tmp_path, monkeypatch):
        c, base = self._compiler(tmp_path)
        # Place pymcuc only in old cmake dirs; they must NOT be found
        for d in ("cmake-build-debug/bin", "cmake-build-release/bin"):
            p = tmp_path / d / "pymcuc"
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text("")
        monkeypatch.setattr(shutil, "which", lambda name: None)
        assert c.get_compiler_path() == Path("pymcuc")


class TestGetStdlibPath:
    @staticmethod
    def _compiler():
        return PyMCUCompiler(Console(quiet=True))

    def test_returns_empty_string_when_metadata_and_path_have_no_stdlib(
            self, tmp_path, monkeypatch):
        monkeypatch.setattr("src.driver.core.compiler.distributions", lambda: [])
        monkeypatch.setattr(
            "src.driver.core.compiler.PathFinder.find_spec", lambda name, path: None
        )
        result = self._compiler().get_stdlib_path(verbose=False)
        assert result == ""

    def test_does_not_print_errors_when_not_verbose(self, tmp_path, monkeypatch, capsys):
        monkeypatch.setattr("src.driver.core.compiler.distributions", lambda: [])
        monkeypatch.setattr(
            "src.driver.core.compiler.PathFinder.find_spec", lambda name, path: None
        )
        self._compiler().get_stdlib_path(verbose=False)
        captured = capsys.readouterr()
        assert captured.out == captured.err == ""

    def test_editable_metadata_locates_stdlib_without_importing_pymcu(
            self, tmp_path, monkeypatch):
        project = tmp_path / "stdlib-project"
        package = project / "src" / "pymcu"
        (package / "chips").mkdir(parents=True)
        marker = tmp_path / "initializer-ran"
        (package / "__init__.py").write_text(
            f"from pathlib import Path\nPath({str(marker)!r}).touch()\n"
        )
        dist_info = tmp_path / "site" / "pymcu_stdlib-1.0.dist-info"
        dist_info.mkdir(parents=True)
        (dist_info / "METADATA").write_text(
            "Metadata-Version: 2.1\nName: pymcu-stdlib\nVersion: 1.0\n"
        )
        (dist_info / "direct_url.json").write_text(json.dumps({
            "url": project.as_uri(), "dir_info": {"editable": True},
        }))
        (dist_info / "RECORD").write_text(
            f"{dist_info.name}/METADATA,,\n{dist_info.name}/direct_url.json,,\n"
        )
        monkeypatch.setattr(
            "src.driver.core.compiler.distributions",
            lambda: [Distribution.at(dist_info)],
        )

        result = self._compiler().get_stdlib_path(verbose=False)

        assert result == str(package)
        assert not marker.exists()


class TestIsolateStdlib:
    def test_exposes_pymcu_without_site_packages_siblings(self, tmp_path):
        site = tmp_path / "site-packages"
        package = site / "pymcu"
        (package / "chips").mkdir(parents=True)
        (package / "types.py").write_text("VALUE = 1\n")
        (site / "host_only.py").write_text("VALUE = 2\n")

        root = PyMCUCompiler(Console(quiet=True)).isolate_stdlib(
            str(package), tmp_path / "dist"
        )

        assert (root / "pymcu" / "types.py").is_file()
        assert not (root / "host_only.py").exists()

    def test_exposes_only_files_owned_by_the_stdlib_distribution(
            self, tmp_path, monkeypatch):
        site = tmp_path / "site-packages"
        package = site / "pymcu"
        (package / "chips").mkdir(parents=True)
        (package / "chips" / "__init__.py").write_text("")
        (package / "types.py").write_text("VALUE = 1\n")
        (package / "toolchain").mkdir()
        (package / "toolchain" / "sdk.py").write_text("HOST_ONLY = 1\n")

        stdlib_info = site / "pymcu_stdlib-1.0.dist-info"
        stdlib_info.mkdir()
        (stdlib_info / "METADATA").write_text(
            "Metadata-Version: 2.1\nName: pymcu-stdlib\nVersion: 1.0\n"
        )
        (stdlib_info / "RECORD").write_text(
            "pymcu/chips/__init__.py,,\npymcu/types.py,,\n"
        )
        sdk_info = site / "pymcu_sdk-1.0.dist-info"
        sdk_info.mkdir()
        (sdk_info / "METADATA").write_text(
            "Metadata-Version: 2.1\nName: pymcu-sdk\nVersion: 1.0\n"
        )
        (sdk_info / "RECORD").write_text("pymcu/toolchain/sdk.py,,\n")
        monkeypatch.setattr(
            "src.driver.core.compiler.distributions",
            lambda: [Distribution.at(stdlib_info), Distribution.at(sdk_info)],
        )

        compiler = PyMCUCompiler(Console(quiet=True))
        stdlib = compiler.get_stdlib_path()
        root = compiler.isolate_stdlib(stdlib, tmp_path / "dist")

        assert (root / "pymcu" / "chips" / "__init__.py").is_file()
        assert (root / "pymcu" / "types.py").is_file()
        assert not (root / "pymcu" / "toolchain").exists()
