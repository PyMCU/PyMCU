# Graph Report - src  (2026-05-20)

## Corpus Check
- 103 files · ~350,879 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 846 nodes · 1838 edges · 75 communities (38 shown, 37 thin omitted)
- Extraction: 95% EXTRACTED · 5% INFERRED · 0% AMBIGUOUS · INFERRED: 99 edges (avg confidence: 0.81)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- [[_COMMUNITY_IRGenerator Core|IRGenerator Core]]
- [[_COMMUNITY_AST Node Hierarchy|AST Node Hierarchy]]
- [[_COMMUNITY_Parser Engine|Parser Engine]]
- [[_COMMUNITY_AVR Code Generation|AVR Code Generation]]
- [[_COMMUNITY_PIC18 Backend|PIC18 Backend]]
- [[_COMMUNITY_PIC12 Backend|PIC12 Backend]]
- [[_COMMUNITY_PIC14 Peephole & Strategy|PIC14 Peephole & Strategy]]
- [[_COMMUNITY_Hardware Programmers|Hardware Programmers]]
- [[_COMMUNITY_RISC-V Backend|RISC-V Backend]]
- [[_COMMUNITY_IR Types & Build Metadata|IR Types & Build Metadata]]
- [[_COMMUNITY_Compiler Pipeline Phases|Compiler Pipeline Phases]]
- [[_COMMUNITY_PIC14 Code Generation|PIC14 Code Generation]]
- [[_COMMUNITY_IR Optimizer|IR Optimizer]]
- [[_COMMUNITY_PIO Backend|PIO Backend]]
- [[_COMMUNITY_Toolchain Management CLI|Toolchain Management CLI]]
- [[_COMMUNITY_Lexer|Lexer]]
- [[_COMMUNITY_Stack & Function Layout|Stack & Function Layout]]
- [[_COMMUNITY_Build Logging|Build Logging]]
- [[_COMMUNITY_AVR Assembly Emission|AVR Assembly Emission]]
- [[_COMMUNITY_BuildClean CLI Commands|Build/Clean CLI Commands]]
- [[_COMMUNITY_Backend Plugin Discovery|Backend Plugin Discovery]]
- [[_COMMUNITY_Build Command Driver|Build Command Driver]]
- [[_COMMUNITY_Python Compiler Driver|Python Compiler Driver]]
- [[_COMMUNITY_AST Processors|AST Processors]]
- [[_COMMUNITY_PIC18 Peephole|PIC18 Peephole]]
- [[_COMMUNITY_PIO Peephole|PIO Peephole]]
- [[_COMMUNITY_PIC12 Peephole|PIC12 Peephole]]
- [[_COMMUNITY_RISC-V Peephole|RISC-V Peephole]]
- [[_COMMUNITY_Conditional Compilation|Conditional Compilation]]
- [[_COMMUNITY_Chip Discovery|Chip Discovery]]
- [[_COMMUNITY_Compiler State & Config|Compiler State & Config]]
- [[_COMMUNITY_Flash Command|Flash Command]]
- [[_COMMUNITY_Backend Plugin Manager|Backend Plugin Manager]]
- [[_COMMUNITY_Code Generator Factory|Code Generator Factory]]
- [[_COMMUNITY_Compiler Phase Framework|Compiler Phase Framework]]
- [[_COMMUNITY_Compile-Time Evaluator|Compile-Time Evaluator]]
- [[_COMMUNITY_Type System|Type System]]
- [[_COMMUNITY_Interrupt Context Helpers|Interrupt Context Helpers]]
- [[_COMMUNITY_AVR Linear Scan Allocator|AVR Linear Scan Allocator]]
- [[_COMMUNITY_Compiler Error Types|Compiler Error Types]]
- [[_COMMUNITY_Diagnostic Reporter|Diagnostic Reporter]]
- [[_COMMUNITY_Target Chip Loader|Target Chip Loader]]
- [[_COMMUNITY_Pre-Scan AST Visitor|Pre-Scan AST Visitor]]
- [[_COMMUNITY_IR Scope Contexts|IR Scope Contexts]]
- [[_COMMUNITY_Module Loader (FS)|Module Loader (FS)]]
- [[_COMMUNITY_Compiler Entry Point|Compiler Entry Point]]
- [[_COMMUNITY_AVR Greedy Register Alloc|AVR Greedy Register Alloc]]
- [[_COMMUNITY_Architecture Family Resolver|Architecture Family Resolver]]
- [[_COMMUNITY_Module Loader Interface|Module Loader Interface]]
- [[_COMMUNITY_Compiler Phase Interface|Compiler Phase Interface]]
- [[_COMMUNITY_Tacky IR Representation|Tacky IR Representation]]
- [[_COMMUNITY_Dynamic Stack Allocator|Dynamic Stack Allocator]]
- [[_COMMUNITY_Source Utilities|Source Utilities]]
- [[_COMMUNITY_Dependency Graph Interface|Dependency Graph Interface]]
- [[_COMMUNITY_AST Processor Interface|AST Processor Interface]]
- [[_COMMUNITY_CLI Builder|CLI Builder]]
- [[_COMMUNITY_Basic Block (CFG)|Basic Block (CFG)]]
- [[_COMMUNITY_Control Flow Graph|Control Flow Graph]]
- [[_COMMUNITY_Compilation Context|Compilation Context]]
- [[_COMMUNITY_Device Configuration|Device Configuration]]
- [[_COMMUNITY_avrdude Discovery|avrdude Discovery]]
- [[_COMMUNITY_Serial Port Discovery|Serial Port Discovery]]

