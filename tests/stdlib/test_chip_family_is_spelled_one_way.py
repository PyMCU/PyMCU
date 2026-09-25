"""Every chip family is spelled ONE way across the stdlib.

A HAL facade picks its per-chip implementation with a module-level ladder:

    if __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25":
        from pymcu.hal.avr.timer.attiny85 import ...
    else:
        from pymcu.hal.avr.timer.atmega328p import ...

Ten families are written this way in twenty-four places. They agree today, so this test
fixes no bug; it pins a hand-maintained correspondence nobody can check by reading. Adding
a chip to a family means editing every repetition, and forgetting one does not break the
build: the condition falls through to the `else` and compiles another part's
implementation. That is the shape the project already paid for with the ATtiny register
maps, where a file copied from a sibling was wrong in six families for months.

Folding the repetitions into one shared table was measured and rejected. A predicate bound
to a name in ANOTHER module stops folding: the same three-line program compiles to 124
bytes with the condition inline, 148 with the predicate bound to a name in the same file
(the branch folds, but a dead 24-byte variable stays), and 168 with it bound in another
module, where the compile-time decision becomes a `module_init` call, a runtime branch and
a variable read. On the real ladders, which select IMPORTS, both implementations would be
compiled instead of one. So the repetition stays and this test guards it.

ONE failure shape is asserted, deliberately. "A family written two ways" has no false
positives by construction. "A repetition missing a member", inferred from one condition's
chip set being a subset of another's, was implemented and measured: three warnings, all
three legitimate distinct partitions (EEPROM size classes, the ATtiny parts that share a
converter), because different modules group chips by different properties. A test that
cries wolf is worse than no test, so that shape is not asserted and should not be added.
"""

import collections
import re
from pathlib import Path

STDLIB = Path(__file__).resolve().parents[2] / "lib" / "src" / "pymcu"

# A module-level `if` / `elif` whose condition is a chain of `__CHIP__.name` equalities.
_LADDER = re.compile(r"^\s*(?:el)?if\s+(__CHIP__\.name\s*==.*?):\s*$")
_CHIP = re.compile(r'__CHIP__\.name\s*==\s*"([a-z0-9]+)"')


def _family_ladders():
    """Yield (file, line, members, spelling) for every chip-family condition."""
    for path in sorted(STDLIB.rglob("*.py")):
        for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            match = _LADDER.match(line)
            if match is None:
                continue
            condition = match.group(1)
            members = _CHIP.findall(condition)
            if len(members) < 2:
                continue
            # Only a plain chain of equalities joined by `or`. A condition that also tests
            # `arch`, negates, or spans lines is not a family list and is left alone.
            canonical = " or ".join('__CHIP__.name == "%s"' % name for name in members)
            if re.sub(r"\s+", " ", condition).strip() != canonical:
                continue
            yield path.relative_to(STDLIB).as_posix(), lineno, frozenset(members), tuple(members)


def test_each_chip_family_is_spelled_one_way():
    by_family = collections.defaultdict(list)
    for file, lineno, members, spelling in _family_ladders():
        by_family[members].append((file, lineno, spelling))

    assert by_family, (
        "no chip-family ladder was found at all. Either the pattern moved or this test's "
        "regex no longer matches it; a silently empty sweep is the one way this test can "
        "pass without checking anything."
    )

    divergent = []
    for members, uses in sorted(by_family.items(), key=lambda kv: sorted(kv[0])):
        spellings = {spelling for _, _, spelling in uses}
        if len(spellings) == 1:
            continue
        divergent.append(
            "  {%s} is written %d ways:\n%s"
            % (
                ", ".join(sorted(members)),
                len(spellings),
                "\n".join(
                    "      %s:%d  %s" % (file, line, " or ".join(spelling))
                    for file, line, spelling in sorted(uses)
                ),
            )
        )

    assert not divergent, (
        "a chip family is spelled more than one way, so a reader cannot tell whether the "
        "difference is deliberate:\n" + "\n".join(divergent)
    )
