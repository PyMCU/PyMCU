# tests/driver/test_toolchain_clean.py
#
# `pymcu toolchain clean`.
#
# Nothing ever pruned ~/.pymcu/tools, so every toolchain upgrade left its
# predecessor on disk for good. A developer machine here had 2.8 GB of it:
# four versions of the AVR toolchain, two each of two LLVM builds, plus a
# directory left by an older cache layout.

import os
from pathlib import Path

import pytest
from typer.testing import CliRunner

from src.driver.commands.toolchain import _collect_clean_targets, _human
from src.driver.main import app

runner = CliRunner()

CURRENT = None   # filled by the fixture: the key this machine uses today


@pytest.fixture(autouse=True)
def current_key():
    global CURRENT
    from pymcu.toolchain.sdk import _default_platform_key
    CURRENT = _default_platform_key()
    return CURRENT


def _cache(root: Path, tool: str, versions: list[str], key: str | None = None) -> Path:
    base = root / (key or CURRENT) / tool
    for i, name in enumerate(versions):
        d = base / name / "bin"
        d.mkdir(parents=True)
        (d / "tool").write_bytes(b"x" * 1024)
        os.utime(base / name, (1000 + i * 100, 1000 + i * 100))
    return base


class TestTargetSelection:
    def test_keeps_the_two_newest_versions(self, tmp_path):
        _cache(tmp_path, "pymcu-avr-toolchain", ["v1", "v2", "v3", "v4"])
        doomed = [p.name for p, _ in _collect_clean_targets(tmp_path, all_versions=False)]
        assert sorted(doomed) == ["v1", "v2"]

    def test_leaves_a_short_history_alone(self, tmp_path):
        _cache(tmp_path, "pymcu-avr-toolchain", ["v1", "v2"])
        assert _collect_clean_targets(tmp_path, all_versions=False) == []

    def test_all_removes_whole_toolchains(self, tmp_path):
        _cache(tmp_path, "pymcu-avr-toolchain", ["v1", "v2"])
        targets = _collect_clean_targets(tmp_path, all_versions=True)
        assert [p.name for p, _ in targets] == ["pymcu-avr-toolchain"]

    def test_stale_layouts_go(self, tmp_path):
        # "darwin" (no architecture) is what an older scheme wrote.
        (tmp_path / "darwin" / "avra" / "avra-1.3.0").mkdir(parents=True)
        targets = _collect_clean_targets(tmp_path, all_versions=False)
        assert [(p.name, why) for p, why in targets] == [("darwin", "stale cache layout")]

    def test_the_current_layout_is_never_called_stale(self, tmp_path):
        _cache(tmp_path, "pymcu-avr-toolchain", ["v1"])
        assert all(p.name != CURRENT for p, _ in
                   _collect_clean_targets(tmp_path, all_versions=False))

    def test_each_toolchain_is_counted_separately(self, tmp_path):
        _cache(tmp_path, "pymcu-avr-toolchain", ["v1", "v2", "v3"])
        _cache(tmp_path, "llvm-arm", ["v1", "v2", "v3"])
        targets = _collect_clean_targets(tmp_path, all_versions=False)
        assert len(targets) == 2   # one superseded version from each


class TestCommand:
    def _run(self, tmp_path, *args):
        env = {"PYMCU_TOOLS_DIR": str(tmp_path)}
        return runner.invoke(app, ["toolchain", "clean", *args], env=env)

    def test_dry_run_removes_nothing(self, tmp_path, unwrapped):
        base = _cache(tmp_path, "pymcu-avr-toolchain", ["v1", "v2", "v3"])
        result = self._run(tmp_path, "--dry-run")
        assert result.exit_code == 0
        assert "Would free" in unwrapped(result.output)
        assert (base / "v1").exists()

    def test_removes_superseded_versions(self, tmp_path):
        base = _cache(tmp_path, "pymcu-avr-toolchain", ["v1", "v2", "v3"])
        result = self._run(tmp_path)
        assert result.exit_code == 0
        assert not (base / "v1").exists()
        assert (base / "v2").exists() and (base / "v3").exists()

    def test_is_idempotent(self, tmp_path, unwrapped):
        _cache(tmp_path, "pymcu-avr-toolchain", ["v1", "v2", "v3"])
        self._run(tmp_path)
        assert "already tidy" in unwrapped(self._run(tmp_path).output)

    def test_missing_cache_is_not_an_error(self, tmp_path, unwrapped):
        result = self._run(tmp_path / "nope")
        assert result.exit_code == 0
        assert "does not exist" in unwrapped(result.output)

    def test_all_empties_the_cache(self, tmp_path):
        base = _cache(tmp_path, "pymcu-avr-toolchain", ["v1", "v2"])
        self._run(tmp_path, "--all")
        assert not base.exists()


class TestHumanSizes:
    @pytest.mark.parametrize(("size", "expected"), [
        (512, "512 B"),
        (1536, "1.5 KB"),
        (5 * 1024 * 1024, "5.0 MB"),
        (2 * 1024 * 1024 * 1024, "2.0 GB"),
    ])
    def test_reads_naturally(self, size, expected):
        assert _human(size) == expected
