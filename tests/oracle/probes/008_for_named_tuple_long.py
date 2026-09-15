# expect: match
# doc: docs/language/roadmap.md:17
vals = (1, 2, 3, 4, 5, 6, 7, 8, 9)
s = 0
for x in vals:
    s = s + x
print(s)
print("END")
