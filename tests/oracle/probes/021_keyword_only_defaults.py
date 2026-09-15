# expect: match
# doc: docs/language/roadmap.md:23
def tune(x, *, gain=2, offset=1):
    return x * gain + offset
print(tune(5))
print(tune(5, offset=9))
print(tune(5, gain=3, offset=4))
print("END")
