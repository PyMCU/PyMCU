# Exception type codes (uint8 constants).
# Used with try/except and raise in PyMCU programs.
ValueError: const = 1
TypeError: const = 2
IndexError: const = 3
KeyError: const = 4
NotImplementedError: const = 5

# Compile-time only -- intercepted by IRGenerator, never emits RaiseExn.
# raise CompileError("msg") aborts compilation with a diagnostic.
# Cannot be caught by try/except in user code.
class CompileError(Exception):
    pass
