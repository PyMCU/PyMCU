# expect: match
# doc: docs/language/roadmap.md:19
def walk(n):
    for i, x in enumerate(range(2, n)):
        print(i * 100 + x)
walk(5)
print("END")
