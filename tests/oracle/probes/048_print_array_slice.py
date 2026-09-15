# expect: match
# doc: docs/language/roadmap.md:37
from pymcu.types import uint8
buf: uint8[4] = [204, 16, 202, 254]
print(buf[0:2])
print("END")
