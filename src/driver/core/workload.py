# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------

"""workload.yaml -> workload JSON for the PGO profiler.

The YAML is deliberately small: a `scenarios` list, each with a `run` bound
(`{ms: N}`, `{cycles: N}`, `{until: break}`, `{until_uart_bytes: N}` or
`{until_i2c_transactions: N}`), a `stimuli` list of timed pin/UART injections,
hc_sr04 responders or attached I2C slaves (`{i2c_slave: 0x3C}`), and an
optional `expect.uart_tx` / `expect.i2c_tx` prefix. The driver translates it
to the JSON the C# profiler consumes -- no YAML parser ships in C#.
"""

from __future__ import annotations

import re
from pathlib import Path

_PIN_RE = re.compile(r"^[Pp][B-Db-d][0-7]$")
_HEX_RE = re.compile(r"^(?:[0-9a-fA-F]{2})(?:\s+[0-9a-fA-F]{2})*$")


class WorkloadError(ValueError):
    """Malformed workload.yaml; reported to the user as a build-style error."""


def _err(msg: str) -> WorkloadError:
    return WorkloadError(f"workload.yaml: {msg}")


def _num(v, what: str) -> float:
    if isinstance(v, bool) or not isinstance(v, (int, float)):
        raise _err(f"{what} must be a number, got {v!r}")
    return float(v)


def _uart_bytes(v) -> list[int]:
    """uart_rx accepts a text string, a space-separated hex string, or a byte list."""
    if isinstance(v, list):
        out = []
        for b in v:
            if isinstance(b, bool) or not isinstance(b, int) or not 0 <= b <= 255:
                raise _err(f"uart_rx byte out of range: {b!r}")
            out.append(b)
        return out
    if isinstance(v, str):
        # "41 42" is hex; anything else is UTF-8 text.
        if " " in v.strip() and _HEX_RE.match(v.strip()):
            return [int(tok, 16) for tok in v.split()]
        return list(v.encode("utf-8"))
    raise _err(f"uart_rx must be a string, hex bytes, or a byte list, got {v!r}")


def _pin_name(v, what: str) -> str:
    if not isinstance(v, str) or not _PIN_RE.match(v):
        raise _err(f"{what} must look like 'PD2' (port letter B/C/D + pin 0-7), got {v!r}")
    return "P" + v[1].upper() + v[2:]


def _i2c_addr(v, what: str) -> int:
    """i2c_slave accepts a 7-bit address as int (60), hex string ('0x3C') or decimal str."""
    if isinstance(v, bool):
        raise _err(f"{what} must be a 7-bit I2C address, got {v!r}")
    if isinstance(v, int):
        addr = v
    elif isinstance(v, str):
        try:
            addr = int(v, 0)
        except ValueError:
            raise _err(f"{what} must be a 7-bit I2C address, got {v!r}")
    else:
        raise _err(f"{what} must be a 7-bit I2C address, got {v!r}")
    if not 0 < addr < 0x80:
        raise _err(f"{what}: 0x{addr:02X} is outside the 7-bit address range")
    return addr


def _stimulus(raw, i: int) -> dict:
    if not isinstance(raw, dict):
        raise _err(f"stimuli[{i}] must be a mapping, got {raw!r}")
    out: dict = {}
    if "at_us" in raw:
        out["at_us"] = _num(raw["at_us"], f"stimuli[{i}].at_us")
    if "every_us" in raw:
        out["every_us"] = _num(raw["every_us"], f"stimuli[{i}].every_us")

    if "responder" in raw:
        if raw["responder"] != "hc_sr04":
            raise _err(f"stimuli[{i}].responder: only 'hc_sr04' is supported, got {raw['responder']!r}")
        if "trig" not in raw or "echo" not in raw:
            raise _err(f"stimuli[{i}]: hc_sr04 needs 'trig' and 'echo' pins")
        out["responder"] = "hc_sr04"
        out["trig"] = _pin_name(raw["trig"], f"stimuli[{i}].trig")
        out["echo"] = _pin_name(raw["echo"], f"stimuli[{i}].echo")
        out["distance_cm"] = _num(raw.get("distance_cm", 10.0), f"stimuli[{i}].distance_cm")
        if "echo_delay_us" in raw:
            out["echo_delay_us"] = _num(raw["echo_delay_us"], f"stimuli[{i}].echo_delay_us")
        return out

    if "i2c_slave" in raw:
        # A bus device wired to the TWI for the whole scenario: it ACKs its
        # address and returns 0xFF on reads. Not a timed event, so the
        # at_us/every_us requirement below does not apply to it.
        out["i2c_slave"] = _i2c_addr(raw["i2c_slave"], f"stimuli[{i}].i2c_slave")
        return out

    if "uart_rx" in raw:
        out["uart_rx"] = _uart_bytes(raw["uart_rx"])
    if "pin" in raw:
        out["pin"] = _pin_name(raw["pin"], f"stimuli[{i}].pin")
        if "level" in raw:
            lvl = raw["level"]
            if lvl not in (0, 1, True, False):
                raise _err(f"stimuli[{i}].level must be 0 or 1, got {lvl!r}")
            out["level"] = int(lvl)
        if raw.get("toggle"):
            out["toggle"] = True
    if "uart_rx" not in out and "pin" not in out:
        raise _err(f"stimuli[{i}] needs one of: uart_rx, pin, responder, i2c_slave")
    if "at_us" not in out and "every_us" not in out:
        raise _err(f"stimuli[{i}] needs a time: at_us or every_us")
    return out


