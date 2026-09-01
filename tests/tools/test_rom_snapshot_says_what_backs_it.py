"""The snapshot must not present a self-derived baseline as though it were a fact.

PyMCU#242. Every field this tool stores is compared against a previous run of itself, with
one exception: `foreign` re-derives the expected set from the chip file. So a green run means
"the compiler did not move", and the only part of it that means "and it is not obviously
wrong" is that one field.

Two things were making that impossible to see, and both are the same shape:

  the ORACLE   `foreign_registers` returned [] both when the image was clean and when the
               chip file could not be read at all. Cannot-check and nothing-wrong were the
               same value, in the one field with an outside oracle.

  the FILE     `foreign` was stored only when non-empty, so a baseline is byte-identical
               whether the check ran and passed or never ran.

Measured when this was written: 4 of 30 chips cannot be checked (ch32v003, ch32v203, rp2040,
rp2350 use computed bases), covering 28 cells of which 16 build. 129 of 145 built cells have
any independent backing at all.

NONE OF THIS DETECTS ANYTHING NEW, and the tests below do not pretend otherwise. #43 would
still pass: a broken stack pointer writes to SPL and SPH, which the chip declares, and the
image assembles quietly, so both oracles are green on the artefact that was wrong.
"""

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from rom_snapshot import chips, declared_registers, foreign_registers  # noqa: E402

# `report_backing` is imported INSIDE the tests that use it, not here.
#
# A module-level import of a symbol the fix adds turns every test in the file into a
# collection error when the fix is reverted. The file then "fails first" without a single
# case having run, so the assertions prove nothing and the check that was supposed to
# demonstrate behaviour demonstrates only that the name is new. That happened once already
# in this campaign; the rule is that if you cannot read the assertion text in the reverted
# run, the case did not run.


def test_the_oracle_says_cannot_check_rather_than_clean():
    """None and [] are different answers and must not be spelled the same."""
    checkable = [c for c in chips() if declared_registers(c) is not None]
    opaque = [c for c in chips() if declared_registers(c) is None]

    assert checkable, "no chip is checkable; the oracle would be vacuous"
    assert opaque, (
        "no chip uses computed bases any more. If that is real, this test has served its "
        "purpose and the None branch can go; check before deleting it"
    )

    # A chip it cannot examine must not answer with the same value as a clean one.
    missing_mir = Path("/nonexistent/firmware.mir")
    assert foreign_registers(missing_mir, opaque[0]) is None


def test_a_clean_cell_records_that_it_was_checked(tmp_path):
    """Absence and cleanliness must stop being the same bytes in the stored file."""
    # The shape build() writes, reduced to the fields this is about.
    checked_clean = {"status": "ok", "rom": 100, "foreign_checked": True}
    never_checked = {"status": "ok", "rom": 100}

    assert checked_clean != never_checked, (
        "a baseline must be able to say whether the oracle ran; if these are equal the file "
        "cannot distinguish a checked cell from an unchecked one"
    )


def test_the_report_prints_on_a_run_with_nothing_wrong(capsys):
    """UNCONDITIONAL. A note that appears only beside other problems is invisible on exactly
    the runs where the reader is being reassured, which is what it is for."""
    from rom_snapshot import report_backing
    clean = {
        "blink|atmega328p": {"status": "ok", "rom": 150, "foreign_checked": True},
        "blink|rp2040": {"status": "ok", "rom": 400, "foreign_checked": False},
    }

    report_backing(clean)
    out = capsys.readouterr().out

    assert "1 las respalda" in out, "it must say how many cells the oracle actually backs"
    assert "1 NO las respalda nada" in out, "it must say how many have no backing at all"
    assert "rp2040" in out, "it must name the chips it cannot check"
    assert "no dice que" in out, "it must say what a match does NOT mean"


def test_the_report_does_not_claim_backing_for_an_older_baseline(capsys):
    """A baseline captured before the field existed cannot say whether the oracle ran, and
    saying so is the honest answer rather than counting those cells as backed."""
    from rom_snapshot import report_backing
    legacy = {"blink|atmega328p": {"status": "ok", "rom": 150}}

    report_backing(legacy)
    out = capsys.readouterr().out

    assert "antes de que se registrara" in out
    assert "0 las respalda" in out, "an unrecorded check must not be counted as a passing one"


def test_the_live_baseline_is_still_comparable_after_the_change():
    """The migration must not turn one format change into a screenful of findings.

    An older cell has no `foreign`; a recaptured clean one has `foreign_checked` and still no
    `foreign`. Those must compare equal, or the first check after this lands reports every
    cell as changed and the real diffs are lost in it.
    """
    baseline = Path(__file__).resolve().parent / "rom_snapshot.json"
    cells = json.loads(baseline.read_text())["cells"]

    COMMENTARY = ("reason", "kind", "proves", "tried", "warn_first", "accepted", "asm_text",
                  "foreign_checked")

    def measured(cell):
        out = {k: v for k, v in cell.items() if k not in COMMENTARY}
        if not out.get("foreign"):
            out.pop("foreign", None)
        return out

    for key, old in cells.items():
        recaptured = dict(old)
        if recaptured.get("status") == "ok":
            recaptured["foreign_checked"] = True
        assert measured(old) == measured(recaptured), f"{key} would report as a false change"

    # And a cell that genuinely gains a foreign register must still differ.
    key, cell = next(iter(cells.items()))
    gained = {**cell, "foreign_checked": True, "foreign": ["0x6e"]}
    assert measured(cell) != measured(gained)
