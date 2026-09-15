# expect: match
# tracked: #386
# doc: isinstance() folds at compile time since #424; the remaining mismatch is #386 (a computed bool prints 1, CPython prints True)
class Base:
    def __init__(self, v: int) -> None:
        self.v = v
class Sub(Base):
    pass
b = Sub(3)
print(isinstance(b, Base))
print("END")
