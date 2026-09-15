# expect: refuse Nested function
# doc: docs/language/roadmap.md:85
def outer(x: int) -> int:
    def inner(y: int) -> int:
        return y + 1
    return inner(x) * 2
print(outer(5))
print("END")
