# expect: match
# doc: docs/language/roadmap.md:23
def scale(x, mul=2, add=1):
    return x * mul + add
print(scale(3))
print(scale(3, add=5))
print(scale(3, mul=4, add=2))
print("END")
