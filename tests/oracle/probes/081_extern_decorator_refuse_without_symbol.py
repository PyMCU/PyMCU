# expect: refuse undefined reference
# doc: docs/language/roadmap.md:62
from pymcu.ffi import extern
from pymcu.types import uint8
@extern("oracle_missing_symbol")
def missing() -> uint8:
    pass
print(missing())
print("END")
