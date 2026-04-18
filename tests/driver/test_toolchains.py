# tests/driver/test_toolchains.py
#
# Unit tests for toolchain classes.  No real binaries or network calls needed.

import os
import sys
import shutil
import zipfile
import tarfile
import io
from pathlib import Path
from importlib.metadata import EntryPoint
from unittest.mock import patch, MagicMock
import pytest

from rich.console import Console
from pymcu_toolchain_avr.avrgas import AvrgasToolchain, _TOOLCHAIN_VERSION
from pymcu_toolchain_pic.gputils import GputilsToolchain
from pymcu_toolchain_avr.avra import AvraToolchain
from pymcu_toolchain_avr import AvrToolchainPlugin
from pymcu_toolchain_pic import PicToolchainPlugin
from src.driver.toolchains import get_toolchain_for_chip, discover_plugins
from src.driver.core.base_tool import CacheableTool, _default_platform_key, _is_non_interactive


# ---------------------------------------------------------------------------
# Helper to mock entry_points for discover_plugins
# ---------------------------------------------------------------------------

def _make_entry_points(plugins):
    """Return a mock for importlib.metadata.entry_points that yields plugin classes."""
    eps = []
    for name, cls in plugins.items():
        ep = MagicMock(spec=EntryPoint)
        ep.name = name
        ep.load.return_value = cls
        eps.append(ep)
    return eps


# ---------------------------------------------------------------------------
# Platform key helper
# ---------------------------------------------------------------------------

class TestDefaultPlatformKey:
    def test_format(self):
        key = _default_platform_key()
        parts = key.split("-")
        assert len(parts) == 2
        assert parts[0] in ("linux", "darwin", "win32")
        # Architecture must be a known value or at least a non-empty string
        assert parts[1] in ("x86_64", "arm64") or len(parts[1]) > 0


# ---------------------------------------------------------------------------
# Non-interactive detection
# ---------------------------------------------------------------------------

class TestIsNonInteractive:
    def test_ci_env_makes_non_interactive(self, monkeypatch):
        monkeypatch.setenv("CI", "true")
        assert _is_non_interactive() is True

    def test_pymcu_no_interactive_env(self, monkeypatch):
        monkeypatch.delenv("CI", raising=False)
        monkeypatch.setenv("PYMCU_NO_INTERACTIVE", "1")
        assert _is_non_interactive() is True

    def test_interactive_when_no_env(self, monkeypatch):
        monkeypatch.delenv("CI", raising=False)
        monkeypatch.delenv("PYMCU_NO_INTERACTIVE", raising=False)
        # sys.stdin.isatty() drives the result — just ensure no exception
        result = _is_non_interactive()
        assert isinstance(result, bool)


# ---------------------------------------------------------------------------
# AvrgasToolchain.supports()
# ---------------------------------------------------------------------------

class TestAvrgasSupports:
    @pytest.mark.parametrize("chip", [
        "atmega328p", "atmega2560", "attiny85", "attiny13a", "atmega32u4",
    ])
    def test_avr_chips(self, chip):
        assert AvrgasToolchain.supports(chip) is True

    @pytest.mark.parametrize("chip", [
        "pic16f84a", "pic18f4550", "ch32v003", "unknown123",
    ])
    def test_non_avr_chips(self, chip):
        assert AvrgasToolchain.supports(chip) is False

    def test_generic_avr_string(self):
        assert AvrgasToolchain.supports("avr") is True


# ---------------------------------------------------------------------------
# GputilsToolchain.supports()
# ---------------------------------------------------------------------------

class TestGputilsSupports:
    @pytest.mark.parametrize("chip", [
        "pic16f84a", "pic18f4550", "pic12f675", "pic10f220",
    ])
    def test_pic_chips(self, chip):
        assert GputilsToolchain.supports(chip) is True

    @pytest.mark.parametrize("chip", [
        "atmega328p", "attiny85", "ch32v003",
    ])
    def test_non_pic_chips(self, chip):
        assert GputilsToolchain.supports(chip) is False


# ---------------------------------------------------------------------------
# get_toolchain_for_chip factory
# ---------------------------------------------------------------------------

