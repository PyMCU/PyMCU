# expect: match
# doc: docs/language/roadmap.md:14
i = 0
while i < 3:
    print(i)
    i = i + 1
else:
    print(9)
print("END")
