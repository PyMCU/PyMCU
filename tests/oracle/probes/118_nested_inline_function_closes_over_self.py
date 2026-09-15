# expect: match
# doc: docs/language/limitations.md:287
# tracked: #427
from pymcu.types import inline

class Counter:
    def __init__(self, start):
        self.value = start
    def bump_twice(self):
        @inline
        def bump():
            self.value = self.value + 1
        bump()
        bump()
        return self.value

c = Counter(5)
print(c.bump_twice())
print("END")