def _run(raw, name: str) -> dict:
    if raw is None:
        return {"ms": 200.0}
    if not isinstance(raw, dict):
        raise _err(f"scenario '{name}': run must be a mapping, got {raw!r}")
    out: dict = {}
    if "ms" in raw:
        out["ms"] = _num(raw["ms"], f"scenario '{name}'.run.ms")
    if "cycles" in raw:
        c = raw["cycles"]
        if isinstance(c, bool) or not isinstance(c, int) or c < 0:
            raise _err(f"scenario '{name}'.run.cycles must be a non-negative integer")
        out["cycles"] = c
    if "until" in raw:
        if raw["until"] != "break":
            raise _err(f"scenario '{name}'.run.until: only 'break' is supported, got {raw['until']!r}")
        out["until"] = "break"
    if "until_uart_bytes" in raw:
        n = raw["until_uart_bytes"]
        if isinstance(n, bool) or not isinstance(n, int) or n < 1:
            raise _err(f"scenario '{name}'.run.until_uart_bytes must be a positive integer")
        out["until_uart_bytes"] = n
    if "until_i2c_transactions" in raw:
        n = raw["until_i2c_transactions"]
        if isinstance(n, bool) or not isinstance(n, int) or n < 1:
            raise _err(f"scenario '{name}'.run.until_i2c_transactions must be a positive integer")
        out["until_i2c_transactions"] = n
    if "max_ms" in raw:
        out["max_ms"] = _num(raw["max_ms"], f"scenario '{name}'.run.max_ms")
    bounds = [k for k in ("ms", "cycles", "until", "until_uart_bytes",
                         "until_i2c_transactions") if k in out]
    if len(bounds) != 1:
        raise _err(
            f"scenario '{name}'.run needs exactly one of ms/cycles/until/"
            f"until_uart_bytes/until_i2c_transactions, got {bounds or 'none'}")
    return out


def _expect(raw, name: str) -> dict | None:
    if raw is None:
        return None
    if not isinstance(raw, dict):
        raise _err(f"scenario '{name}': expect must be a mapping")
    out: dict = {}
    if "uart_tx" in raw:
        if not isinstance(raw["uart_tx"], str):
            raise _err(f"scenario '{name}'.expect.uart_tx must be a string")
        out["uart_tx"] = raw["uart_tx"]
    if "i2c_tx" in raw:
        # Prefix over the flattened transaction stream: each transaction
        # contributes its address byte followed by its data bytes, so
        # "3c 3c 80 af" pins an empty probe at 0x3C then a 0x80 0xAF write.
        out["i2c_tx"] = _uart_bytes(raw["i2c_tx"])
    return out or None


def _load_yaml():
    """pyyaml is the `pgo` extra, not a driver dependency: `pymcu flash` on a
    venv without it must keep working, so the import happens here, on the
    only path that reads a workload.yaml."""
    try:
        import yaml
    except ModuleNotFoundError as ex:
        raise _err(
            "reading it needs pyyaml, which is the optional 'pgo' extra: "
            "pip install 'pymcu-compiler[pgo]'"
        ) from ex
    return yaml


def parse_workload(text: str) -> dict:
    """Parse workload.yaml text into the normalized workload JSON dict."""
    yaml = _load_yaml()
    try:
        doc = yaml.safe_load(text)
    except yaml.YAMLError as ex:
        raise _err(f"invalid YAML: {ex}") from ex
    if doc is None:
        doc = {}
    if not isinstance(doc, dict):
        raise _err("top level must be a mapping with a 'scenarios' list")
    raw_scenarios = doc.get("scenarios")
    if raw_scenarios is None:
        raise _err("no 'scenarios' list")
    if not isinstance(raw_scenarios, list) or not raw_scenarios:
        raise _err("'scenarios' must be a non-empty list")

    scenarios = []
    for i, raw in enumerate(raw_scenarios):
        if not isinstance(raw, dict):
            raise _err(f"scenarios[{i}] must be a mapping, got {raw!r}")
        name = raw.get("name", f"scenario{i}")
        if not isinstance(name, str):
            raise _err(f"scenarios[{i}].name must be a string")
        stimuli = raw.get("stimuli") or []
        if not isinstance(stimuli, list):
            raise _err(f"scenario '{name}'.stimuli must be a list")
        sc: dict = {
            "name": name,
            "run": _run(raw.get("run"), name),
            "stimuli": [_stimulus(s, j) for j, s in enumerate(stimuli)],
        }
        exp = _expect(raw.get("expect"), name)
        if exp:
            sc["expect"] = exp
        scenarios.append(sc)
    return {"scenarios": scenarios}


def default_workload() -> dict:
    """The workload used when no workload.yaml exists: one 200 ms idle run."""
    return {"scenarios": [{"name": "default", "run": {"ms": 200.0}, "stimuli": []}]}


def load_workload(path: Path | None) -> tuple[dict, bool]:
    """Load *path* (workload.yaml) -> (jsonable dict, was_user_provided)."""
    if path is None or not Path(path).exists():
        return default_workload(), False
    return parse_workload(Path(path).read_text()), True
