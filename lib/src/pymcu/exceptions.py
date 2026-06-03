# Exception type codes -- builtins, no import required.
# These are pre-defined by the compiler identically to True/False/None.
# Kept here only for IDEs and explicit imports from library code.
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
