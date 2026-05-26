package dev.begeistert.pymcu.config

import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile

data class PyMcuConfig(
    val chip: String?,
    val board: String?,
    val frequency: String?,
    val sources: String?,
    val entry: String?,
    val stdlib: List<String> = emptyList(),
    val hasFfi: Boolean = false
) {
    val displayName: String
        get() = when (board) {
            "arduino_uno"   -> "Arduino Uno (atmega328p)"
            "arduino_nano"  -> "Arduino Nano (atmega328p)"
            "arduino_mega"  -> "Arduino Mega (atmega2560)"
            "arduino_micro" -> "Arduino Micro (atmega32u4)"
            "attiny85"      -> "ATtiny85"
            "attiny84"      -> "ATtiny84"
            "attiny2313"    -> "ATtiny2313"
            else            -> board ?: chip ?: "(unknown)"
        }
}

/**
 * Reads [tool.pymcu] (or legacy [tool.whip]) from pyproject.toml.
 * [tool.pymcu] is the canonical section; [tool.whip] is the legacy name from the early alpha.
 */
object PyMcuConfigReader {

    // Primary section header
    private val SECTION_PRIMARY_RE = Regex("""\[tool\.pymcu]""")
    // Legacy section header (projects created during alpha under the "whip" name)
    private val SECTION_LEGACY_RE  = Regex("""\[tool\.whip]""")

    // FFI sections
    private val FFI_PRIMARY_RE = Regex("""\[tool\.pymcu\.ffi]""")
    private val FFI_LEGACY_RE  = Regex("""\[tool\.whip\.ffi]""")

    private val KV_RE          = Regex("""^\s*(\w+)\s*=\s*["']?([^"'\[\n\r]+?)["']?\s*$""")
    private val STDLIB_ARRAY_RE = Regex("""^\s*stdlib\s*=\s*\[([^\]]*)]""")
    private val NEW_SECTION_RE  = Regex("""^\s*\[""")

    fun findPyproject(project: Project): VirtualFile? {
        val basePath = project.basePath ?: return null
        return LocalFileSystem.getInstance().findFileByPath("$basePath/pyproject.toml")
    }

    fun findConfig(project: Project): PyMcuConfig? {
        val file = findPyproject(project) ?: return null
        return parseContent(String(file.contentsToByteArray()))
    }

    fun parseContent(content: String): PyMcuConfig? {
        val lines = content.lines()

        val hasFfi = lines.any { FFI_PRIMARY_RE.containsMatchIn(it) || FFI_LEGACY_RE.containsMatchIn(it) }

        var sectionStart = -1
        for ((index, line) in lines.withIndex()) {
            if (SECTION_PRIMARY_RE.containsMatchIn(line) || SECTION_LEGACY_RE.containsMatchIn(line)) {
                sectionStart = index + 1
                break
            }
        }
        if (sectionStart < 0) return null

        var chip: String?      = null
        var board: String?     = null
        var frequency: String? = null
        var sources: String?   = null
        var entry: String?     = null
        var stdlib             = emptyList<String>()

        for (i in sectionStart until lines.size) {
            val line = lines[i]
            if (NEW_SECTION_RE.containsMatchIn(line)) break

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
                "chip", "target" -> chip = value   // "target" is the current field name; "chip" is legacy
                "board"          -> board     = value
                "frequency"      -> frequency = value
                "sources"        -> sources   = value
                "entry"          -> entry     = value
            }
        }

        return PyMcuConfig(
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
