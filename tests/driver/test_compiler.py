# tests/driver/test_compiler.py
#
# Unit tests for PyMCUCompiler path-resolution logic.
# No real compiler binary is required.

import sys
import shutil
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

    def test_returns_empty_string_on_import_error(self, tmp_path, monkeypatch):
        import builtins
        real_import = builtins.__import__

        def fake_import(name, *args, **kwargs):
            if name == "pymcu":
                raise ImportError("not installed")
            return real_import(name, *args, **kwargs)

        monkeypatch.setattr(builtins, "__import__", fake_import)
        result = self._compiler().get_stdlib_path(verbose=False)
        assert result == ""

    def test_does_not_print_errors_when_not_verbose(self, tmp_path, monkeypatch, capsys):
        import builtins
        real_import = builtins.__import__

        def fake_import(name, *args, **kwargs):
            if name == "pymcu":
                raise ImportError("not installed")
            return real_import(name, *args, **kwargs)

        monkeypatch.setattr(builtins, "__import__", fake_import)
        self._compiler().get_stdlib_path(verbose=False)
        captured = capsys.readouterr()
        assert "Failed to import" not in captured.out
        assert "Failed to import" not in captured.err
