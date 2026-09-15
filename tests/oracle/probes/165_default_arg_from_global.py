# expect: match
# doc: docs/language/roadmap.md:23
GAIN = 4
def scale(x, mul=GAIN):
    return x * mul
print(scale(3))
print("END")
