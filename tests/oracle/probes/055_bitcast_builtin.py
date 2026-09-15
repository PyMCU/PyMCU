# expect: match
# doc: docs/language/roadmap.md:43
from pymcu.types import bitcast, uint32
print(bitcast(uint32, 1.0))
print("END")
