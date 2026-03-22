package dev.begeistert.whipsnake.config

import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile

/**
 * Holds the parsed contents of the [tool.whip] section of pyproject.toml.
 */
data class WhipsnakeConfig(
    /** Explicit chip name, e.g. "atmega328p". Null when [board] is set instead. */
    val chip: String?,
    /** Board alias, e.g. "arduino_uno". Takes precedence over [chip] for display. */
    val board: String?,
    val frequency: String?,
    val sources: String?,
    val entry: String?,
    /** Compat stdlib flavors declared via  stdlib = ["micropython"]  or  ["circuitpython"]. */
    val stdlib: List<String> = emptyList(),
    /** True when a [tool.pymcu.ffi] section is present in pyproject.toml. */
    val hasFfi: Boolean = false
) {
    /** Human-readable display name: prettified board name, chip name, or "(unknown)". */
    val displayName: String
        get() = when (board) {
            "arduino_uno"   -> "Arduino Uno (atmega328p)"
            "arduino_nano"  -> "Arduino Nano (atmega328p)"
            "arduino_mega"  -> "Arduino Mega (atmega2560)"
            "arduino_micro" -> "Arduino Micro (atmega32u4)"
            else            -> board ?: chip ?: "(unknown)"
        }
}

/**
 * Reads [tool.whip] from pyproject.toml located at the project base directory.
 * Uses simple regex — no external TOML library required.
 */
object WhipsnakeConfigReader {

    private val SECTION_RE      = Regex("""\[tool\.pymcu]""")
    private val FFI_SECTION_RE  = Regex("""\[tool\.pymcu\.ffi]""")

    // Matches key = "value"  or  key = 'value'  or  key = bare_value (not arrays)
    private val KV_RE           = Regex("""^\s*(\w+)\s*=\s*["']?([^"'\[\n\r]+?)["']?\s*$""")

    // Matches  stdlib = ["micropython"]  or  stdlib = ["circuitpython", "other"]
    private val STDLIB_ARRAY_RE = Regex("""^\s*stdlib\s*=\s*\[([^\]]*)]""")

    // Matches a new [section] header (to stop parsing)
    private val NEW_SECTION_RE  = Regex("""^\s*\[""")

    /**
     * Locate pyproject.toml in the project base directory.
     */
    fun findPyproject(project: Project): VirtualFile? {
        val basePath = project.basePath ?: return null
        return LocalFileSystem.getInstance().findFileByPath("$basePath/pyproject.toml")
    }

    /**
     * Parse [tool.whip] and return a [WhipsnakeConfig], or null if the file/section is absent.
     */
    fun findConfig(project: Project): WhipsnakeConfig? {
        val file = findPyproject(project) ?: return null
        return parseContent(String(file.contentsToByteArray()))
    }

    /**
     * Exposed for tests — parse raw TOML content.
     */
    fun parseContent(content: String): WhipsnakeConfig? {
        val lines = content.lines()

        // Check for [tool.pymcu.ffi] anywhere in the file
        val hasFfi = lines.any { FFI_SECTION_RE.containsMatchIn(it) }

        // Find the [tool.whip] section start
        var sectionStart = -1
        for ((index, line) in lines.withIndex()) {
            if (SECTION_RE.containsMatchIn(line)) {
                sectionStart = index + 1
                break
            }
        }
        if (sectionStart < 0) return null

        // Collect key=value pairs until the next section header
        var chip: String?      = null
        var board: String?     = null
        var frequency: String? = null
        var sources: String?   = null
        var entry: String?     = null
        var stdlib             = emptyList<String>()

        for (i in sectionStart until lines.size) {
            val line = lines[i]
            if (NEW_SECTION_RE.containsMatchIn(line)) break

            // stdlib array: stdlib = ["micropython"]  or  stdlib = ["circuitpython"]
            STDLIB_ARRAY_RE.find(line)?.let { m ->
                stdlib = m.groupValues[1]
                    .split(",")
                    .map { it.trim().trim('"', '\'') }
                    .filter { it.isNotEmpty() }
                return@let
            }

            val match = KV_RE.matchEntire(line) ?: continue
            val key   = match.groupValues[1]
            val value = match.groupValues[2].trim()
            when (key) {
                "chip"      -> chip      = value
                "board"     -> board     = value
                "frequency" -> frequency = value
                "sources"   -> sources   = value
                "entry"     -> entry     = value
            }
        }

        return WhipsnakeConfig(
            chip      = chip,
            board     = board,
            frequency = frequency,
            sources   = sources,
            entry     = entry,
            stdlib    = stdlib,
            hasFfi    = hasFfi
        )
    }
}
