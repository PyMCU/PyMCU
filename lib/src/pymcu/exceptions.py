# PyMCU compile-time exception.
#
# The runtime exception types (ValueError, TypeError, IndexError, KeyError,
# NotImplementedError, ZeroDivisionError) are real Python builtins — they are
# recognised by the compiler directly (see IRGenerator) and need no import,
# exactly as in CPython. They are deliberately NOT redeclared here; doing so
# would shadow the builtins and make the dialect look less like Python.
#
# CompileError is the one PyMCU-specific exception: `raise CompileError("msg")`
# aborts compilation with a diagnostic and can never be caught at runtime. It is
# the bare-metal way for a HAL to say "this feature is not available on this chip".
class CompileError(Exception):
    pass
