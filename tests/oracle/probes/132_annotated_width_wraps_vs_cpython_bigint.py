# expect: divergence docs/language/type-system.md:242
# doc: docs/language/type-system.md:242
from pymcu.types import uint8
def echo(x: uint8) -> uint8:
    return x
print(echo(300))
print("END")
