package dev.begeistert.pymcu.config

import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile

/**
 * Holds the parsed contents of the [tool.pymcu] section of pyproject.toml.
 */
data class PyMcuConfig(
    val chip: String?,
    val frequency: String?,
    val sources: String?,
    val entry: String?
)

/**
 * Reads [tool.pymcu] from pyproject.toml located at the project base directory.
 * Uses simple regex — no external TOML library required.
 */
object PyMcuConfigReader {

    private val SECTION_RE = Regex("""\[tool\.pymcu]""")

    // Matches key = "value"  or  key = 'value'  or  key = bare_value
    private val KV_RE = Regex("""^\s*(\w+)\s*=\s*["']?([^"'\n\r]+?)["']?\s*$""")

    // Matches a new [section] header (to stop parsing)
    private val NEW_SECTION_RE = Regex("""^\s*\[""")

    /**
     * Locate pyproject.toml in the project base directory.
     */
    fun findPyproject(project: Project): VirtualFile? {
        val basePath = project.basePath ?: return null
        return LocalFileSystem.getInstance().findFileByPath("$basePath/pyproject.toml")
    }

    /**
     * Parse [tool.pymcu] and return a [PyMcuConfig], or null if the file/section is absent.
     */
    fun findConfig(project: Project): PyMcuConfig? {
        val file = findPyproject(project) ?: return null
        return parseContent(String(file.contentsToByteArray()))
    }

    /**
     * Exposed for tests — parse raw TOML content.
     */
    fun parseContent(content: String): PyMcuConfig? {
        val lines = content.lines()

        // Find the [tool.pymcu] section start
        var sectionStart = -1
        for ((index, line) in lines.withIndex()) {
            if (SECTION_RE.containsMatchIn(line)) {
                sectionStart = index + 1
                break
            }
        }
        if (sectionStart < 0) return null

        // Collect key=value pairs until the next section header
        var chip: String? = null
        var frequency: String? = null
        var sources: String? = null
        var entry: String? = null

        for (i in sectionStart until lines.size) {
            val line = lines[i]
            if (NEW_SECTION_RE.containsMatchIn(line)) break  // next section reached
            val match = KV_RE.matchEntire(line) ?: continue
            val key = match.groupValues[1]
            val value = match.groupValues[2].trim()
            when (key) {
                "chip"      -> chip = value
                "frequency" -> frequency = value
                "sources"   -> sources = value
                "entry"     -> entry = value
            }
        }

        return PyMcuConfig(chip = chip, frequency = frequency, sources = sources, entry = entry)
    }
}
