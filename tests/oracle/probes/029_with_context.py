# expect: match
# doc: docs/language/roadmap.md:30
class Gate:
    def __init__(self):
        self.state = 0
    def __enter__(self):
        self.state = 1
        return self
    def __exit__(self, typ, val, tb):
        self.state = self.state + 2
        return False
g = Gate()
with g as h:
    print(h.state)
print(g.state)
print("END")
