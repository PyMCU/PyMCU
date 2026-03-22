package dev.begeistert.whipsnake.stdlib

import com.intellij.openapi.diagnostic.Logger
import java.nio.file.Path

/**
 * Generates PEP 561 `.pyi` stub files by parsing Whipsnake stdlib `.py` source files.
 *
 * Handles the patterns found in pymcu_circuitpython / pymcu_micropython:
 *  - Leading `#` comment block → module-level docstring
 *  - `@inline` decorator → stripped (not part of the public API)
 *  - `@property` / `@name.setter` → preserved
 *  - `class Name:` → emitted; single-line `"""docstring"""` on the next line extracted
 *  - `def name(params) -> RetType:` → signature emitted with `...` body; docstring extracted
 *  - `NAME = value` (ALL_CAPS) inside a class → `NAME: int` annotation
 *  - `name = Type()` at module level (singleton) → `name: Type` annotation
 *  - `pass` → `...`
 *  - Whipsnake-specific types (`uint8`, `uint16`, `const[str]`, …) → standard Python types
 *  - All import lines and implementation bodies are skipped
 */
object PyStubGenerator {

    private val log = Logger.getInstance(PyStubGenerator::class.java)

    // Order matters: match const[uint32] before uint32, uint before int
    private val TYPE_REPLACEMENTS = listOf(
        Regex("""\bconst\[str\]""")     to "str",
        Regex("""\bconst\[uint\d+\]""") to "int",
        Regex("""\bconst\[int\d+\]""")  to "int",
        Regex("""\bconst\[int\]""")     to "int",
        Regex("""\buint\d+\b""")        to "int",
        Regex("""\bint\d+\b""")         to "int",
    )

    /**
     * Generate a `.pyi` stub string from the given stdlib source file.
     * Returns `null` if the file cannot be read.
     */
    fun generateFrom(sourceFile: Path): String? = try {
        generate(sourceFile.toFile().readLines(), sourceFile.fileName.toString())
    } catch (e: Exception) {
        log.warn("Whipsnake stubs: failed to generate stub from $sourceFile", e)
        null
    }

    // ── Core parser ──────────────────────────────────────────────────────────

    private fun generate(lines: List<String>, fileName: String): String {
        val sb = StringBuilder()
        sb.append("# Whipsnake — generated stub for $fileName (do not edit manually)\n")
        var i = 0
        val n = lines.size

        // Phase 1: collect leading # comment block → module docstring
        val headerLines = mutableListOf<String>()
        while (i < n) {
            val raw = lines[i]
            when {
                raw.startsWith("#") -> { headerLines += raw.removePrefix("#").trim(); i++ }
                raw.isBlank()       -> { i++; if (headerLines.isNotEmpty()) break }
                else                -> break
            }
        }
        val headerContent = headerLines.filter { it.isNotEmpty() }
        if (headerContent.isNotEmpty()) {
            sb.append("\"\"\"\n")
            headerContent.forEach { sb.append("$it\n") }
            sb.append("\"\"\"\n\n")
        }

        // Phase 2: state-machine parse
        val pendingDecorators = mutableListOf<String>()

        while (i < n) {
            val raw     = lines[i]
            val trimmed = raw.trimStart()
            val indent  = raw.length - trimmed.length

            when {
                trimmed.isEmpty() -> { sb.append("\n"); i++ }

                // Skip all pymcu-internal imports
                trimmed.startsWith("from pymcu") ||
                trimmed.startsWith("import pymcu") -> i++

                // Skip @inline — hidden implementation detail
                trimmed == "@inline" -> i++

                // Collect @property, @name.setter, @name.deleter, etc.
                trimmed.startsWith("@") -> { pendingDecorators += raw; i++ }

                // class definition
                trimmed.startsWith("class ") && ":" in trimmed -> {
                    flushDecorators(sb, pendingDecorators)
                    sb.append("$raw\n")
                    i++
                    // Extract single-line class docstring (no ... emitted for class level)
                    i = tryEmitDocstring(lines, i, n, indent + 4, sb)
                }

                // def — function or method
                trimmed.startsWith("def ") -> {
                    flushDecorators(sb, pendingDecorators)
                    sb.append(buildStubSignature(raw) + "\n")
                    i++
                    // Extract single-line method docstring then emit body (...)
                    i = tryEmitDocstring(lines, i, n, indent + 4, sb)
                    sb.append("${" ".repeat(indent + 4)}...\n")
                    // Skip the rest of the implementation body
                    while (i < n) {
                        val bodyLine    = lines[i]
                        val bodyTrimmed = bodyLine.trimStart()
                        val bodyIndent  = bodyLine.length - bodyTrimmed.length
                        if (bodyTrimmed.isEmpty() || bodyIndent > indent) i++ else break
                    }
                }

                // Class-level constant: ALL_CAPS = <value>
                Regex("""^[A-Z][A-Z0-9_]*\s*=""").containsMatchIn(trimmed) -> {
                    val name = trimmed.substringBefore("=").trim()
                    sb.append("${" ".repeat(indent)}$name: int\n")
                    i++
                }

                // Module-level singleton: name = Type()
                Regex("""^[a-z_]\w*\s*=\s*\w+\(\)\s*$""").containsMatchIn(trimmed) -> {
                    val name     = trimmed.substringBefore("=").trim()
                    val typeName = Regex("""=\s*(\w+)\(\)""").find(trimmed)?.groupValues?.get(1)
                    if (typeName != null) sb.append("${" ".repeat(indent)}$name: $typeName\n")
                    i++
                }

                // pass → stub ellipsis
                trimmed == "pass" -> { sb.append("${" ".repeat(indent)}...\n"); i++ }

                // Section comments — keep for readability in the generated stub
                trimmed.startsWith("#") -> { sb.append("$raw\n"); i++ }

                // Everything else (implementation, private assignments) — skip
                else -> i++
            }
        }

        return sb.toString()
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /**
     * If the next non-blank line at [expectedIndent] is a single-line `"""docstring"""`,
     * emits it and returns the index after it. Otherwise returns [startIdx] unchanged.
     */
    private fun tryEmitDocstring(
        lines: List<String>,
        startIdx: Int,
        n: Int,
        expectedIndent: Int,
        sb: StringBuilder
    ): Int {
        var i = startIdx
        while (i < n && lines[i].isBlank()) i++
        if (i >= n) return i
        val raw     = lines[i]
        val trimmed = raw.trimStart()
        val actual  = raw.length - trimmed.length
        val dquote3 = "\"\"\""
        if (actual == expectedIndent &&
            trimmed.startsWith(dquote3) && trimmed.endsWith(dquote3) && trimmed.length > 6
        ) {
            sb.append("$raw\n")
            return i + 1
        }
        return i
    }

    /**
     * Rewrites a `def` line as a stub signature:
     *  - Strips the trailing `:` body marker
     *  - Maps Whipsnake types to standard Python types
     *  - Appends `-> None` when no return annotation is present
     *  - Appends `:` (the body is emitted separately)
     */
    private fun buildStubSignature(defLine: String): String {
        var sig = defLine.trimEnd().removeSuffix(":").trimEnd()
        sig = remapTypes(sig)
        if ("->" !in sig) sig = "$sig -> None"
        return "$sig:"
    }

    private fun remapTypes(s: String): String {
        var result = s
        for ((pattern, replacement) in TYPE_REPLACEMENTS) {
            result = pattern.replace(result, replacement)
        }
        return result
    }

    private fun flushDecorators(sb: StringBuilder, decorators: MutableList<String>) {
        decorators.forEach { sb.append("$it\n") }
        decorators.clear()
    }
}
