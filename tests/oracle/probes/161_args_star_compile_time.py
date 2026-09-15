# expect: match
# doc: docs/language/roadmap.md:23
def total(*args):
    s = 0
    for a in args:
        s = s + a
    return s
print(total(1, 2, 3))
print("END")
