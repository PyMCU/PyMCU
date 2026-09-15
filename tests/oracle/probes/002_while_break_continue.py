# expect: match
# doc: docs/language/roadmap.md:14
i = 0
s = 0
while i < 8:
    i = i + 1
    if i == 3:
        continue
    if i == 7:
        break
    s = s + i
print(s)
print("END")
