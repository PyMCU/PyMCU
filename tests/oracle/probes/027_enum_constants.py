# expect: match
# doc: docs/language/roadmap.md:29
# tracked: #400
from enum import Enum
class Mode(Enum):
    OFF = 0
    ON = 1
print(Mode.ON.value)
print("END")
