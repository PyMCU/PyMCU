# expect: refuse yield
# doc: LANGUAGE_ROADMAP.md:375
class Gen:
    def values(self):
        yield 1
for x in Gen().values():
    print(x)
print("END")
