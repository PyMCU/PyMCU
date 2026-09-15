# expect: match
# doc: docs/language/type-system.md:156
from pymcu.types import int8
def band(a: int8, b: int8) -> int8:
    return a & b
def bor(a: int8, b: int8) -> int8:
    return a | b
def bxor(a: int8, b: int8) -> int8:
    return a ^ b
def bnot(a: int8) -> int8:
    return ~a
print(band(-1, 6))
print(bor(-8, 3))
print(bxor(-1, 5))
print(bnot(5))
print("END")
