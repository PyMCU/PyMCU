# expect: divergence docs/language/type-system.md:20
# doc: docs/language/roadmap.md:67
d = {0: 10, "mid": 2}
print(0 in d)
print(99 in d)
print("mid" in d)
print("END")
