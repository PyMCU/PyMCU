# expect: divergence docs/language/type-system.md:20
# doc: LANGUAGE_ROADMAP.md:79
def to_int(x: float) -> int:
    return int(x)
def to_float(x: int) -> float:
    return float(x)
def to_bool(x: int) -> bool:
    return bool(x)
print(to_int(3.9))
print(to_int(-3.9))
print(to_float(3))
print(to_bool(5))
print(to_bool(0))
print("END")
