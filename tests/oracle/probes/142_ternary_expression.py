# expect: match
# doc: docs/language/roadmap.md:23
def sign(n: int) -> int:
    return 1 if n >= 0 else -1
print(sign(5))
print(sign(-5))
print("END")
