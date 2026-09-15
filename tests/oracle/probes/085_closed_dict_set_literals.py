# expect: divergence docs/language/type-system.md:20
# doc: docs/language/roadmap.md:66
d = {0: 10, "mid": 2}
print(d[0])
print(d["mid"])
print(3 in {1, 3, 5})
print(len(d))
print("END")
