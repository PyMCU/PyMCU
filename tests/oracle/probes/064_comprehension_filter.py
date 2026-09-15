# expect: match
# doc: docs/language/roadmap.md:54
xs = [x for x in [1, 2, 3, 4] if x > 2]
for v in xs:
    print(v)
print("END")
