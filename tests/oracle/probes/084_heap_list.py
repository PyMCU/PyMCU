# expect: match
# doc: docs/language/roadmap.md:65
# tracked: #398
from pymcu.types import uint8
xs: list[uint8] = list()
xs.append(2)
xs.append(3)
print(len(xs))
print(xs[0] + xs[1])
print("END")
