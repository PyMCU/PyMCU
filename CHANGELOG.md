# Changelog — pymcu-compiler / pymcu-stdlib

## Unreleased

### Zero cost
- A call argument that HOLDS a compile-time constant binds the parameter as that constant, not
  as a variable containing it. The difference used to be only whether the argument mentioned a
  local: `f(int(s * 1000))` bound a constant and `x = int(s * 1000); f(x)` bound a variable,
  so every callee that dispatches on the value -- the calibrated delays,
  `pwm_prescaler_for_freq`, `claim()`, any `match` on a `const` parameter -- lost its constant
  path, and a `const` parameter refused the call outright. A local computed from another local
  counts, and so does a narrowing cast of one while the value fits. Measured on a millisecond
  delay written with locals: 681 bytes and the generic counted subroutine, against 484 and the
  calibrated loop (#327).
- A `range()` bound written as an EXPRESSION decides the loop. Only a literal or a name folded
  before, so `range(total_us // 60000000)` was a run-time counter loop over a 32-bit bound;
  1814 bytes on a 380-byte program. A folded count of at most eight unrolls, and an empty
  range emits nothing (#326).

### Silent wrong code
- An unhandled `raise` written in the entry function halts with its name instead of returning
  from a function that has no caller. It lowered to `SET; RET`, so the RET popped a return
  address that was never pushed and the chip ran off into whatever the top of SRAM held; the
  documented `E:<TypeName>` and halt was reached only by an exception returning into main from
  a callee (#339).
- A keyword argument clears the None an earlier expansion of the same `@inline` function left
  on that parameter. Any call that let `parity` default to None made the NEXT call's
  `parity=Parity.EVEN` read as None, so the `match` inside took the `case None` arm and a
  UART asked for 7E2 was programmed 7N2 (#324). A `match` subject that folded to a constant
  no longer matches `case None` at all: the arm was lowered as a comparison against a value
  with no representation, and which arm ran was decided by whatever register it read.
- An unannotated field is as wide as the widest value the constructor assigns. It was laid
  out as a byte whatever was stored in it, so `self._period = uint16(1000000 // uint32(hz))`
  read back as `20000 & 0xFF`; `adafruit_motor.servo` put 689 us on the pin where 1000 was
  asked for (#322).
- An import alias belongs to the module that wrote it. One flat table was shared by every
  module, so two files aliasing different things to the same name got whichever was
  registered first, and a wrapper class ended up constructing itself (#320).
- Registering one routine at two interrupt vectors is refused where it is written. The
  second registration overwrote the first, leaving that vector on the bad-interrupt handler
  with its enable bit set: a quadrature encoder on INT0 and INT1 was deaf to one of its two
  pins (#325).
- A global an ISR writes stays in SRAM. The AVR backend homed it in R2-R15, which every ISR
  prologue saves and every epilogue restores, so the handler's write was undone on RETI and
  an encoder counted every edge and reported 0 for ever (#328, fixed in pymcu-avr).

### Language surface
- `super().__init__(a)` applies the base constructor's defaults. The binding loop stopped at
  the end of the argument list it was given and left the rest unbound, so the base body read
  its own defaulted parameter and was told the name "is read here but never assigned,
  imported, or received as a parameter" -- about a parameter, one line under its declaration.
  A required parameter no argument reaches is now named instead (#350).
- A keyword argument binds to a base-class method: `super().__init__(pwm, min_pulse=500)` is
  line 110 of `adafruit_motor/servo.py` and the normal way a driver subclass forwards. It used
  to reach the value path and print "Unknown Expression type: KeywordArgExpr", the name of a
  class in the compiler about a word the program does not contain. Any callee that still
  cannot bind one now names the argument instead (#349).
- `except (A, B):` catches either, which is what the refusal used to tell the reader to write
  by hand. The alternatives are compared against the error code in turn and all reach the one
  handler body; a single type still emits the one comparison and one skip it always did, so
  every existing firmware is byte-identical. It is the optional-import fallback that opens
  `adafruit_dht`, `adafruit_hcsr04` and `adafruit_motor` (#346).
- An annotation with a dotted name, a nested subscript or an empty `[]` inside its brackets is
  read whole, so the sentence that names the construct is reached instead of `Expected ']'` at
  a column inside the annotation. `Union[int, List[int]]` and `Optional[digitalio.DigitalInOut]`
  now get the union refusal, and `tuple[X, ...]` is told that `...` is not a type annotation, on
  both front ends at the same line and column. An unclosed bracket points at the bracket rather
  than at the end of the file (#345).
- `self.column, self.row = 0, 0` unpacks into attributes, as it already did into names. The
  right-hand side is snapshotted before any store, so an attribute swap is still a swap, and
  each target is then written through the assignment it would have been on its own line. It
  used to die two tokens past the comma as "Expected newline or end of block" (#344).
- A base class spelled `module.Class` is read as the class it names. The C# parser used to
  stop at the dot and ask for a closing bracket, so `class NeoPixel(adafruit_pixelbuf.PixelBuf)`
  was refused where the Python front end built the firmware; when the module really is absent
  the reader is now told which one (#343).
- An annotation spelled `module.Class` is read as the class it names, so `p: busio.I2C` says
  what `p: I2C` already said. The parser used to stop at the dot and ask for the closing
  bracket of the parameter list, which is about a bracket in a program whose brackets are
  balanced; a dotted name that ends in a typo is still reported, by the sentence about type
  names (#342).
- A list literal accepts a trailing comma, as a call, a parameter list, a dict and a set
  already did. It was the one bracketed construct without the guard, so `[1, 2,]` was
  reported as a missing expression pointing at the closing bracket -- and every formatter in
  the CircuitPython ecosystem writes that comma on a collection split over several lines
  (#341).
- `from <package> import <submodule>` works even when the package's `__init__` mentions the
  submodule's name in a comment, which is what refused `from adafruit_motor import servo`
  (#323).
- A constant inside a nested class is readable: `Outer.Inner.A`, and `busio.UART.Parity.ODD`
  through the declaring module. The scan never registered a nested class body's attributes
  and the read resolved one hop at a time (#319).
- A `for` over a constant list of STRINGS unrolls, so
  `for pin in (board.D2, board.D3, board.D4)` -- the CircuitPython idiom for a row of pins --
  binds each pin as a compile-time constant instead of being refused as a non-integer (#308).
- A HAL module can put its own interrupt on a pin: a handler named as a value resolves in the
  module that defines it, and `compile_isr()` accepts a vector composed from an `@inline`
  table instead of only a literal (#321).
- `Pin.mode()` has the reading half its signature advertises, on AVR and on PIC; every
  `match __CHIP__.name:` in the PIC14 GPIO layer refuses an unsupported part instead of
  falling off the end (#312).

### Diagnostics
- A refusal about a parameter annotation, a return annotation or an undefined base class
  inside an imported module names THAT module's file. Both checks are deferred until every
  module has been scanned, and both ran with no module in scope, so the line came from the
  definition and the file from the entry program: a union inside `adafruit_motor/servo.py`
  line 52 was printed as `main.py:52`, in a `main.py` fifteen lines long (#347).
- A refused comprehension says which thing is unsupported. A comprehension of class instances
  was told it has a filter, and sent the reader looking for an `if` that is not there (#307).
- A diagnostic about a module-level line above `def main():` is reported at the line it is on.
  The driver mapped the injected preamble by one offset for two insertion points and picked
  the larger, so the number came out one early while the snippet text was right; the
  debugger's line map was off by one over the same region (#311).

### Peripherals (measured on an Arduino Uno with a scope)
- `PWM.stop()` takes the channel off the pin and drives it low instead of stopping the
  timer, which froze the sibling channel and the time base and left the pin at whatever
  level the compare latch had (5 V half the time after `pwmio.deinit()`); `PWM.deinit()`
  is new and returns the pin to an input, which `pwmio.PWMOut.deinit()` now calls (#296).
- A PWM on PD5/PD6 at a frequency the Timer0 time base cannot share is refused at
  compile time, naming D3/D11 and D9/D10; `millis_init()` no longer clears TCCR0A. The
  driver passes `--timebase` and the compiler binds `__TIMEBASE__` next to `__FREQ__`
  (#295).
- Every PWM HAL takes a 16-bit duty (`PWM(pin, duty_u16=...)`, `set_duty_u16()`), the entry
  the CircuitPython and MicroPython layers now use exclusively; on the AVR 8-bit channels
  it lands exactly (32768 is 50.0 %, it read 50.4 % on the Uno; pymcu-circuitpython#30).
- `Pin.mode(Pin.IN)` gives an input with the pull the pin asked for, not the level it
  was driving (a pin made an input after `high()` stayed at 5 V on the Uno);
  `mode(IN_PULLUP)` sets the pull-up instead of writing 2 into the direction bit, and
  `mode(OPEN_DRAIN)` is refused (#309). `digitalio.deinit()` releases without pull.
- The two channels of one timer share its prescaler: the second `PWM()` (or a
  `set_freq()` next to a running sibling) asking for another bucket is refused at compile
  time, where it is written, through the new `claim()` intrinsic in `pymcu.types` (#300).

### Classes and variables
- A driver takes a list and keeps it. A list of instances (`Bar([Pin("PD5", Pin.OUT),
  Pin("PD6", Pin.OUT)])`) or of numbers, given to a constructor or to a method and stored
  in a `self` field, is now a compile-time sequence the field is another name for:
  `self._pins[0]`, `for p in self._pins`, `len(self._pins)` and a run-time
  `self._pins[i].method()` all answer. Before, the field became a scalar and every read
  was zero, or the spelling was refused outright (#313, #314).
- A list argument is built once. It used to stay raw AST bound to the parameter, so every
  subscript re-evaluated it and `ps[0]` ran the constructor again. A method call through
  the rebuilt instance still reached the right pin, because the duplicate folds to the
  same port and bit; a property setter did not, because it resolves its receiver to a
  name and an anonymous re-construction is not one, so the write was dropped with no
  diagnostic (#313).
- A bytearray handed to a driver and stored in a field keeps its storage, so
  `self._data[i] = v` writes the caller's buffer instead of being refused as a bit index
  into a scalar (#315).
- Assigning to a name the class defines as a METHOD is refused where it is written,
  instead of writing a phantom field that shadows the method: `p.value = 1` on a HAL Pin,
  the CircuitPython spelling, built clean and emitted no write to the port at all. The
  refusal names `p.value(1)` and `p.value()` (#316).
- A MicroPython driver library compiles unmodified. Measured on
  `github.com/kritishmohapatra/micropython-sevenseg`, which needed eight edits and now needs
  none: a comprehension of instances over a list of pin numbers (#332), a constant subscript
  of that list (#333), an optional peripheral guarded by a field set to None (#334), a dict
  literal in a field (#335) whose values are lists (#336), and `zip` over the field of pins
  against a row of that dict (#337).
- A glyph table keyed by CHARACTERS works: a one-character string literal folds to its
  character code, so any set of distinct constant integer keys is the same rectangle, with
  the lookup mapping the key to its row index -- a subtraction when the keys are
  contiguous, a search over a key row in flash otherwise, so a 96-glyph font costs the same
  code as a 7-glyph one. A run-time key against one-character keys is allowed, because
  those are codes; a multi-character key is an interned id and stays constant-only, which
  the refusal now says. A key that matches nothing raises KeyError, where the contiguous
  case used to read past the table (#338).
- A dict of constant rows -- the shape of every digit, font and gamma table -- is a
  rectangle in flash, laid out only when a run-time key needs it. A constant key folds to
  the row and emits nothing (#336).
- `zip` walks whatever a `for` loop walks: a fixed array by name, a compile-time sequence of
  instances held in a field, a list of constants, or a dict row (#337).
- A branch that cannot be taken is not lowered. `if self.dp:` on a field holding None, and
  the same as a ternary, used to be lowered on the dead side and fail inside it (#334).
- A method that reads a compile-time table from a field is inlined rather than compiled as
  a shared subroutine, where the table is unreachable and the reader was told the method
  could not be dispatched.
- A lookup table written as a plain list reads at run time. `DIGITS = [0x3F, 0x06, ...]`
  then `DIGITS[digit]` is how every 7-segment table, font and gamma curve is written, and
  it was refused: the list lives as separate variables, which have nothing to index. The
  values are constants and nothing writes them, so the table is materialised in flash at
  the first run-time subscript that needs it, and a table only ever indexed with a
  constant still emits nothing. The same answer through a parameter and through a `self`
  field. A table the program stores into keeps its refusal, because flash cannot be
  written (#317).

### Correctness (silent-miscompile class)
- The counter of `for i in range(...)` is sized from its bounds instead of being an
  unconditional uint8: `range(300)` ran 44 times, `range(0, 256)` never ran,
  `range(200, -1, -1)` never ran, a `uint16` stop variable and a `uint16` annotation on
  the loop variable were ignored, and `range(0, 250, 30)` wrapped its counter (#284).
- The range loop variable holds the last value visited after the loop, as in Python:
  it read 0 after an unrolled constant range and `stop` after a runtime one (#285).
- A signed runtime step (`range(10, 0, step)` with `step: int8 = -2`) counts down instead
  of exiting before the first iteration (#286).
- List comprehensions over `range(start, stop, step)` honour the step (#287).
- One width per variable name, program-wide: a name typed two bytes wide at one site and
  one byte at another reached a register allocator that sizes a name once (#291).
- A module-level unannotated accumulator is typed like a local, with promotion: `n = 0`
  then `n = n + 1` over `range(300)` counted to 44 at module level and to 300 in a def.
  A written annotation keeps its width (#289).
- A comparison is decided by the values, not by the left operand's width: `count >= 404`
  with `count: uint8` tested against 148, `count < 300` against 44, and `x < n` with
  `n: uint16` read n's low byte. Sides that cannot overlap fold to Python's answer (#290).
- A single-field instance mutated inside a method's loop keeps its value and the method
  returns it: an unannotated method's `return self.value` arrived as None, and the field
  folded to the constructor's value after the loop (#292).

### Language
- `x in range(a, b[, s])`, `reversed(range(...))` and `enumerate(range(...))` over runtime
  bounds are supported; `range()` as a value is refused with a message that names the
  spellings that work (#288).
- A module-level `main()` says where the entry point's body runs: the statements written
  after it run after that body, as they do in CPython. The call used to be dropped, so
  `main(); print("END")` printed END first and main's output last, with nothing reported.
  A second `main()` and a `main()` that returns early with module-level code after the
  call are refused where the call is written (#301).
- A method called on the name bound by `with ... as` reaches the manager's class. The name is
  a pure alias of the manager and only the field path followed it, so
  `with digitalio.DigitalInOut(board.D6) as pin:` then `pin.switch_to_output(True)` was
  refused as a call to an undefined `pin_switch_to_output` (#305).
- `None` passed as an argument, or assigned through a property setter, binds the parameter as
  `None` instead of leaving it unbound, and a `match` whose subject is `None` is decided at
  compile time: it matches `case None` and the wildcard, and only that arm is lowered.
  `pin.pull = None`, CircuitPython's spelling for "no pull", was refused with "Pull-down
  resistor not supported on AVR" from the arm the program never selected (#306).

### Guardrails (was silent, now a located error)
- A call whose callee reaches the end of its body without returning is refused where the
  result is read, naming the callee. There is no `None` here, so the caller was reading the
  result temporary the expansion never wrote: an AVR PWM HAL whose prescaler selector had
  lost its `return` compiled to `MOV R4, R16` -- the low byte of RAMEND, left by the reset
  prologue -- and programmed TCCR0B = 0x3F instead of 3, which clocks Timer0 from the T0 pin
  so the output never toggles. The firmware built clean. A call written as a statement reads
  nothing and is untouched (#302).

### Diagnostics
- A line a message quotes for an EARLIER site is a line of the file the reader wrote. The
  citation now names its file (`already 3 for PD6 at main.py:38`) and `pymcu build` maps it
  back from the synthetic entry it compiles, like the header. A program that calls
  `print()` is four lines longer in `dist/_generated`, so the quoted line pointed past the
  end of the source: measured at "at line 11" for an eight-line program (#303).

## 0.1.0a10 — 2026-08-18

The hardware-validation release. Everything below came out of a sustained
bug-hunting campaign on a real Arduino Uno with a logic analyzer, plus a
sweep of the official MicroPython quickref and CircuitPython Essentials
examples (63 projects; 53 compile, the rest fail on purpose with a clear
diagnostic). Suites at release: 517 unit, 508 driver, 1549 AVR integration.

### Correctness (silent-miscompile class)
- Copy propagation no longer forwards through same-width float<->int casts:
  `uint32(float_var)` produced raw float bits (16464 for 3.25 on a real Uno).
- An unannotated module global widens to its call-result type instead of
  wrapping at a uint8 store (`f0 = pwm.freq()` printed 232 for 1000).
- A user global no longer shadows a same-named function parameter -- neither
  of an `@inline` (`data = 5` broke `uart.write('hello')`) nor of a plain def
  (`start_low_ms = 250` silently drove the DHT driver's start pulse for
  250 ms instead of 18).
- `raise CompileError` inside an `@inline` body aborts compilation even when
  the call site sits under runtime control flow (a swallowed raise had let
  `readline()` compile to an unbound temp written to UDR0).
- `print(bytearray)` streamed the array variable as a scalar and printed
  garbage; it now prints the CPython `bytearray(b'...')` repr.
- `millis()`/`ticks_ms`/`monotonic` count real milliseconds (Arduino-style
  fractional correction; a 1 s blink measured 1024 ms before), and
  `micros()` no longer jumps backward across a Timer0 overflow.
- The second PWM channel of a timer no longer disconnects the first
  (shared TCCRxA COM bits are OR-ed in).
- `uart_write_float` prints two rounded decimals with the trailing zero
  trimmed (3.25 printed 3.2 before; 0.05 printed 0.0).

### Language surface
- Slice assignment dispatches through `__setitem__` with a bytes/list
  literal source: `microcontroller.nvm[0:4] = b'\xcc\x10\xca\xfe'`.
- `for b in buf[0:n]` accepts runtime bounds (rewritten to a range loop).
- `str.join` in assignment: compile-time strings fold to a constant;
  `''.join([chr(b) for b in buf])` builds a runtime string.
- `const` parameters accept compile-time float constants (`Timer(freq=2.5)`).
- Exception catch-all forms, user exception classes, bare `except`;
  `__bool__`/`__len__` truthiness; `__call__`; n-ary `min`/`max`;
  `dict.get` on literal dicts.
- A nested constructor argument types as its class in overload resolution
  (`ADC(Pin(14))` picks the Pin overload); overload resolution matches
  parameter types, not declaration order.

### Guardrails (was silent, now a located error)
- A `const[...]` parameter rejects runtime-varying arguments (a loop
  variable passed to `Pin()` silently drove a fixed pin before).
- An image larger than the chip's flash, and static SRAM beyond the chip's
  RAM, are build errors with the part's real numbers.
- Runtime tuples, filtered comprehensions, list parameters, instance
  interpolation and iterator-protocol loops all get specific diagnostics
  instead of misbehaving quietly.

### Requires
- pymcu-avr >= 0.1.0a9 (paired codegen fixes: float conversions and
  comparisons, wide constants, linker MEMORY regions).
