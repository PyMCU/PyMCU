# expect: match
# doc: docs/language/roadmap.md:85
from pymcu.types import inline
@inline
def inl(x):
    return x + 1
def plain(x):
    return x + 2
print(inl(3))
print(plain(3))
print("END")
