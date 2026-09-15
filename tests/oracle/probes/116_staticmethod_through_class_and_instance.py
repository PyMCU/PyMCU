# expect: match
# doc: docs/language/roadmap.md:92
class Util:
    @staticmethod
    def double(n):
        return n * 2

print(Util.double(3))
u = Util()
print(u.double(3))
print("END")
