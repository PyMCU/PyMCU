# Example: tuple-ops

Demonstrates tuple literals, multi-return functions, tuple unpacking, and `enumerate`.

## Multi-return divmod

```python
from whisnake.types import uint8

def divmod8(a: uint8, b: uint8) -> (uint8, uint8):
    q: uint8 = a // b
    r: uint8 = a - q * b
    return (q, r)

def main():
    q, r = divmod8(10, 3)    # q=3, r=1
    # q and r are allocated to registers — zero SRAM cost
```

## Enumerate over an array

```python
from whisnake.types import uint8

def main():
    buf: uint8[4] = [10, 20, 30, 40]

    for i, val in enumerate(buf):
        # i is a compile-time counter; val is buf[i]
        process(i, val)
```

## Key points

- Tuple return values are allocated to synthetic named registers (one per element), not the stack.
- The optimizer constant-folds `divmod8(10, 3)` to `q=3, r=1` at compile time when operands are
  known.
- `enumerate()` is a compile-time loop sugar — it does not create a tuple object at runtime.
