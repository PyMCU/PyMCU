# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

"""
Reading and changing [tool.pymcu] without disturbing the rest of the file.

pyproject.toml is the user's file: their comments, their ordering, their
formatting. Every write here goes through tomlkit for that reason, and touches
only the keys that were asked for.

The rules enforced are the ones the build enforces, applied earlier: `board` and
`target` are mutually exclusive, and a project has at most one compat layer.
Catching them here means a bad combination never reaches a build.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

import tomlkit

from .boards import (
    BOARD_CHIPS,
    BOARD_GROUPS,
    board_frequency,
    board_label,
    chip_label,
    default_frequency,
    default_programmer,
    default_toolchain,
    extension_board_chips,
    resolve_chip_for_board,
)

LAYERS = ("native", "micropython", "circuitpython")


@dataclass
class ConfigChange:
    ok: bool
    message: str
    changed: dict = field(default_factory=dict)


def describe(doc: tomlkit.TOMLDocument, root: Path) -> dict:
    """
    Everything [tool.pymcu] says about this project, resolved.

    Derived values (the chip a board implies, the toolchain that follows from
    the chip) are marked as derived, because a field someone cannot edit should
    not look like one they forgot to fill in.
    """
    cfg = doc.get("tool", {}).get("pymcu", {})
    layers = [str(f) for f in cfg.get("stdlib", [])]
    board = str(cfg.get("board", "") or "")
    target = str(cfg.get("target", "") or cfg.get("chip", "") or "")
    chip = resolve_chip_for_board(board, extension_board_chips(layers)) or "" if board else target

    frequency = cfg.get("frequency")
    flash_cfg = cfg.get("flash", {}) or {}

    return {
        "board": board,
        "board_label": board_label(board) if board else "",
        "target": target,
        "chip": chip,
        "chip_label": chip_label(chip) if chip else "",
        "layer": layers[0] if layers else "native",
        "layers": layers,
        "frequency": int(frequency) if frequency else (board_frequency(board) if board else 0),
        "frequency_explicit": frequency is not None,
        "sources": str(cfg.get("sources", "src")),
        "entry": str(cfg.get("entry", "main.py")),
        "programmer": str(flash_cfg.get("programmer", "") or (default_programmer(chip) if chip else "")),
        "programmer_explicit": bool(flash_cfg.get("programmer")),
        "toolchain": str((cfg.get("toolchain", {}) or {}).get("name", "")
                         or (default_toolchain(chip) if chip else "")),
        "sources_exist": (root / str(cfg.get("sources", "src"))).is_dir(),
        "entry_exists": (root / str(cfg.get("sources", "src"))
                         / str(cfg.get("entry", "main.py"))).is_file(),
    }


def available_boards(layers: list[str] | None = None) -> list[dict]:
    """Known boards, grouped as `pymcu new` groups them, plus what each implies."""
    extra = extension_board_chips(layers or [])
    known = {**BOARD_CHIPS, **extra}

    groups: list[dict] = []
    seen: set[str] = set()
    for group, names in BOARD_GROUPS.items():
        boards = []
        for name in names:
            chip = known.get(name)
            if not chip:
                continue
            seen.add(name)
            boards.append({
                "name": name,
                "label": board_label(name),
                "chip": chip,
                "chip_label": chip_label(chip),
                "frequency": board_frequency(name),
            })
        if boards:
            groups.append({"group": group, "boards": boards})

    # Boards contributed by a compat package are not in BOARD_GROUPS.
    rest = [
        {
            "name": name,
            "label": board_label(name),
            "chip": chip,
            "chip_label": chip_label(chip),
            "frequency": board_frequency(name),
        }
        for name, chip in sorted(extra.items()) if name not in seen
    ]
    if rest:
        groups.append({"group": "From the compat layer", "boards": rest})
    return groups


def apply_changes(path: Path, doc: tomlkit.TOMLDocument, *,
                  board: str | None = None,
                  frequency: int | None = None,
                  layer: str | None = None,
                  sources: str | None = None,
                  entry: str | None = None) -> ConfigChange:
    """Write the requested keys into [tool.pymcu]. Returns what actually changed."""
    tool = doc.setdefault("tool", tomlkit.table())
    cfg = tool.setdefault("pymcu", tomlkit.table())
    changed: dict = {}

    if layer is not None:
        if layer not in LAYERS:
            return ConfigChange(False, f"Unknown layer '{layer}'. Pick one of: {', '.join(LAYERS)}.")
        if layer == "native":
            if "stdlib" in cfg:
                del cfg["stdlib"]
                changed["layer"] = "native"
        else:
            arr = tomlkit.array()
            arr.append(layer)
            cfg["stdlib"] = arr
            changed["layer"] = layer

    if board is not None:
        layers = [str(f) for f in cfg.get("stdlib", [])]
        chip = resolve_chip_for_board(board, extension_board_chips(layers))
        if chip is None:
            return ConfigChange(False, f"Unknown board '{board}'.")
        cfg["board"] = board
        # The build refuses a project that sets both, so setting one clears the
        # other rather than leaving a file that cannot be built.
        cfg.pop("target", None)
        cfg.pop("chip", None)
        changed["board"] = board
        changed["chip"] = chip

        # A frequency left from another board is worse than no frequency: it
        # compiles, and every delay is wrong. Follow the board unless this same
        # call sets one explicitly.
        if frequency is None:
            cfg["frequency"] = board_frequency(board)
            changed["frequency"] = int(cfg["frequency"])

    if frequency is not None:
        if frequency <= 0:
            return ConfigChange(False, "Frequency must be a positive number of hertz.")
        cfg["frequency"] = frequency
        changed["frequency"] = frequency

    if sources is not None:
        cfg["sources"] = sources
        changed["sources"] = sources

    if entry is not None:
        cfg["entry"] = entry
        changed["entry"] = entry

    if not changed:
        return ConfigChange(True, "Nothing to change.")

    path.write_text(tomlkit.dumps(doc), encoding="utf-8")
    return ConfigChange(True, "pyproject.toml updated.", changed)
