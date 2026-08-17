# tests/driver/test_wheel_binary_placement.py
#
# Putting the published pymcuc where the wheel expects it.
#
# The failure this covers: on a checkout where build/bin/pymcuc and
# src/driver/pymcuc are the same file -- a hard link, which is how a developer
# refreshes the driver's binary after a rebuild -- `shutil.copy2` raises
# SameFileError and the whole wheel build dies over a binary that was already
# exactly where it needed to be.

import importlib.util
import os
import stat
import sys
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]


@pytest.fixture(scope="module")
def hook():
    spec = importlib.util.spec_from_file_location(
        "pymcu_hatch_build_placement", REPO / "hatch_build.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _binary(path: Path, content: bytes = b"\x7fELF pretend") -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content)
    return path


class TestPlaceBinary:
    def test_a_normal_publish_is_copied(self, hook, tmp_path):
        src = _binary(tmp_path / "build" / "bin" / "pymcuc")
        dst = tmp_path / "src" / "driver" / "pymcuc"
        dst.parent.mkdir(parents=True)

        assert hook.place_binary(src, dst) == "placed at"
        assert dst.read_bytes() == src.read_bytes()

    def test_a_stale_destination_is_overwritten(self, hook, tmp_path):
        src = _binary(tmp_path / "build" / "bin" / "pymcuc", b"new")
        dst = _binary(tmp_path / "src" / "driver" / "pymcuc", b"old")

        assert hook.place_binary(src, dst) == "placed at"
        assert dst.read_bytes() == b"new"

    @pytest.mark.skipif(sys.platform == "win32", reason="hard links need admin on Windows")
    def test_the_same_file_is_left_alone_instead_of_failing(self, hook, tmp_path):
        """The maintainer's layout: one file, reachable by both paths."""
        src = _binary(tmp_path / "build" / "bin" / "pymcuc")
        dst = tmp_path / "src" / "driver" / "pymcuc"
        dst.parent.mkdir(parents=True)
        os.link(src, dst)

        assert hook.place_binary(src, dst) == "already in place"
        assert dst.read_bytes() == src.read_bytes()

    @pytest.mark.skipif(sys.platform == "win32", reason="POSIX mode bits")
    def test_the_executable_bit_is_set_either_way(self, hook, tmp_path):
        """
        A wheel with a non-executable pymcuc installs and then cannot run, so
        the bit is set whether the binary was copied or was already there.
        """
        src = _binary(tmp_path / "build" / "bin" / "pymcuc")
        src.chmod(0o644)
        dst = tmp_path / "src" / "driver" / "pymcuc"
        dst.parent.mkdir(parents=True)
        os.link(src, dst)

        hook.place_binary(src, dst)
        assert stat.S_IMODE(dst.stat().st_mode) & stat.S_IXUSR

    def test_a_missing_publish_says_so(self, hook, tmp_path):
        src = tmp_path / "build" / "bin" / "pymcuc"
        dst = tmp_path / "src" / "driver" / "pymcuc"
        dst.parent.mkdir(parents=True)

        with pytest.raises(FileNotFoundError) as excinfo:
            hook.place_binary(src, dst)
        assert "pymcuc" in str(excinfo.value)
