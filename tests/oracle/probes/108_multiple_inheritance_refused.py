# expect: refuse inheritance
# doc: docs/language/limitations.md:323
class A:
    pass
class B:
    pass
class C(A, B):
    pass
print("END")
