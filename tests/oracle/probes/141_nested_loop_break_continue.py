# expect: match
# doc: docs/language/roadmap.md:14
def run() -> int:
    total = 0
    for i in range(3):
        for j in range(3):
            if j == 1:
                continue
            if j == 2:
                break
            total = total + i * 10 + j
    return total
print(run())
print("END")
