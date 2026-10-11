"""
Tests for the macOS SDK fallback in hatch_build.py.

NativeAOT links the AOT binary through clang against the default SDK. On a macOS whose
default SDK carries .tbd syntax the installed clang rejects, the link fails although the
code is fine. The hook retries with SDKROOT set to the newest sibling SDK that links.
These tests pin the selection without needing a Mac: the SDK directories are fake and
the link probe is replaced.

Run with:
    pytest tests/driver/test_hatch_sdk_retry.py -v
"""
from __future__ import annotations

import importlib.util
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]


def _load_hook():
    spec = importlib.util.spec_from_file_location("hatch_build", ROOT / "hatch_build.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


hook = _load_hook()


def _fake_default(tmp_path: Path, default: str, *names: str) -> Path:
    """Create SDK directories in tmp_path and return the path xcrun would report."""
    for name in names:
        (tmp_path / name).mkdir()
    return tmp_path / default


def _xcrun_reports(monkeypatch, path: Path) -> None:
    def fake_run(cmd, **kwargs):
        assert cmd[:1] == ["xcrun"]
        return subprocess.CompletedProcess(cmd, 0, stdout=f"{path}\n", stderr="")

    monkeypatch.setattr(hook.subprocess, "run", fake_run)


def test_versions_sort_newest_first():
    names = ["MacOSX.sdk", "MacOSX26.sdk", "MacOSX27.0.sdk", "MacOSX26.2.sdk"]
    ordered = sorted((Path(n) for n in names), key=hook._sdk_version, reverse=True)
    assert [p.name for p in ordered] == [
        "MacOSX27.0.sdk", "MacOSX26.2.sdk", "MacOSX26.sdk", "MacOSX.sdk",
    ]


def test_the_newest_sdk_that_links_is_chosen(tmp_path, monkeypatch):
    default = _fake_default(tmp_path, "MacOSX27.0.sdk",
                            "MacOSX27.0.sdk", "MacOSX26.2.sdk", "MacOSX26.sdk")
    _xcrun_reports(monkeypatch, default)
    monkeypatch.setattr(hook, "_sdk_links", lambda sdk: sdk.name != "MacOSX27.0.sdk")

    assert hook._linkable_macos_sdk() == tmp_path / "MacOSX26.2.sdk"


def test_an_sdk_reached_by_two_names_is_tried_once(tmp_path, monkeypatch):
    default = _fake_default(tmp_path, "MacOSX27.0.sdk", "MacOSX27.0.sdk", "MacOSX27.sdk")
    (tmp_path / "MacOSX27.sdk").rmdir()
    (tmp_path / "MacOSX27.sdk").symlink_to(tmp_path / "MacOSX27.0.sdk")
    _xcrun_reports(monkeypatch, default)
    tried = []

    def links(sdk):
        tried.append(sdk.name)
        return False

    monkeypatch.setattr(hook, "_sdk_links", links)

    assert hook._linkable_macos_sdk() is None
    assert tried == ["MacOSX27.0.sdk"]


def test_no_linkable_sdk_gives_none(tmp_path, monkeypatch):
    default = _fake_default(tmp_path, "MacOSX27.0.sdk", "MacOSX27.0.sdk")
    _xcrun_reports(monkeypatch, default)
    monkeypatch.setattr(hook, "_sdk_links", lambda sdk: False)

    assert hook._linkable_macos_sdk() is None


def test_missing_xcrun_gives_none(monkeypatch):
    def no_xcrun(cmd, **kwargs):
        raise FileNotFoundError("xcrun")

    monkeypatch.setattr(hook.subprocess, "run", no_xcrun)
    assert hook._linkable_macos_sdk() is None


def test_only_a_darwin_link_error_triggers_the_fallback(monkeypatch):
    message = "ld: multiple errors: tapi error: malformed file"
    monkeypatch.setattr(sys, "platform", "darwin")
    assert hook._is_sdk_link_failure(message)
    assert not hook._is_sdk_link_failure("error: something else")

    monkeypatch.setattr(sys, "platform", "linux")
    assert not hook._is_sdk_link_failure(message)
