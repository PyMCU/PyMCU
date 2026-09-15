# expect: match
# doc: docs/language/roadmap.md:65
# tracked: #398
from pymcu.types import uint8
xs: list[uint8] = list()
xs.append(2)
xs.append(3)
xs.append(4)
total = 0
for v in xs:
    total = total + v
print(total)
print("END")
