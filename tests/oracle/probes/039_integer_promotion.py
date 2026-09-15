# expect: match
# doc: docs/language/roadmap.md:34
from pymcu.types import uint8
x: uint8 = 255
y: uint8 = 45
print(x + y)
print("END")
