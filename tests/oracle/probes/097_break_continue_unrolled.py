# expect: match
# doc: docs/language/roadmap.md:15
for i in range(5):
    if i == 1:
        continue
    if i == 4:
        break
    print(i)
print("END")
