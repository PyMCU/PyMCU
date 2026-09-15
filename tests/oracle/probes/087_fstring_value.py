# expect: match
# doc: docs/language/roadmap.md:68
# tracked: #399
x = 12
s = f"t={x:04d}"
print(s)
print(len(s))
print(s[2])
print("END")
