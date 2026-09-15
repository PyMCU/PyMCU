# expect: match
# doc: docs/language/roadmap.md:15
s = 0
for i in range(5, -4, -3):
    print(i)
    s = s + i
print(s)
print("END")