## God Nodes (most connected - your core abstractions)
1. `AvrCodeGen` - 51 edges
2. `Parser` - 49 edges
3. `PIC18CodeGen` - 44 edges
4. `PIC12CodeGen` - 39 edges
5. `PIC14CodeGen` - 29 edges
6. `RiscvCodeGen` - 28 edges
7. `Statement` - 25 edges
8. `PIOCodeGen` - 25 edges
9. `Expression` - 22 edges
10. `IRGenerator` - 21 edges

## Surprising Connections (you probably didn't know these)
- `backend_list()` --calls--> `discover_backends()`  [INFERRED]
  driver/commands/backend.py → driver/backends/__init__.py
- `backend_check()` --calls--> `discover_backends()`  [INFERRED]
  driver/commands/backend.py → driver/backends/__init__.py
- `build()` --calls--> `get_backend_for_chip()`  [INFERRED]
  driver/commands/build.py → driver/backends/__init__.py
- `build()` --calls--> `run_backend()`  [INFERRED]
  driver/commands/build.py → driver/backends/__init__.py
- `flash()` --calls--> `get_programmer()`  [INFERRED]
  driver/commands/flash.py → driver/programmers/__init__.py

## Hyperedges (group relationships)
- **PyMCU IR Instruction Types (Tacky IR)** — ir_constant, ir_floatconstant, ir_variable, ir_temporary, ir_memoryaddress, ir_noneval, ir_return, ir_unary, ir_binary, ir_copy, ir_loadindirect, ir_storeindirect, ir_jump, ir_jumpifzero, ir_jumpifnotzero [EXTRACTED 1.00]
- **PyMCU Compiler Build Outputs (Release osx-arm64)** — pymcuc_dll_release, pymcu_backend_sdk_dll, spectre_console_dll, system_commandline_dll, pymcuc_binary_release [EXTRACTED 1.00]

## Communities (75 total, 37 thin omitted)

### Community 0 - "IRGenerator Core"
Cohesion: 0.06
Nodes (8): IRGenerator, IRGenerator, IRGenerator, IRGenerator, IRGenerator, IRGenerator, IRGenerator, IRGenerator

### Community 1 - "AST Node Hierarchy"
Cohesion: 0.08
Nodes (50): AnnAssign, AssertStmt, AssignStmt, ASTNode, AugAssignStmt, BinaryExpr, Block, BooleanLiteral (+42 more)

### Community 6 - "PIC14 Peephole & Strategy"
Cohesion: 0.08
Nodes (6): ArchStrategy, PIC14AsmLine, PIC14EStrategy, PIC14Peephole, PIC14Strategy, PIC14CodeGen

