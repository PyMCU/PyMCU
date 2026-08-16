# tests/driver/test_library_lint.py
#
# Tests for `pymcu lint --library`: the checks a library has to pass before it
# can be published.

from pathlib import Path

import pytest

from src.driver.core import libraries as core
from src.driver.core.library_lint import lint_library, surface_hash


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
# Library lint
# ---------------------------------------------------------------------------

class TestLibraryLint:
    def test_a_clean_package_has_no_errors(self, tmp_path):
        pkg = _make_package(tmp_path)
        findings = lint_library(pkg, write_surface=True)
        assert [f for f in findings if f.severity == "error"] == []

    def test_non_ascii_in_a_string_is_an_error(self, tmp_path):
        pkg = _make_package(tmp_path)
        (pkg / "extra.py").write_text('MESSAGE = "temperatura en °C"\n', encoding="utf-8")
        findings = lint_library(pkg, write_surface=True)
        assert any(f.code == "ascii-string" and f.severity == "error" for f in findings)

    def test_non_ascii_in_a_comment_is_only_a_warning(self, tmp_path):
        pkg = _make_package(tmp_path)
        (pkg / "extra.py").write_text("# grados °C\nVALUE = 1\n", encoding="utf-8")
        findings = lint_library(pkg, write_surface=True)
        codes = {(f.code, f.severity) for f in findings}
        assert ("ascii-comment", "warn") in codes
        assert not any(f.code == "ascii-code" for f in findings)

    def test_sentinel_default_branch_is_an_error(self, tmp_path):
        pkg = _make_package(tmp_path)
        (pkg / "dht11.py").write_text(
            "from pymcu.chips import __CHIP__\n"
            "\n"
            "def read():\n"
            "    match __CHIP__.arch:\n"
            "        case \"avr\":\n"
            "            return 0\n"
            "        case _:\n"
            "            return 0xFFFF\n"
        )
        findings = lint_library(pkg, write_surface=True)
        assert any(f.code == "sentinel-default" for f in findings)

    def test_missing_declared_module_is_an_error(self, tmp_path):
        pkg = _make_package(tmp_path)
        (pkg / "dht11.py").unlink()
        findings = lint_library(pkg, write_surface=True)
        assert any(f.code == "module-missing" for f in findings)

    def test_missing_adapter_directory_is_an_error(self, tmp_path):
        pkg = _make_package(tmp_path)
        import shutil
        shutil.rmtree(pkg / "compat" / "micropython")
        findings = lint_library(pkg, write_surface=True)
        assert any(f.code == "adapter-missing" for f in findings)

    def test_surface_change_without_a_version_bump_is_caught(self, tmp_path):
        pkg = _make_package(tmp_path)
        lint_library(pkg, write_surface=True)
        before = surface_hash(pkg)

        (pkg / "dht11.py").write_text(
            (pkg / "dht11.py").read_text() + "\n\ndef confirm_download():\n    pass\n"
        )
        assert surface_hash(pkg) != before

        findings = lint_library(pkg)
        assert any(f.code == "surface-changed" and f.severity == "error" for f in findings)

    def test_private_helpers_are_not_part_of_the_surface(self, tmp_path):
        pkg = _make_package(tmp_path)
        before = surface_hash(pkg)
        (pkg / "dht11.py").write_text(
            (pkg / "dht11.py").read_text() + "\n\ndef _internal():\n    pass\n"
        )
        assert surface_hash(pkg) == before
