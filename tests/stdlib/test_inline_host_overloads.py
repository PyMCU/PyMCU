"""CPython overload dispatch must keep quoted class types distinct from scalars."""

import runpy
from pathlib import Path


types = runpy.run_path(str(Path(__file__).resolve().parents[2] / "lib/src/pymcu/types.py"))
inline, const, uint8 = (types[name] for name in ("inline", "const", "uint8"))


class Channel:
    @inline
    def __init__(self, number: const[uint8]):
        self.number = number

    @inline
    def __init__(self, other: "Channel"):
        self.number = other.number


def test_quoted_class_overload_does_not_capture_an_integer():
    assert Channel(3).number == 3


def test_quoted_class_overload_accepts_an_instance_after_class_creation():
    original = Channel(5)
    assert Channel(original).number == 5
