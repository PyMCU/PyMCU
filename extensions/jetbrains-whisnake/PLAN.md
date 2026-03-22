# Whisnake JetBrains Plugin Plan

Plugin for PyCharm / CLion supporting Whisnake projects.

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
- **Whisnake Build** — runs `whip build`, parses compiler output
- **Whisnake Flash** — runs `pymcu flash`
- **Whisnake Clean** — runs `pymcu clean`
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
- "Whisnake" tool window showing:
  - Target chip and frequency
  - Build/Flash/Clean action buttons
  - Last build output summary

### 6. Python Intellisense
- Add pymcu stdlib to Python interpreter paths
- Resolve `from pymcu.chips.<chip> import *` for autocomplete
- Recognize `@inline`, `@interrupt(vector)` decorators

### 7. External Tool Configuration
- Auto-detect `pymcu` CLI in PATH or virtualenv
- Configurable executable path in Settings > Tools > Whisnake
- Toolchain and programmer status indicators

## Project Structure (Planned)
```
extensions/jetbrains-pymcu/
├── build.gradle.kts
├── settings.gradle.kts
├── gradle.properties
├── src/main/
│   ├── kotlin/dev/begeistert/pymcu/
│   │   ├── WhisnakeBundle.kt              # Message bundle
│   │   ├── WhisnakeStartupActivity.kt     # Project open detection
│   │   ├── WhisnakeConfigReader.kt        # pyproject.toml parser
│   │   ├── WhisnakeRunConfigType.kt       # Run configuration type
│   │   ├── WhisnakeRunConfig.kt           # Run configuration
│   │   ├── WhisnakeConsoleFilter.kt       # Error output parser
│   │   ├── WhisnakeToolWindow.kt          # Tool window factory
│   │   └── WhisnakeStatusBarWidget.kt     # Chip status widget
│   └── resources/
│       ├── META-INF/plugin.xml         # Plugin descriptor
│       └── messages/
│           └── WhisnakeBundle.properties  # i18n strings
└── src/test/
    └── kotlin/dev/begeistert/pymcu/
        └── WhisnakeConfigReaderTest.kt
```

## Dependencies
- `org.jetbrains.intellij.platform` Gradle plugin
- `com.moandjiezana.toml:toml4j` or `cc.ekblad:4koma` for TOML parsing
- Python plugin API for interpreter path configuration

## Priority
Low — VS Code extension covers the primary use case. Implement after VS Code extension is stable and published.
