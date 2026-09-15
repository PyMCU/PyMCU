# expect: match
# doc: docs/language/roadmap.md:34
from pymcu.types import int8, uint8
a: int8 = -1
b: uint8 = 200
print(a < b)
print(b > a)
print("END")
