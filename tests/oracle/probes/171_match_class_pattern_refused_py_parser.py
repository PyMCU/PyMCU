# expect: refuse match pattern MatchClass
# doc: https://github.com/PyMCU/PyMCU/issues/440
# frontend: py-parser
class Point:
    def __init__(self, x: int, y: int) -> None:
        self.x = x
        self.y = y

def describe(p: Point) -> int:
    match p:
        case Point(x=0, y=0):
            return 0
        case Point(x=0):
            return 1
        case Point():
            return 2
print(describe(Point(0, 0)))
print(describe(Point(0, 5)))
print(describe(Point(3, 4)))
print("END")
