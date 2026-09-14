"""PyMCU#311: the preamble is inserted in two places, so one offset cannot describe it.

`pymcu build` puts a header at the top of the entry file and, when the source has an
explicit `def main():`, a call as the first statement inside it. A line ABOVE `def main():`
is shifted by the header alone; a line at or below the call by the header plus one. The
driver carried a single number, chose the larger, and subtracted it from everything, so
every diagnostic about a module-level line came out one line early and the debugger's line
map was off by one over the same region.

Every injected line now carries a sentinel, and `generated -> original` is read back off the
file. That composes over the four preambles that can stack without any of them knowing about
the others, which a single accumulated integer cannot do.
"""

import json

from src.driver.commands.build import (
    INJECTED_MARK,
    _correct_linemap,
    _inject_preamble,
    _preamble_line_map,
)
from src.driver.core.compiler import _remap_diagnostics, map_line


WITH_MAIN = (
    "from pymcu.hal.console import print\n"
    "from pymcu.types import uint8\n"
    "\n"
    "BAD: uint8 = undefined_name_here\n"
    "\n"
    "\n"
    "def main():\n"
    "    print(\"A\", BAD)\n"
)

NO_MAIN = (
    "from pymcu.types import uint8\n"
    "\n"
    "x: uint8 = 1\n"
)


def _inject(tmp_path, source):
    entry = tmp_path / "main.py"
    entry.write_text(source, encoding="utf-8")
    synthetic, _ = _inject_preamble(
        entry,
        tmp_path / "_generated",
        comment="# Auto-injected by pymcu build: stdout\n",
        import_line="from pymcu.hal.uart import UART as _pymcu_stdout\n",
        call_line="_pymcu_stdout(115200)",
    )
    return synthetic


def test_every_injected_line_carries_the_sentinel(tmp_path):
    lines = _inject(tmp_path, WITH_MAIN).read_text(encoding="utf-8").splitlines()
    injected = [i for i, line in enumerate(lines, 1)
                if line.rstrip().endswith(INJECTED_MARK.strip())]
    # The header (comment, import, blank) and the call inside def main().
    assert injected == [1, 2, 3, 11]


def test_a_line_above_def_main_keeps_its_own_number(tmp_path):
    mapping = _preamble_line_map(_inject(tmp_path, WITH_MAIN))
    # `BAD: uint8 = ...` is the user's line 4, generated line 7: the header alone shifts it.
    assert mapping[7] == 4


def test_a_line_below_the_inserted_call_is_shifted_by_one_more(tmp_path):
    mapping = _preamble_line_map(_inject(tmp_path, WITH_MAIN))
    # `print("A", BAD)` is the user's line 8, generated line 12: header plus the call.
    assert mapping[12] == 8


def test_an_injected_line_has_no_original(tmp_path):
    mapping = _preamble_line_map(_inject(tmp_path, WITH_MAIN))
    assert mapping[1] is None
    assert mapping[11] is None


def test_a_file_with_no_def_main_shifts_uniformly(tmp_path):
    mapping = _preamble_line_map(_inject(tmp_path, NO_MAIN))
    assert mapping[1:5] == [None, None, None, None]
    assert mapping[5] == 1


def test_a_stacked_second_preamble_composes(tmp_path):
    entry = tmp_path / "main.py"
    entry.write_text(WITH_MAIN, encoding="utf-8")
    first, _ = _inject_preamble(
        entry, tmp_path / "_generated",
        comment="# one\n", import_line="import a\n", call_line="a.init()")
    second, _ = _inject_preamble(
        first, tmp_path / "_generated2",
        comment="# two\n", import_line="import b\n", call_line="b.init()")

    mapping = _preamble_line_map(second)
    text = second.read_text(encoding="utf-8").splitlines()
    # Eight injected lines: two headers of three lines each, and a call inside def main()
    # from each injection.
    injected = sum(1 for line in text if line.rstrip().endswith(INJECTED_MARK.strip()))
    assert injected == 8
    # The user's `BAD:` line survives with its own number whatever stacked above it.
    bad = next(i for i, line in enumerate(text, 1) if line.startswith("BAD:"))
    assert mapping[bad] == 4


def test_the_diagnostic_header_uses_the_map(tmp_path):
    mapping = _preamble_line_map(_inject(tmp_path, WITH_MAIN))
    source = ("dist/_generated/main.py", "src/main.py", mapping)
    text = "dist/_generated/main.py:7:14: error: CompileError: name is not defined\n"
    assert _remap_diagnostics(text, source) == (
        "src/main.py:4:14: error: CompileError: name is not defined\n"
    )


def test_an_injected_line_is_not_renumbered_into_the_users_file(tmp_path):
    mapping = _preamble_line_map(_inject(tmp_path, WITH_MAIN))
    source = ("dist/_generated/main.py", "src/main.py", mapping)
    text = "dist/_generated/main.py:2:1: error: in the injected preamble\n"
    assert _remap_diagnostics(text, source) == (
        "src/main.py:1:1: error: in the injected preamble\n"
    )


def test_the_linemap_is_corrected_per_line(tmp_path):
    mapping = _preamble_line_map(_inject(tmp_path, WITH_MAIN))
    linemap = tmp_path / "linemap.json"
    linemap.write_text(json.dumps([
        {"File": "main.py", "Line": 1},    # injected: dropped
        {"File": "main.py", "Line": 7},    # above def main()
        {"File": "main.py", "Line": 12},   # below the inserted call
        {"File": "other.py", "Line": 7},   # another file: untouched
    ]), encoding="utf-8")

    _correct_linemap(linemap, "main.py", mapping)

    assert json.loads(linemap.read_text(encoding="utf-8")) == [
        {"File": "main.py", "Line": 4},
        {"File": "main.py", "Line": 8},
        {"File": "other.py", "Line": 7},
    ]


def test_a_plain_count_still_works_for_callers_that_pass_one():
    assert map_line(5, 13) == 8
    assert map_line(5, 5) is None
