#!/usr/bin/env python3
"""Say when the HAL parity scan is about to measure a layer older than this tree.

The parity jobs install `pymcu-circuitpython` and `pymcu-micropython` from PyPI,
and `hal_parity.py` prefers an installed distribution over a sibling checkout. So
between a freeze and a publish the scan compares a tree that has moved on against
a release that has not, and it fails in the worst possible way: the layer's
already-fixed lines come out as universality violations, and the tree's newer
allowlist entries come out as unused. Four red tests that name files and line
numbers, and not one of them is a parity bug.

That happened on 2026-09-26 with pymcu-circuitpython 0.1.0a2, whose analogio.py
still imported pymcu.chips while the layer's own tree no longer did. Somebody
reading that job would go looking for a parity bug that does not exist.

So the version is compared first, and the job stops here with the two numbers in
one line. The check goes quiet by itself the moment the layer publishes: there is
nothing to remember to undo.

Only an INSTALLED layer is compared. A layer resolved from PYMCU_COMPAT_* or from
a sibling checkout is the developer's own working tree, which has no release
version and is the thing the scan is supposed to measure.

This makes the red honest, it does not remove it. The scan is still answering two
questions in one run, "does THIS tree have parity" and "is the PUBLISHED layer
still compatible", and the second keeps knocking the first over at every release.
Separating them is PyMCU#515, for after beta 1.

    python tools/check_compat_layer_versions.py
"""

from __future__ import annotations

import importlib.metadata
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tests" / "stdlib"))

from packaging.version import InvalidVersion, Version  # noqa: E402

import hal_parity  # noqa: E402


def main() -> int:
    problems = []
    for layer, (_, env_var, dist) in hal_parity.LAYER_SPECS.items():
        resolution = hal_parity.LAYER_RESOLUTIONS[layer]
        if not resolution.origin.startswith("installed"):
            print(f"{dist}: {resolution.origin or 'not resolved'}, no release version to compare")
            continue
        try:
            installed = importlib.metadata.version(dist)
        except importlib.metadata.PackageNotFoundError:
            print(f"{dist}: imported but has no distribution metadata, not compared")
            continue

        expected = hal_parity.LAYER_MINIMUM[layer]
        try:
            older = Version(installed) < Version(expected)
        except InvalidVersion:
            print(f"{dist}: cannot read version {installed!r}, not compared")
            continue

        if older:
            problems.append(
                f"the published {dist} is {installed} and this tree expects >={expected}; "
                f"parity cannot be measured against a layer older than the tree. "
                f"Point the scan at the layer's own checkout with "
                f"{env_var}=<path>/src to measure it now, or wait for the release."
            )
        else:
            print(f"{dist}: {installed} >= {expected}")

    for problem in problems:
        print(problem, file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
