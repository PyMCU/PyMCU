# expect: refuse unsupported f-string format spec
# doc: docs/language/roadmap.md:36
x = 7
print(f"[{x:>5}]")
print("END")
