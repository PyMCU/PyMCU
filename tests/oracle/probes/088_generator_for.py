# expect: match
# doc: docs/language/roadmap.md:69
def gen(n):
    for i in range(n):
        yield i * 2
for x in gen(4):
    print(x)
print("END")
