# expect: refuse method not supported
# doc: docs/language/roadmap.md:65
from pymcu.types import uint8
xs: list[uint8] = list()
xs.append(2)
xs.append(3)
print(xs.index(3))
print("END")
