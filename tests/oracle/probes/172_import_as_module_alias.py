# expect: match
# doc: docs/language/roadmap.md:34
# tracked: #449
import pymcu.types as t
x: t.uint8 = 200
y: t.uint8 = 100
print(x + y)
print("END")
