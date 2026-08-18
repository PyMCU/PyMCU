# tests/driver/test_avrdude_assets.py
#
# Release-asset selection for the bundled avrdude download. No network access:
# the platform and machine are mocked, and only the chosen URL/hash is checked.
#
# The regression these pin down: selection used to key on sys.platform alone, so
# every Linux machine got the x86_64 tarball -- which does not execute on a
# Raspberry Pi, and failed later at flash time with an unhelpful message.

import errno
import sys
from pathlib import Path
from unittest.mock import patch

import pytest
from rich.console import Console

from src.driver.programmers.avrdude import AvrdudeProgrammer as Avrdude

# Every hash below was produced by downloading the v8.1 asset and hashing it
# locally; they also match the digests GitHub reports for the release.
EXPECTED = {
    ("linux", "x86_64"): (
        "avrdude_v8.1_Linux_64bit.tar.gz",
        "c751c88b1c0b886834d85cd4b19f100cc3415c896ab9f98cf7e2955edbcd678f",
    ),
    ("linux", "aarch64"): (
        "avrdude_v8.1_Linux_ARM64.tar.gz",
        "9e1f2c1e7988bac93f30e5e8aea6cd7a9c8e782542f4d93dc04b6495820184d8",
    ),
    ("linux", "armv7l"): (
        "avrdude_v8.1_Linux_ARMv6.tar.gz",
        "174343ea5c4c3b0d29e98eb4c8de44e0f075a407fded755a1b7fcf793909d1da",
    ),
    ("linux", "i686"): (
        "avrdude_v8.1_Linux_32bit.tar.gz",
        "de4b3fbf0683fd998e139a352392994566a7d729f67d32dd95cfaf95abe08b09",
    ),
    ("win32", "AMD64"): (
        "avrdude-v8.1-windows-x64.zip",
        "e4d571d81fee3387d51bfdedd0b6565e4c201e974101cac2caec7adfd6201da3",
    ),
    ("win32", "ARM64"): (
        "avrdude-v8.1-windows-arm64.zip",
        "2194b65669e680b855d139ccb863c75971b0a0fbdfbb50942bc554158020bf29",
    ),
    ("darwin", "x86_64"): (
        "avrdude_v8.1_macOS_64bit.tar.gz",
        "d7739fbb5d1fe649511121a695dac3f4ca5ccb348919bf1f45f9bc5a2ea0ce72",
    ),
}


class TestAssetSelection:
    @pytest.mark.parametrize(("os_key", "machine"), list(EXPECTED))
    def test_each_platform_gets_its_own_build(self, os_key, machine):
        filename, digest = EXPECTED[(os_key, machine)]
        info = Avrdude._select_asset(os_key, machine)
        assert info["url"].endswith(filename)
        assert info["hash"] == digest

    def test_arm_linux_does_not_get_the_x86_build(self):
        # The original bug, stated directly.
        for machine in ("aarch64", "arm64", "armv7l", "armv6l"):
            url = Avrdude._select_asset("linux", machine)["url"]
            assert "64bit" not in url, f"{machine} was handed the x86_64 tarball"

    def test_apple_silicon_falls_back_to_the_rosetta_build(self):
        # Upstream ships no arm64 macOS asset, so arm64 resolves to the x86_64
        # one, which runs under Rosetta. A brew install still wins via PATH.
        arm = Avrdude._select_asset("darwin", "arm64")
        intel = Avrdude._select_asset("darwin", "x86_64")
        assert arm == intel

    def test_armv7_uses_the_armv6_build(self):
        # The ARMv6 binary is forward-compatible with ARMv7 Pis.
        assert Avrdude._select_asset("linux", "armv7l") == \
               Avrdude._select_asset("linux", "armv6l")


class TestMachineNormalisation:
    @pytest.mark.parametrize(("raw", "canonical"), [
        ("x86_64", "x86_64"), ("AMD64", "x86_64"), ("x64", "x86_64"),
        ("aarch64", "arm64"), ("arm64", "arm64"), ("ARM64", "arm64"),
        ("armv7l", "armv6"), ("armv6l", "armv6"), ("armhf", "armv6"),
        ("i686", "x86"), ("i386", "x86"), ("x86", "x86"),
    ])
    def test_spellings_fold_onto_one_name(self, raw, canonical):
        assert Avrdude._arch_key(raw) == canonical

    def test_unknown_machine_is_not_guessed(self):
        assert Avrdude._arch_key("sparc64") is None


class TestFailureModes:
    def test_unknown_architecture_is_reported_not_guessed(self):
        # Better a clear error than a binary that cannot execute.
        with pytest.raises(RuntimeError, match="unrecognised architecture"):
            Avrdude._select_asset("linux", "sparc64")

    def test_unsupported_platform_names_itself(self):
        with pytest.raises(RuntimeError, match="freebsd14"):
            Avrdude._select_asset("freebsd14", "x86_64")

    def test_the_error_points_at_the_package_manager(self):
        with pytest.raises(RuntimeError, match="package manager"):
            Avrdude._select_asset("linux", "sparc64")


