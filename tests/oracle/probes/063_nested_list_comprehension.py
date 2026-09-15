# expect: match
# doc: docs/language/roadmap.md:54
xs = [x * 10 + y for x in [1, 2] for y in [3, 4]]
for v in xs:
    print(v)
print("END")
