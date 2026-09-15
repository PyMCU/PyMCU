# expect: refuse not supported
# doc: docs/language/limitations.md:500
d = {x: x + 1 for x in [1, 2, 3]}
print(d[1])
print("END")
