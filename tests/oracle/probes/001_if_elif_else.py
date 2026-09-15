# expect: match
# doc: docs/language/roadmap.md:13
x = 7
if x < 3:
    print("low")
elif x < 10:
    print("mid")
else:
    print("high")
print("END")
