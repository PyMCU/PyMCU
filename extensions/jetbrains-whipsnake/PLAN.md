# Whipsnake JetBrains Plugin Plan

Plugin for PyCharm / CLion supporting Whipsnake projects.

## Plugin Type
- IntelliJ Platform Plugin (Kotlin/JVM)
- Compatible with: PyCharm, CLion, IntelliJ IDEA (with Python plugin)
- Build system: Gradle with `intellij-platform-gradle-plugin`

## Core Features

### 1. Project Recognition
- Detect `[tool.whip]` section in `pyproject.toml`
- Read `chip`, `frequency`, `sources`, `entry` from config
- Show target chip in status bar widget

### 2. Run Configurations
- **Whipsnake Build** — runs `whip build`, parses compiler output
- **Whipsnake Flash** — runs `pymcu flash`
- **Whipsnake Clean** — runs `pymcu clean`
- Run configuration templates auto-created for pymcu projects

### 3. Error Highlighting (Console Filter)
- Parse compiler diagnostic output: `file:line:col: severity: message`
- Link errors to source files (clickable in Run console)
- Populate Inspections/Problems tool window with compiler errors
- Use `ConsoleFilterProvider` or `Filter` to create hyperlinks

### 4. Compiler Output Format
The `pymcuc` compiler emits GCC-style diagnostics on stderr:
```
src/main.py:16:1: error: CompileError: NotImplementedError: Pull-up not available
    btn = Pin("RB4", Pin.IN, pull=Pin.PULL_UP)
    ^
```
Regex: `^(.+):(\d+):(\d+):\s+(error|warning|info):\s+(.+)$`

### 5. Tool Window
- "Whipsnake" tool window showing:
  - Target chip and frequency
  - Build/Flash/Clean action buttons
  - Last build output summary

### 6. Python Intellisense
- Add pymcu stdlib to Python interpreter paths
- Resolve `from pymcu.chips.<chip> import *` for autocomplete
- Recognize `@inline`, `@interrupt(vector)` decorators

### 7. External Tool Configuration
- Auto-detect `pymcu` CLI in PATH or virtualenv
- Configurable executable path in Settings > Tools > Whipsnake
- Toolchain and programmer status indicators

## Project Structure (Planned)
```
extensions/jetbrains-pymcu/
├── build.gradle.kts
├── settings.gradle.kts
├── gradle.properties
├── src/main/
│   ├── kotlin/dev/begeistert/pymcu/
│   │   ├── WhipsnakeBundle.kt              # Message bundle
│   │   ├── WhipsnakeStartupActivity.kt     # Project open detection
│   │   ├── WhipsnakeConfigReader.kt        # pyproject.toml parser
│   │   ├── WhipsnakeRunConfigType.kt       # Run configuration type
│   │   ├── WhipsnakeRunConfig.kt           # Run configuration
│   │   ├── WhipsnakeConsoleFilter.kt       # Error output parser
│   │   ├── WhipsnakeToolWindow.kt          # Tool window factory
│   │   └── WhipsnakeStatusBarWidget.kt     # Chip status widget
│   └── resources/
│       ├── META-INF/plugin.xml         # Plugin descriptor
│       └── messages/
│           └── WhipsnakeBundle.properties  # i18n strings
└── src/test/
    └── kotlin/dev/begeistert/pymcu/
        └── WhipsnakeConfigReaderTest.kt
```

## Dependencies
- `org.jetbrains.intellij.platform` Gradle plugin
- `com.moandjiezana.toml:toml4j` or `cc.ekblad:4koma` for TOML parsing
- Python plugin API for interpreter path configuration

## Priority
Low — VS Code extension covers the primary use case. Implement after VS Code extension is stable and published.
