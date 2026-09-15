# expect: refuse Generator
# doc: docs/language/limitations.md:502
print(sum(x for x in [1, 2, 3]))
print("END")
