from typing import NewType, Generic, TypeVar

T = TypeVar("T")

class ptr(Generic[T]):
    def __init__(self, address: int):
        self.address = address

    def __add__(self, other):
        return ptr(self.address + other)

    def __set__(self, instance, value):
        raise RuntimeError(
            "⚠️ Error: You're trying to write to a hardware register "
            "while running Python on your computer.\n"
            "This code must be compiled with 'pymcuc' and run on the microcontroller."
        )

    def __getitem__(self, bit: int) -> bool:
        raise RuntimeError("Bit checking only works in compiled code")

    def __setitem__(self, bit: int, value: int):
        raise RuntimeError("Bit manipulation only works in compiled code")


# Phantom types (type-level aliases used only for static typing)
uint8 = NewType("uint8", int)
int8 = NewType("int8", int)
uint16 = NewType("uint16", int)
int16 = NewType("int16", int)
uint32 = NewType("uint32", int)
int32 = NewType("int32", int)