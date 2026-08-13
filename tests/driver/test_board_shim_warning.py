# tests/driver/test_board_shim_warning.py
#
# The "No board file found" warning.
#
# `pymcu new --board raspberry_pi_pico --stdlib micropython` warned on every
# single build of a perfectly good project. The board is in the catalogue --
# `pymcu boards` lists it -- but the board *file* only exists for the
# CircuitPython flavor, because `board` is a CircuitPython concept
# (board.LED, board.GP25). MicroPython code addresses pins through
# machine.Pin and never imports it, so the shim it was warning about was one
# nothing needed.
#
# A warning that is always there is a warning people learn to scroll past,
# which costs you the one time it matters.

from pathlib import Path

import pytest

from src.driver.commands.build import _imports_board


def _sources(tmp_path: Path, body: str) -> Path:
    src = tmp_path / "src"
    src.mkdir(parents=True, exist_ok=True)
    (src / "main.py").write_text(body)
    return src


class TestDetectingTheImport:
    @pytest.mark.parametrize("body", [
        "import board\n",
        "from board import LED\n",
        "import board as b\n",
        "from machine import Pin\nimport board\n",
        "    import board\n",              # nested in a function
    ])
    def test_it_is_seen(self, tmp_path, body):
        assert _imports_board(_sources(tmp_path, body)) is True

    @pytest.mark.parametrize("body", [
        "from machine import Pin\nled = Pin(13, Pin.OUT)\n",
        "# import board\n",                       # commented out
        "import boardgame\n",                     # a different name entirely
        "from boardgame import Piece\n",
        # Inside a string: the match is anchored to the start of a line, so an
        # import has to be the statement rather than merely appear somewhere.
        'text = "import board"\n',
    ])
    def test_it_is_not_claimed(self, tmp_path, body):
        assert _imports_board(_sources(tmp_path, body)) is False

    def test_it_looks_beyond_the_entry_file(self, tmp_path):
        src = _sources(tmp_path, "from machine import Pin\n")
        (src / "pins.py").write_text("import board\n")
        assert _imports_board(src) is True

    def test_no_sources_is_not_an_error(self, tmp_path):
        assert _imports_board(tmp_path / "nothing-here") is False

    def test_unreadable_files_are_skipped(self, tmp_path):
        src = _sources(tmp_path, "import board\n")
        (src / "binary.py").write_bytes(b"\xff\xfe\x00\x01")
        assert _imports_board(src) is True       # must not raise
