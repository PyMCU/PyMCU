# tests/driver/test_stubs.py
#
# `pymcu stubs` turns the installed pymcu packages into `.pyi` files for the
# type checkers that run outside the IDE. The interesting case is PyMCU's own
# overloading: a second `def freq(self, value)` after `def freq(self)` is how
# the language spells an overload, and a stub that copies both verbatim makes
# the second shadow the first -- one signature survives and every call matching
# the other is reported as wrong.

import textwrap

from src.driver.commands.stubs import _stub_source


def stub(source: str, remap_types: bool = False) -> str:
    result = _stub_source(textwrap.dedent(source), "test", remap_types)
    assert result is not None, "the source did not parse"
    return result


class TestRedefinitionOverloads:
    def test_a_redefined_method_becomes_two_overloads(self):
        """`machine.PWM.freq` -- the getter/setter pair the compiler dispatches."""
        out = stub("""
            from pymcu.types import uint16, inline

            class PWM:
                @inline
                def freq(self) -> uint16:
                    return self._freq

                @inline
                def freq(self, value: uint16):
                    self._freq = value
        """)

        assert out.count("@overload") == 2
        assert "def freq(self) -> uint16:" in out
        assert "def freq(self, value: uint16) -> None:" in out
        assert "from typing import overload" in out

    def test_a_redefined_module_level_function_becomes_overloads(self):
        out = stub("""
            from pymcu.types import inline

            @inline
            def sleep(ms: int):
                pass

            @inline
            def sleep(ms: int, us: int):
                pass
        """)

        assert out.count("@overload") == 2

    def test_the_overload_marker_precedes_what_survives_of_the_decorators(self):
        """`@inline` is dropped as an implementation detail; `@overload` is not."""
        out = stub("""
            from pymcu.types import inline

            class Pin:
                @inline
                def value(self) -> int:
                    return 0

                @inline
                def value(self, x: int):
                    pass
        """)

        assert "@inline" not in out
        body = [line.strip() for line in out.splitlines() if line.strip()]
        assert body[body.index("def value(self) -> int:") - 1] == "@overload"

    def test_a_single_definition_is_not_an_overload(self):
        out = stub("""
            class Pin:
                def value(self) -> int:
                    return 0
        """)

        assert "@overload" not in out
        assert "from typing import overload" not in out

    def test_a_property_pair_is_not_an_overload(self):
        """`pwmio.PWMOut.frequency` redefines a name without overloading it.

        Marking the setter `@overload` would make the attribute unassignable.
        """
        out = stub("""
            from pymcu.types import uint16

            class PWMOut:
                @property
                def frequency(self) -> uint16:
                    return self._frequency

                @frequency.setter
                def frequency(self, val: uint16):
                    self._frequency = val
        """)

        assert "@overload" not in out
        assert "@property" in out
        assert "@frequency.setter" in out

    def test_overloads_in_one_class_do_not_leak_into_the_next(self):
        out = stub("""
            class A:
                def f(self) -> int:
                    return 0

                def f(self, x: int):
                    pass

            class B:
                def f(self) -> int:
                    return 0
        """)

        b = out[out.index("class B:"):]
        assert "@overload" not in b


class TestCompatLayerModule:
    """A CircuitPython-layer module: the shape a bare `import pwmio` needs."""

    def test_pwmio_keeps_its_class_its_properties_and_its_imports(self):
        out = stub("""
            from pymcu.chips import __CHIP__
            from pymcu.exceptions import CompileError
            from pymcu.types import uint8, uint16, inline
            if __CHIP__.arch == "avr":
                from pymcu.hal.pwm import PWM as _PWM

            class PWMOut:
                @inline
                def __init__(self, pin_name, *, duty_cycle: uint16 = 0,
                             frequency: uint16 = 500, variable_frequency: uint8 = 0):
                    self._pwm = _PWM(pin_name, duty_cycle, frequency)

                @property
                def frequency(self) -> uint16:
                    return self._frequency

                @frequency.setter
                def frequency(self, val: uint16):
                    if self._variable_freq == 0:
                        raise CompileError("read-only")
                    self._frequency = val

                @inline
                def deinit(self):
                    self._pwm.stop()
        """)

        assert "class PWMOut:" in out
        assert "def __init__(self, pin_name, *, duty_cycle: uint16=0" in out
        assert "@property" in out
        assert "@frequency.setter" in out
        assert "def deinit(self) -> None:" in out
        # The arch-dispatch branch binds `_PWM`, so its import survives.
        assert "from pymcu.hal.pwm import PWM as _PWM" in out
        # A `raise` in a body is discarded with the rest of the body.
        assert "raise" not in out

    def test_private_helpers_stay_out_of_the_stub(self):
        out = stub("""
            class PWMOut:
                def _scale(self, v: int) -> int:
                    return v

                def deinit(self):
                    pass
        """)

        assert "_scale" not in out
        assert "def deinit(self) -> None:" in out


class TestTypeRemap:
    def test_remapped_overloads_keep_their_marker(self):
        out = stub("""
            from pymcu.types import uint16

            class PWM:
                def freq(self) -> uint16:
                    return 0

                def freq(self, value: uint16):
                    pass
        """, remap_types=True)

        assert out.count("@overload") == 2
        assert "def freq(self) -> int:" in out
        assert "def freq(self, value: int) -> None:" in out
