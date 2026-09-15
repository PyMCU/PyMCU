# expect: divergence docs/language/limitations.md:372
# doc: docs/language/limitations.md:372
#
# PyMCU#441: `setup` is called directly from __init__, so `self.x` is a real field of C (the
# fix this probe guards). But `check`, ALSO called directly from __init__, runs FIRST and
# reads `self.x` before `setup` ever writes it -- attribute existence is resolved dynamically,
# per instance, by execution order in CPython/MicroPython/CircuitPython, so this read raises
# AttributeError there. PyMCU cannot see that ordering statically: it prints the
# zero-initialized default instead.
class C:
    def __init__(self, flag: bool):
        self.check(flag)
        self.setup()

    def check(self, flag: bool):
        if flag:
            print(self.x)

    def setup(self):
        self.x = 5


try:
    c = C(True)
except Exception:
    print("AttributeError")
print("END")