class TestIntegrity:
    def test_no_placeholder_hashes_remain(self):
        # The download used to be unverified: every hash read "PLACEHOLDER".
        for os_key, assets in Avrdude.METADATA["platforms"].items():
            for arch, info in assets.items():
                digest = info["hash"]
                assert digest.lower() not in ("placeholder", ""), f"{os_key}/{arch}"
                assert len(digest) == 64, f"{os_key}/{arch} is not a SHA-256"
                assert all(c in "0123456789abcdef" for c in digest.lower()), \
                    f"{os_key}/{arch} is not hex"

    def test_every_asset_url_is_pinned_to_the_configured_version(self):
        version = Avrdude.METADATA["version"]
        for assets in Avrdude.METADATA["platforms"].values():
            for info in assets.values():
                assert f"/v{version}/" in info["url"]

    def test_hashes_are_unique_per_asset(self):
        # A copy-paste slip would otherwise verify the wrong file happily.
        digests = [
            info["hash"]
            for assets in Avrdude.METADATA["platforms"].values()
            for info in assets.values()
        ]
        assert len(digests) == len(set(digests))


class TestHostResolution:
    def test_platform_info_follows_the_running_machine(self):
        programmer = Avrdude(Console(quiet=True))
        with patch("sys.platform", "linux"), \
             patch("platform.machine", return_value="aarch64"):
            assert programmer._get_platform_info()["url"].endswith("Linux_ARM64.tar.gz")

    def test_linux_variants_all_map_to_linux(self):
        for spelling in ("linux", "linux2"):
            with patch("sys.platform", spelling):
                assert Avrdude._os_key() == "linux"


class TestExecutabilityGuard:
    def test_a_wrong_architecture_binary_names_rosetta(self, tmp_path):
        binary = tmp_path / "avrdude"
        binary.write_bytes(b"\xcf\xfa\xed\xfe")
        # errno.EBADARCH only exists on macOS; production reads it with the same
        # numeric fallback, so the test must too or it cannot even run on CI's
        # Linux and Windows legs.
        exc = OSError(getattr(errno, "EBADARCH", 86), "Bad CPU type in executable")
        with patch("src.driver.programmers.avrdude.subprocess.run", side_effect=exc), \
             patch("sys.platform", "darwin"):
            with pytest.raises(RuntimeError) as err:
                Avrdude._verify_runs(binary)
        assert "Rosetta 2" in str(err.value)
        assert "brew install avrdude" in str(err.value)

    def test_any_other_os_error_does_not_blame_rosetta(self, tmp_path):
        binary = tmp_path / "avrdude"
        binary.write_bytes(b"\xcf\xfa\xed\xfe")
        exc = OSError(errno.EACCES, "Permission denied")
        with patch("src.driver.programmers.avrdude.subprocess.run", side_effect=exc), \
             patch("sys.platform", "darwin"):
            with pytest.raises(RuntimeError) as err:
                Avrdude._verify_runs(binary)
        assert "Rosetta" not in str(err.value)

    def test_a_runnable_binary_passes(self):
        # Any binary that actually executes passes the guard; the interpreter
        # running this test is the one executable every platform has.
        Avrdude._verify_runs(Path(sys.executable))


class TestPortDisambiguation:
    def _prog(self):
        return Avrdude(Console())

    def test_one_port_is_used_without_asking(self):
        with patch.object(Avrdude, "candidate_ports", return_value=["/dev/cu.usbmodem1101"]):
            assert self._prog().auto_detect_port() == "/dev/cu.usbmodem1101"

    def test_several_ports_are_never_guessed(self):
        ports = ["/dev/cu.usbmodem1101", "/dev/cu.usbserial-A50285BI"]
        with patch.object(Avrdude, "candidate_ports", return_value=ports):
            assert self._prog().auto_detect_port() is None

    def test_flashing_with_several_ports_lists_them_instead_of_writing(self, tmp_path):
        ports = ["/dev/cu.usbmodem1101", "/dev/cu.usbserial-A50285BI"]
        prog = self._prog()
        with patch.object(Avrdude, "candidate_ports", return_value=ports), \
             patch.object(Avrdude, "is_cached", return_value=True), \
             patch.object(Avrdude, "_get_binary", return_value=Path("/usr/bin/true")), \
             patch.object(Avrdude, "find_system_avrdude", return_value=Path("/usr/bin/true")), \
             patch("src.driver.programmers.avrdude.subprocess.run") as run:
            with pytest.raises(RuntimeError) as err:
                prog.flash(tmp_path / "f.hex", "atmega328p")
        assert run.call_count == 0
        for p in ports:
            assert p in str(err.value)

    def test_an_explicit_port_wins_over_detection(self, tmp_path):
        prog = self._prog()
        with patch.object(Avrdude, "candidate_ports", return_value=["/dev/cu.other"]), \
             patch.object(Avrdude, "is_cached", return_value=True), \
             patch.object(Avrdude, "_get_binary", return_value=Path("/usr/bin/true")), \
             patch.object(Avrdude, "find_system_avrdude", return_value=Path("/usr/bin/true")), \
             patch("src.driver.programmers.avrdude.subprocess.run") as run:
            prog.flash(tmp_path / "f.hex", "atmega328p", port="/dev/cu.chosen")
        assert "/dev/cu.chosen" in run.call_args[0][0]
