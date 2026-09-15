# expect: match
# doc: docs/language/roadmap.md:20
for a, b in zip([1, 2, 3], [9, 8, 7]):
    print(a * 10 + b)
print("END")
