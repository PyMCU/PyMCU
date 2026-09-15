# expect: divergence docs/language/type-system.md:20
# doc: docs/language/type-system.md:20
def check(a: int, b: int, c: int) -> bool:
    return a < b < c
print(check(1, 2, 3))
print(check(1, 5, 3))
print("END")
