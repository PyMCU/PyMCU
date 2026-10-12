"""The parity scan follows a facade's `from M import name` to the per-chip definition."""

import hal_parity
from hal_parity import Claim, Signature


def _stdlib(tmp_path, monkeypatch, files):
    for rel, text in files.items():
        p = tmp_path / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text)
    monkeypatch.setattr(hal_parity, "STDLIB", tmp_path)
    # parse_source may be cached by path; clear it when it is.
    cache_clear = getattr(hal_parity.parse_source, "cache_clear", None)
    if cache_clear:
        cache_clear()


def test_a_name_a_facade_imports_resolves_to_its_definition(tmp_path, monkeypatch):
    _stdlib(tmp_path, monkeypatch, {
        "hal/avr/pulse/__init__.py":
            "if __CHIP__.name == 'x':\n    from pymcu.hal.avr.pulse.chip import delay, Thing\n",
        "hal/avr/pulse/chip.py":
            "def delay(us):\n    pass\n\nclass Thing:\n    def go(self):\n        pass\n",
    })
    fn = Claim("pulse", "avr", "pymcu.hal.avr.pulse", "delay", "delay")
    cls = Claim("pulse", "avr", "pymcu.hal.avr.pulse", "Thing", "Thing")

    assert isinstance(hal_parity._surface_for_claim(fn), Signature)
    assert hal_parity._surface_for_claim(cls).methods[0][0] == "go"


def test_a_name_defined_in_the_module_itself_is_not_followed(tmp_path, monkeypatch):
    _stdlib(tmp_path, monkeypatch, {
        "hal/avr/pulse/__init__.py": "from pymcu.hal.avr.pulse.chip import delay\n\ndef delay(us, x):\n    pass\n",
        "hal/avr/pulse/chip.py": "def delay(us):\n    pass\n",
    })
    sig = hal_parity._surface_for_claim(Claim("pulse", "avr", "pymcu.hal.avr.pulse", "delay", "delay"))
    assert [p[0] for p in sig.params] == ["us", "x"]


def test_an_aliased_import_or_a_missing_name_is_not_resolved(tmp_path, monkeypatch):
    _stdlib(tmp_path, monkeypatch, {
        "hal/avr/pulse/__init__.py": "from pymcu.hal.avr.pulse.chip import delay as wait\n",
        "hal/avr/pulse/chip.py": "def delay(us):\n    pass\n",
    })
    for name in ("wait", "absent"):
        assert hal_parity._surface_for_claim(
            Claim("pulse", "avr", "pymcu.hal.avr.pulse", name, name)) is None