class TestGetToolchainForChip:
    def _entry_points(self):
        return _make_entry_points({"avr": AvrToolchainPlugin, "pic": PicToolchainPlugin})

    def test_avr_chip_returns_avrgas(self):
        with patch("src.driver.toolchains.entry_points", return_value=self._entry_points()):
            tc = get_toolchain_for_chip("atmega328p", Console(quiet=True))
        assert isinstance(tc, AvrgasToolchain)

    def test_pic_chip_returns_gputils(self):
        with patch("src.driver.toolchains.entry_points", return_value=self._entry_points()):
            tc = get_toolchain_for_chip("pic16f84a", Console(quiet=True))
        assert isinstance(tc, GputilsToolchain)

    def test_avra_not_in_factory(self):
        with patch("src.driver.toolchains.entry_points", return_value=self._entry_points()):
            tc = get_toolchain_for_chip("atmega328p", Console(quiet=True))
        assert not isinstance(tc, AvraToolchain)

    def test_unknown_chip_raises(self):
        with patch("src.driver.toolchains.entry_points", return_value=self._entry_points()):
            with pytest.raises(ValueError, match="No toolchain found"):
                get_toolchain_for_chip("unknown_mcu_xyz", Console(quiet=True))

    def test_chip_stored_on_instance(self):
        with patch("src.driver.toolchains.entry_points", return_value=self._entry_points()):
            tc = get_toolchain_for_chip("atmega328p", Console(quiet=True))
        assert tc.chip == "atmega328p"


# ---------------------------------------------------------------------------
# discover_plugins
# ---------------------------------------------------------------------------

class TestDiscoverPlugins:
    def test_returns_avr_and_pic(self):
        eps = _make_entry_points({"avr": AvrToolchainPlugin, "pic": PicToolchainPlugin})
        with patch("src.driver.toolchains.entry_points", return_value=eps):
            plugins = discover_plugins()
        assert "avr" in plugins
        assert "pic" in plugins
        assert plugins["avr"] is AvrToolchainPlugin
        assert plugins["pic"] is PicToolchainPlugin

    def test_returns_empty_when_no_plugins(self):
        with patch("src.driver.toolchains.entry_points", return_value=[]):
            plugins = discover_plugins()
        assert plugins == {}

    def test_skips_broken_entry_points(self):
        bad_ep = MagicMock(spec=EntryPoint)
        bad_ep.name = "broken"
        bad_ep.load.side_effect = ImportError("module missing")
        eps = [bad_ep] + _make_entry_points({"avr": AvrToolchainPlugin})
        with patch("src.driver.toolchains.entry_points", return_value=eps):
            plugins = discover_plugins()
        assert "avr" in plugins
        assert "broken" not in plugins


# ---------------------------------------------------------------------------
# linux-aarch64 platform metadata
# ---------------------------------------------------------------------------

class TestAvrgasLinuxAarch64:
    def test_aarch64_metadata_present(self):
        assert "linux-arm64" in AvrgasToolchain.METADATA

    def test_aarch64_url_contains_aarch64(self):
        url = AvrgasToolchain.METADATA["linux-arm64"]["url"]
        assert "aarch64" in url


# ---------------------------------------------------------------------------
# CacheableTool: PYMCU_TOOLS_DIR override
# ---------------------------------------------------------------------------

