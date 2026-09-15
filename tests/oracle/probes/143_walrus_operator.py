# expect: match
# doc: docs/language/roadmap.md:23
def double(n: int) -> int:
    return n * 2
def run() -> int:
    total = 0
    n = 3
    if (d := double(n)) > 5:
        total = d
    return total
print(run())
print("END")
