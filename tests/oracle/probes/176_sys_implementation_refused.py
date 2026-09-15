# expect: refuse Module not found: sys
# doc: docs/language/limitations.md (no interpreter to introspect)
import sys
print(sys.implementation.name)
print("END")