class TestToolsDirOverride:
    def test_uses_pymcu_tools_dir_env(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PYMCU_TOOLS_DIR", str(tmp_path))
        tc = GputilsToolchain(Console(quiet=True))
        assert str(tmp_path) in str(tc.base_dir)

    def test_default_is_home_pymcu_tools(self, monkeypatch):
        monkeypatch.delenv("PYMCU_TOOLS_DIR", raising=False)
        tc = GputilsToolchain(Console(quiet=True))
        assert ".pymcu" in str(tc.base_dir)
        assert "tools" in str(tc.base_dir)


# ---------------------------------------------------------------------------
# AvrgasToolchain.is_cached() — checks version file
# ---------------------------------------------------------------------------

class TestAvrgasIsCached:
    def test_not_cached_when_no_binaries(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PYMCU_TOOLS_DIR", str(tmp_path))
        monkeypatch.setattr(shutil, "which", lambda name: None)
        tc = AvrgasToolchain(Console(quiet=True), "atmega328p")
        assert tc.is_cached() is False

    def test_cached_when_on_system_path(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PYMCU_TOOLS_DIR", str(tmp_path))
        monkeypatch.setattr(shutil, "which", lambda name: f"/usr/bin/{name}")
        tc = AvrgasToolchain(Console(quiet=True), "atmega328p")
        assert tc.is_cached() is True

    def test_not_cached_when_version_mismatch(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PYMCU_TOOLS_DIR", str(tmp_path))
        monkeypatch.setattr(shutil, "which", lambda name: None)
        tc = AvrgasToolchain(Console(quiet=True), "atmega328p")
        # Create fake binaries but with a stale version
        try:
            info = tc._platform_info()
            bin_dir = tc._get_tool_dir() / info["bin_dir"]
            bin_dir.mkdir(parents=True, exist_ok=True)
            for b in ("avr-as", "avr-ld", "avr-objcopy"):
                (bin_dir / b).write_text("")
            tc._write_cached_version("0.0.0-stale")
        except RuntimeError:
            pytest.skip("Platform not supported in this test environment")
        assert tc.is_cached() is False


# ---------------------------------------------------------------------------
# CacheableTool._extract_archive() — zip-slip protection
# ---------------------------------------------------------------------------

class TestExtractArchiveZipSlip:
    def _make_malicious_zip(self, path: Path) -> Path:
        """Create a zip with a path-traversal member."""
        zf_path = path / "malicious.zip"
        with zipfile.ZipFile(zf_path, "w") as zf:
            zf.writestr("../../../../etc/evil.txt", "pwned")
            zf.writestr("safe/file.txt", "safe content")
        return zf_path

    def test_rejects_zip_slip_members(self, tmp_path):
        tc = GputilsToolchain(Console(quiet=True))
        zf_path = self._make_malicious_zip(tmp_path)
        target = tmp_path / "extracted"
        target.mkdir()
        tc._extract_archive(zf_path, target, "zip")
        # The traversal path must NOT exist outside target
        assert not (tmp_path / "etc" / "evil.txt").exists()
        # The safe member should be extracted
        assert (target / "safe" / "file.txt").exists()

    def _make_malicious_tar(self, path: Path) -> Path:
        """Create a tar.gz with a path-traversal member."""
        tar_path = path / "malicious.tar.gz"
        buf = io.BytesIO()
        with tarfile.open(fileobj=buf, mode="w:gz") as tf:
            # Add safe file
            safe_data = b"safe content"
            info = tarfile.TarInfo(name="safe/file.txt")
            info.size = len(safe_data)
            tf.addfile(info, io.BytesIO(safe_data))
            # Add traversal file
            evil_data = b"pwned"
            info2 = tarfile.TarInfo(name="../../../../etc/evil.txt")
            info2.size = len(evil_data)
            tf.addfile(info2, io.BytesIO(evil_data))
        tar_path.write_bytes(buf.getvalue())
        return tar_path

    def test_rejects_tar_slip_members(self, tmp_path):
        tc = GputilsToolchain(Console(quiet=True))
        tar_path = self._make_malicious_tar(tmp_path)
        target = tmp_path / "extracted"
        target.mkdir()
        tc._extract_archive(tar_path, target, "tar.gz")
        assert not (tmp_path / "etc" / "evil.txt").exists()

    def test_unsupported_archive_type_raises(self, tmp_path):
        tc = GputilsToolchain(Console(quiet=True))
        dummy = tmp_path / "file.exe"
        dummy.write_bytes(b"")
        with pytest.raises(ValueError, match="Unsupported archive type"):
            tc._extract_archive(dummy, tmp_path, "exe")


# ---------------------------------------------------------------------------
# GputilsToolchain._find_system_gpasm
# ---------------------------------------------------------------------------

class TestFindSystemGpasm:
    def test_returns_path_when_on_system(self, monkeypatch):
        monkeypatch.setattr(shutil, "which", lambda name: "/usr/bin/gpasm")
        tc = GputilsToolchain(Console(quiet=True))
        result = tc._find_system_gpasm()
        assert result is not None
        assert result == Path("/usr/bin/gpasm")

    def test_returns_none_when_absent(self, monkeypatch):
        monkeypatch.setattr(shutil, "which", lambda name: None)
        tc = GputilsToolchain(Console(quiet=True))
        assert tc._find_system_gpasm() is None

    def test_is_cached_true_when_system_gpasm_present(self, monkeypatch, tmp_path):
        monkeypatch.setenv("PYMCU_TOOLS_DIR", str(tmp_path))
        monkeypatch.setattr(shutil, "which", lambda name: "/usr/bin/gpasm")
        tc = GputilsToolchain(Console(quiet=True))
        assert tc.is_cached() is True


# ---------------------------------------------------------------------------
# Version file helpers
# ---------------------------------------------------------------------------

class TestVersionFile:
    def test_write_and_read_version(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PYMCU_TOOLS_DIR", str(tmp_path))
        tc = GputilsToolchain(Console(quiet=True))
        tc._get_tool_dir().mkdir(parents=True, exist_ok=True)
        tc._write_cached_version("1.2.3")
        assert tc._read_cached_version() == "1.2.3"

    def test_read_returns_empty_when_absent(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PYMCU_TOOLS_DIR", str(tmp_path))
        tc = GputilsToolchain(Console(quiet=True))
        assert tc._read_cached_version() == ""

