# tests/driver/test_programmers.py
#
# Programmer discovery through the `pymcu.programmers` entry-point group.
#
# The driver's own pk2cmd implementation is retired: PIC programmers ship with
# pymcu-pic now (pk2cmd for PICkit 2/3 and PKOB, pymcuprog for the Curiosity
# Nano nEDBG). pymcu-pic deliberately reuses the name "pk2cmd", and whenever
# two distributions register the same name the plugin has to win by design --
# the alternative is the driver's entry point winning or losing on whichever
# order dist-info scanning happens to return, and the PICkit 3 the plugin
# drives refused by an implementation that never spoke its protocol.

import pytest
from rich.console import Console

from src.driver.programmers import get_programmer


class _Dist:
    def __init__(self, name):
        self.name = name


class _EntryPoint:
    """The slice of importlib.metadata.EntryPoint discovery reads."""

    def __init__(self, name, cls, dist_name):
        self.name = name
        self.value = f"{cls.__module__}:{cls.__name__}"
        self.group = "pymcu.programmers"
        self.dist = _Dist(dist_name)
        self._cls = cls

    def load(self):
        return self._cls


class _DriverPk2cmd:
    """Stands in for the driver's retired built-in."""

    def __init__(self, console):
        self.console = console


class _PluginPk2cmd:
    """Stands in for pymcu-pic's pk2cmd."""

    def __init__(self, console):
        self.console = console


def _patch_eps(monkeypatch, eps):
    monkeypatch.setattr(
        "src.driver.programmers.entry_points", lambda **kw: list(eps)
    )


class TestSameNameTie:
    @pytest.mark.parametrize("driver_first", [True, False])
    def test_the_plugin_wins_regardless_of_scan_order(
        self, monkeypatch, driver_first
    ):
        # pymcu-pic's pk2cmd is registered under the name the driver's built-in
        # used to claim; which one entry_points() happens to return first must
        # not decide the outcome.
        own = _EntryPoint("pk2cmd", _DriverPk2cmd, "pymcu-compiler")
        plugin = _EntryPoint("pk2cmd", _PluginPk2cmd, "pymcu-pic")
        eps = [own, plugin] if driver_first else [plugin, own]
        _patch_eps(monkeypatch, eps)
        assert isinstance(get_programmer("pk2cmd", Console()), _PluginPk2cmd)

    def test_the_drivers_own_ep_still_resolves_when_it_is_the_only_match(
        self, monkeypatch
    ):
        _patch_eps(
            monkeypatch, [_EntryPoint("pk2cmd", _DriverPk2cmd, "pymcu-compiler")]
        )
        assert isinstance(get_programmer("pk2cmd", Console()), _DriverPk2cmd)


class TestBuiltinFallback:
    def test_avrdude_and_ipecmd_resolve_without_dist_info(self, monkeypatch):
        # A source checkout has no dist-info, so the built-ins are reached by
        # name. pk2cmd is deliberately not one of them.
        _patch_eps(monkeypatch, [])
        assert (
            type(get_programmer("avrdude", Console())).__name__
            == "AvrdudeProgrammer"
        )
        assert (
            type(get_programmer("ipecmd", Console())).__name__
            == "IpecmdProgrammer"
        )

    def test_pk2cmd_is_unknown_without_pymcu_pic(self, monkeypatch):
        # No built-in and no plugin: the name must not resolve, so `pymcu
        # flash` can point at the package that provides it.
        _patch_eps(monkeypatch, [])
        assert get_programmer("pk2cmd", Console()) is None


class TestRegisteredProgrammers:
    def test_lists_plugins_and_builtins(self, monkeypatch):
        from src.driver.programmers import registered_programmers

        _patch_eps(
            monkeypatch,
            [
                _EntryPoint("pk2cmd", _PluginPk2cmd, "pymcu-pic"),
                _EntryPoint("pymcuprog", _PluginPk2cmd, "pymcu-pic"),
            ],
        )
        assert registered_programmers() == [
            "avrdude",
            "ipecmd",
            "pk2cmd",
            "pymcuprog",
        ]
