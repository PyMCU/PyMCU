# expect: match
# doc: docs/language/roadmap.md:33
def risky(x):
    if x == 1:
        raise TypeError("t")
    if x == 2:
        raise ValueError("v")
    return x
for x in [1, 2, 3]:
    try:
        print(risky(x))
    except (TypeError, ValueError):
        print(8)
print("END")
