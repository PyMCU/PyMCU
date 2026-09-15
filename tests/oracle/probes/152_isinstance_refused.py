# expect: refuse isinstance() is a Python builtin that PyMCU does not provide
# doc: docs/language/limitations.md (Built-ins summary); enhancement requested in #423/#424
class Base:
    def __init__(self, v: int) -> None:
        self.v = v
class Sub(Base):
    pass
b = Sub(3)
print(isinstance(b, Base))
print("END")
