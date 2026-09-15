# expect: match
# doc: docs/language/roadmap.md:23
from pymcu import inline
@inline
def pair(x) -> (int, int):
    return x, x + 1
a, b = pair(4)
print(a)
print(b)
print("END")
