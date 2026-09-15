# tests/stdlib/conftest.py
#
# Puts this directory on sys.path so test modules can import sibling helper
# modules (e.g. hal_parity.py) by plain name, the way pytest test files
# themselves are discovered.

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
