# expect: match
# doc: docs/language/roadmap.md:34
from pymcu.types import uint8
def ops(x: uint8) -> uint8:
    x += 3
    x -= 1
    x *= 2
    x //= 3
    x %= 5
    x <<= 1
    x >>= 1
    x &= 0x0F
    x |= 0x10
    x ^= 0x01
    return x
print(ops(10))
print("END")
