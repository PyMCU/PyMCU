# expect: match
# doc: docs/language/roadmap.md:32
from pymcu import inline
def outer():
    total = 1
    @inline
    def add(n):
        nonlocal total
        total = total + n
    add(4)
    add(5)
    return total
print(outer())
print("END")