### Community 7 - "Hardware Programmers"
Cohesion: 0.13
Nodes (12): HardwareProgrammer, auto_detect_port(), AvrdudeProgrammer, find_system_avrdude(), Search for the avrdude binary within the tool directory (handles nested archive, Search for avrdude.conf within the tool directory., Return avrdude binary path: system PATH preferred, cached binary fallback., Concrete implementation for AVRDUDE (AVR Downloader/UploaDEr).      Binary resol (+4 more)

### Community 8 - "RISC-V Backend"
Cohesion: 0.19
Nodes (3): Dictionary, DependencyGraph, RiscvCodeGen

### Community 9 - "IR Types & Build Metadata"
Cohesion: 0.12
Nodes (26): AOT Native Binary Strategy (osx-arm64 self-contained), DataTypeExtensions (PyMCU.IR), DeviceConfig Model (PyMCU.Common.Models), IR Binary (PyMCU.IR), IR Constant (PyMCU.IR), IR Copy (PyMCU.IR), IR FloatConstant (PyMCU.IR), IR Jump (PyMCU.IR) (+18 more)

### Community 10 - "Compiler Pipeline Phases"
Cohesion: 0.08
Nodes (8): CompilerPhaseBase, BackendPhase, BootstrapPhase, FrontendResolutionPhase, InitializationPhase, IrGenerationPhase, IrSerializerPhase, ParsingPhase

### Community 11 - "PIC14 Code Generation"
Cohesion: 0.21
Nodes (3): ArchStrategy, CodeGen, PIC14CodeGen

### Community 14 - "Toolchain Management CLI"
Cohesion: 0.19
Nodes (13): Re-download and reinstall a toolchain to pick up a newer version.      Examples, List all installed toolchain plugins and their installation status., Install a toolchain into the local cache (~/.pymcu/tools/).      Examples     --, toolchain_install(), toolchain_list(), toolchain_update(), discover_plugins(), get_ffi_toolchain_for_chip() (+5 more)

### Community 15 - "Lexer"
Cohesion: 0.43
Nodes (14): Advance(), Error(), HandleIndentation(), Identifier(), IsDigitForBase(), Match(), Number(), Peek() (+6 more)

### Community 16 - "Stack & Function Layout"
Cohesion: 0.17
Nodes (7): FunctionNode, StackAllocator, BuiltinModuleNames, DependencyGraphBuilder, HashSet, IDependencyGraphBuilder, int

### Community 19 - "Build/Clean CLI Commands"
Cohesion: 0.2
Nodes (8): clean(), Removes build artifacts (dist/ directory, including dist/_generated/)., Displays the version information for PyMCU and its components., version(), _ensure_venv(), Automatic Venv Switching (The "Wrapper" Logic)     Checks if we are running glob, run_cli(), version_callback()

### Community 20 - "Backend Plugin Discovery"
Cohesion: 0.23
Nodes (11): discover_backends(), get_backend_binary(), get_backend_for_chip(), _hint_for_chip(), Return the BackendPlugin class for *chip* or raise ValueError with a     helpful, Return the path to the backend binary for *chip*, or None if no backend     is i, Invoke an external backend binary (e.g. pymcuc-avr) to translate a .mir     IR f, Return all registered backend plugins keyed by family name.      Plugins are dis (+3 more)

### Community 21 - "Build Command Driver"
Cohesion: 0.24
Nodes (11): build(), _diag_log(), _load_extension_board_chips(), _make_compiler_output_handler(), _parse_hex_flash_bytes(), Try to import pymcu_<flavor>.board_chips and return its BOARD_CHIPS dict.     Re, Return the chip name for *board*, checking extension-supplied entries first., Parse an Intel HEX file and return the total number of data bytes.     Only coun (+3 more)

### Community 22 - "Python Compiler Driver"
Cohesion: 0.27
Nodes (4): PyMCUCompiler, Wrapper for the core C++ build tool (pymcuc).     Handles path resolution, stdli, Helper to allow easier mocking or inheritance if needed, Resolves the PyMCU Standard Library path.

### Community 23 - "AST Processors"
Cohesion: 0.2
Nodes (4): IAstProcessor, ConditionalCompilationProcessor, DeviceConfigFallbackProcessor, PreScanProcessor

### Community 26 - "PIC12 Peephole"
Cohesion: 0.2
Nodes (3): LineType, PIC12AsmLine, PIC12Peephole

### Community 29 - "Chip Discovery"
Cohesion: 0.36
Nodes (7): _chip_imports(), _discover_stdlib_flavors(), get_available_chips(), new(), Dynamically scans the installed 'pymcu-stdlib' package for chip definitions., Return a list of installed pymcu extension packages (pymcu-<flavor>).     Scans, Generate the import block and a minimal main() body for the given chip     and o

### Community 30 - "Compiler State & Config"
Cohesion: 0.25
Nodes (4): DeviceConfig, IRGenerator, List, CompilerDriver

### Community 31 - "Flash Command"
Cohesion: 0.38
Nodes (5): _default_programmer(), flash(), Flashes the built firmware to the target microcontroller.      Port resolution o, default_programmer(), Return the default programmer name for a given chip identifier.

### Community 32 - "Backend Plugin Manager"
Cohesion: 0.29
Nodes (6): backend_check(), backend_install(), backend_list(), Install a backend plugin package (wraps pip install)., Validate licenses for all installed backend plugins., List all installed backend plugins and their license status.

### Community 33 - "Code Generator Factory"
Cohesion: 0.33
Nodes (3): CodeGenFactory, CompilerInfo, string

### Community 38 - "AVR Linear Scan Allocator"
Cohesion: 0.33
Nodes (4): AvrLinearScan, LiveInterval, bool, DataType

### Community 39 - "Compiler Error Types"
Cohesion: 0.53
Nodes (5): CompilerError, IndentationError, LexicalError, SyntaxError, Exception

### Community 43 - "IR Scope Contexts"
Cohesion: 0.4
Nodes (4): FunctionEntry, InlineContext, LoopLabels, ModuleScope

## Knowledge Gaps
- **61 isolated node(s):** `Automatic Venv Switching (The "Wrapper" Logic)     Checks if we are running glob`, `Wrapper for the core C++ build tool (pymcuc).     Handles path resolution, stdli`, `Helper to allow easier mocking or inheritance if needed`, `Resolves the PyMCU Standard Library path.`, `Return the default programmer name for a given chip identifier.` (+56 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **37 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `int` connect `Stack & Function Layout` to `IRGenerator Core`, `Parser Engine`, `AVR Code Generation`, `PIC18 Backend`, `PIC12 Backend`, `AVR Linear Scan Allocator`, `PIC14 Peephole & Strategy`, `RISC-V Backend`, `PIC14 Code Generation`, `PIO Backend`, `PIO Peephole`, `Compiler State & Config`?**
  _High betweenness centrality (0.147) - this node is a cross-community bridge._
- **Why does `IRGenerator` connect `IRGenerator Core` to `Stack & Function Layout`, `RISC-V Backend`?**
  _High betweenness centrality (0.074) - this node is a cross-community bridge._
- **Why does `string` connect `Code Generator Factory` to `AVR Linear Scan Allocator`, `PIC14 Peephole & Strategy`, `RISC-V Backend`, `PIC14 Code Generation`, `Stack & Function Layout`, `AVR Assembly Emission`, `PIC18 Peephole`, `PIO Peephole`, `PIC12 Peephole`, `RISC-V Peephole`, `Compiler State & Config`?**
  _High betweenness centrality (0.060) - this node is a cross-community bridge._
- **What connects `Automatic Venv Switching (The "Wrapper" Logic)     Checks if we are running glob`, `Wrapper for the core C++ build tool (pymcuc).     Handles path resolution, stdli`, `Helper to allow easier mocking or inheritance if needed` to the rest of the system?**
  _61 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `IRGenerator Core` be split into smaller, more focused modules?**
  _Cohesion score 0.06 - nodes in this community are weakly interconnected._
- **Should `AST Node Hierarchy` be split into smaller, more focused modules?**
  _Cohesion score 0.08 - nodes in this community are weakly interconnected._
- **Should `PIC14 Peephole & Strategy` be split into smaller, more focused modules?**
  _Cohesion score 0.08 - nodes in this community are weakly interconnected._