# tests/driver/test_wheel_rid_guard.py
#
# The build must not label a wheel with an architecture it does not contain.
#
# From the Windows 11 ARM64 trial: the machine had only an emulated x64
# Python. `_get_rid()` asks platform.machine() -- the machine -- and got
# ARM64, so dotnet compiled ARM64 binaries. `_get_wheel_platform_tag()` asks
# sysconfig.get_platform() -- the interpreter -- and got win-amd64, so the
# wheel went out labelled win_amd64. Two different questions, two different
# answers, no complaint. pip would have installed those ARM64 binaries onto an
# x64 machine.
#
# Same shape as the cache bug in #24: naming the interpreter when what matters
# is the payload.

import importlib.util
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
ROOT_HOOK = REPO / "hatch_build.py"
RISCV_HOOK = REPO / "extensions" / "pymcu-backend-riscv" / "hatch_build.py"


def _load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.fixture(scope="module")
def hook():
    return _load(ROOT_HOOK, "pymcu_hatch_build")


class TestTheMismatchIsCaught:
    def test_the_windows_arm_case(self, hook):
        # Exactly what the trial machine produced.
        with pytest.raises(RuntimeError) as excinfo:
            hook._check_rid_matches_wheel_tag("win-arm64", "win_amd64")
        message = str(excinfo.value)
        assert "win-arm64" in message and "win_amd64" in message
        assert "emulation" in message

    def test_the_message_says_what_to_do(self, hook):
        with pytest.raises(RuntimeError) as excinfo:
            hook._check_rid_matches_wheel_tag("win-arm64", "win_amd64")
        # Both halves matter: a contributor on an emulated Python needs the
        # first, CI doing a deliberate cross-build needs the second.
        assert "Install a Python matching the machine" in str(excinfo.value)
        assert "DOTNET_RID and WHEEL_PLATFORM_TAG together" in str(excinfo.value)

    @pytest.mark.parametrize(("rid", "tag"), [
        ("osx-arm64", "macosx_14_0_x86_64"),
        ("osx-x64", "macosx_14_0_arm64"),
        ("linux-arm64", "manylinux_2_17_x86_64"),
        ("linux-x64", "manylinux_2_17_aarch64"),
        ("win-x64", "win_arm64"),
    ])
    def test_every_platform_pair(self, hook, rid, tag):
        with pytest.raises(RuntimeError):
            hook._check_rid_matches_wheel_tag(rid, tag)


class TestHonestPairsPass:
    @pytest.mark.parametrize(("rid", "tag"), [
        ("linux-x64", "manylinux_2_17_x86_64"),
        ("linux-x64", "linux_x86_64"),
        ("linux-arm64", "manylinux_2_17_aarch64"),
        ("osx-x64", "macosx_10_9_x86_64"),
        ("osx-arm64", "macosx_14_0_arm64"),
        ("win-x64", "win_amd64"),
        ("win-arm64", "win_arm64"),
        ("win-x86", "win32"),
    ])
    def test_matching_pairs_are_accepted(self, hook, rid, tag):
        hook._check_rid_matches_wheel_tag(rid, tag)      # must not raise

    def test_no_rid_means_dotnet_targets_the_host(self, hook):
        # Without -r there is nothing to disagree with.
        hook._check_rid_matches_wheel_tag(None, "win_amd64")

    def test_an_unknown_rid_is_not_second_guessed(self, hook):
        # A deliberate override for something the table has no rule for must
        # not be blocked by a guard that cannot judge it.
        hook._check_rid_matches_wheel_tag("linux-musl-x64", "musllinux_1_2_x86_64")


class TestUniversalTagIsNarrowed:
    """universal2 promises two architectures; one RID ships one.

    Found by running the guard against this very Mac rather than only against
    invented pairs: the stock Apple Silicon Python is a universal2 build, so
    sysconfig hands out macosx_*_universal2 for a wheel whose payload is
    arm64-only. Left alone, the guard would have rejected every local macOS
    build -- and, worse, the tag was the same over-promise the a5 release had
    to correct by hand.
    """

    def test_arm64_payload_loses_the_universal_label(self, hook, monkeypatch):
        monkeypatch.delenv("WHEEL_PLATFORM_TAG", raising=False)
        assert hook._narrow_universal_tag(
            "osx-arm64", "macosx_10_15_universal2") == "macosx_10_15_arm64"

    def test_intel_payload_too(self, hook, monkeypatch):
        monkeypatch.delenv("WHEEL_PLATFORM_TAG", raising=False)
        assert hook._narrow_universal_tag(
            "osx-x64", "macosx_10_15_universal2") == "macosx_10_15_x86_64"

    def test_the_narrowed_tag_passes_the_guard(self, hook, monkeypatch):
        monkeypatch.delenv("WHEEL_PLATFORM_TAG", raising=False)
        narrowed = hook._narrow_universal_tag("osx-arm64", "macosx_10_15_universal2")
        hook._check_rid_matches_wheel_tag("osx-arm64", narrowed)

    def test_an_explicit_tag_is_left_alone(self, hook, monkeypatch):
        # CI pins the macOS tag deliberately; the hook must not second-guess it.
        monkeypatch.setenv("WHEEL_PLATFORM_TAG", "macosx_14_0_arm64")
        assert hook._narrow_universal_tag(
            "osx-arm64", "macosx_14_0_arm64") == "macosx_14_0_arm64"

    def test_non_universal_tags_are_untouched(self, hook, monkeypatch):
        monkeypatch.delenv("WHEEL_PLATFORM_TAG", raising=False)
        for rid, tag in (("win-x64", "win_amd64"),
                         ("linux-x64", "manylinux_2_17_x86_64")):
            assert hook._narrow_universal_tag(rid, tag) == tag


class TestTheTwoHooksStayInStep:
    """They are separate packages built independently, so the table is copied.

    #24 taught that a copied table drifts -- the install-hint tables in #25
    had already drifted apart the same way. This is the cheap guard against it.
    """

    def test_the_tables_are_identical(self):
        root = _load(ROOT_HOOK, "root_hook")
        riscv = _load(RISCV_HOOK, "riscv_hook")
        assert root._RID_WHEEL_ARCH == riscv._RID_WHEEL_ARCH

    def test_both_reject_the_windows_arm_case(self):
        for name, path in (("root", ROOT_HOOK), ("riscv", RISCV_HOOK)):
            module = _load(path, f"{name}_hook_check")
            with pytest.raises(RuntimeError):
                module._check_rid_matches_wheel_tag("win-arm64", "win_amd64")

    def test_every_rid_the_tables_produce_has_a_rule(self):
        # A RID that _get_rid() can return but the table does not cover would
        # silently disable the guard on that platform.
        root = _load(ROOT_HOOK, "root_hook_rids")
        for rid in ("linux-x64", "linux-arm64", "osx-x64", "osx-arm64",
                    "win-x64", "win-x86", "win-arm64"):
            assert rid in root._RID_WHEEL_ARCH
