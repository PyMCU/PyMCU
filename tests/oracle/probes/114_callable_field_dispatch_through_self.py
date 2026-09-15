# expect: match
# doc: docs/language/limitations.md:312
# tracked: #425
def double(n):
    return n * 2

class Worker:
    def __init__(self):
        self.cb = double
    def run(self, n):
        return self.cb(n)

w = Worker()
print(w.run(3))
print("END")
