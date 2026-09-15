# expect: match
# doc: docs/language/roadmap.md:15
from pymcu.types import uint16
n: uint16 = 300
c: uint16 = 0
for i in range(n):
    c = c + 1
print(c)
print("END")
