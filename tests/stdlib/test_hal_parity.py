"""Cross-backend HAL API parity, checked from source with ast only."""

import re

import pytest

from hal_parity import (
    LAYERS,
    api_violations,
    allowlist,
    facade_claims,
    universality_violations,
)


def _allowed(symbol):
    allowed = allowlist()
    if symbol in allowed:
        return allowed[symbol]
    for allowed_symbol, item in allowed.items():
        if allowed_symbol.endswith("*") and symbol.startswith(allowed_symbol[:-1]):
            return item
    if symbol.startswith("compat."):
        parts = symbol.split("/")
        while len(parts) > 1:
            wildcard = "/".join([parts[0], *parts[1:-1], "*"])
            if wildcard in allowed:
                return allowed[wildcard]
            parts.pop()
    return None


def _params(cases, prefix):
    params = []
    for case in cases:
        allowed = _allowed(case.symbol)
        marks = []
        if allowed is not None:
            marks.append(pytest.mark.xfail(strict=True, reason=f"{allowed.status}: {allowed.reason}"))
        params.append(pytest.param(case, id=f"{prefix}:{case.symbol}", marks=marks))
    return params


API_CASES = _params(api_violations(), "api")
UNIVERSALITY_CASES = _params(universality_violations(), "universality")


@pytest.mark.parametrize("violation", API_CASES)
def test_hal_facade_claims_keep_the_same_api(violation):
    assert False, violation.detail


@pytest.mark.parametrize("violation", UNIVERSALITY_CASES)
def test_compat_layers_do_not_contain_chip_particulars(violation):
    assert False, (
        f"{violation.layer}:{violation.path}:{violation.line}: "
        f"{', '.join(violation.kinds)} ({violation.detail}) belongs in the native HAL"
    )


@pytest.mark.parametrize("missing_layers", [
    (),
    ("circuitpython",),
    ("micropython",),
    ("circuitpython", "micropython"),
])
def test_hal_parity_allowlist_entries_are_used_and_tracked(missing_layers, monkeypatch, tmp_path):
    for layer in missing_layers:
        monkeypatch.setitem(LAYERS, layer, tmp_path / layer)
    seen = {v.symbol for v in api_violations()}
    seen.update(v.symbol for v in universality_violations())
    # Only require usage for layers that were scanned; still validate every
    # entry's tracking metadata, including those for absent sibling repos.
    missing_prefixes = tuple(
        f"compat.{layer}." for layer, root in LAYERS.items() if not root.exists()
    )
    unused = []
    malformed = []
    for symbol, item in allowlist().items():
        if not symbol.startswith(missing_prefixes):
            if symbol.endswith("*"):
                prefix = symbol[:-1]
                if not any(v.startswith(prefix) for v in seen):
                    unused.append(symbol)
            elif symbol not in seen:
                unused.append(symbol)
        if item.status != "documented" and not re.fullmatch(r"tracked:#\d+", item.status):
            malformed.append(f"{symbol} has status {item.status!r}")
        if item.status == "documented" and not item.quote:
            malformed.append(f"{symbol} is documented but has no quote")
    assert not unused, "unused HAL parity allowlist entries: " + ", ".join(sorted(unused))
    assert not malformed, "; ".join(malformed)


def test_hal_claim_scan_is_not_empty():
    claims = facade_claims()
    assert len(claims) >= 20
    assert {claim.peripheral for claim in claims} >= {"gpio", "uart", "pwm", "adc", "i2c", "spi"}
