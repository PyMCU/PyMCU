# expect: refuse yield
# doc: LANGUAGE_ROADMAP.md:375
def gen():
    x = (yield 1)
    yield x
for x in gen():
    print(x)
print("END")
