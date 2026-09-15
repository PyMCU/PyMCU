# expect: refuse is out of range for uint8
# doc: docs/language/type-system.md:189
from pymcu.types import uint8
y: uint8 = 200 + 100
print(y)
print("END")
