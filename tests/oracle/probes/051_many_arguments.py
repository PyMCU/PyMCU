# expect: match
# doc: docs/language/roadmap.md:39
def pack(a, b, c, d, e, f, g):
    return a + b * 10 + c * 100 + d + e + f + g
print(pack(1, 2, 3, 4, 5, 6, 7))
print("END")
