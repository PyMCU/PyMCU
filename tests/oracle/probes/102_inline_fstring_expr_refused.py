# expect: refuse f-string
# doc: docs/language/limitations.md:53
def use(s):
    print(s)
x = 7
use(f"x={x}")
print("END")
