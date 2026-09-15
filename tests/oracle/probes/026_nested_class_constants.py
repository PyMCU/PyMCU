# expect: match
# doc: docs/language/roadmap.md:27
class Outer:
    class Inner:
        A = 7
print(Outer.Inner.A)
print("END")
