package dev.begeistert.pymcu.newproject

import com.intellij.facet.ui.ValidationResult
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.module.Module
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.DirectoryProjectGenerator
import com.intellij.platform.ProjectGeneratorPeer
import javax.swing.Icon

/**
 * Adds a "PyMCU" entry to PyCharm's New Project wizard.
 *
 * On generation it scaffolds:
 *   pyproject.toml   — [project] + [tool.pymcu] sections
 *   src/main.py      — minimal starter for the chosen chip
 *
 * A background sync is then run using the configured package manager.
 */
class PyMcuProjectGenerator : DirectoryProjectGenerator<PyMcuNewProjectSettings> {

    private val log = Logger.getInstance(PyMcuProjectGenerator::class.java)

    override fun getName(): String = "PyMCU"

    override fun getDescription(): String =
        "Create a PyMCU project targeting an AVR or PIC microcontroller."

    override fun getLogo(): Icon? = null   // replace with a real icon once assets are added

    override fun validate(baseDirPath: String): ValidationResult = ValidationResult.OK

    override fun createPeer(): ProjectGeneratorPeer<PyMcuNewProjectSettings> =
        PyMcuProjectGeneratorPeer()

    override fun generateProject(
        project: Project,
        baseDir: VirtualFile,
        settings: PyMcuNewProjectSettings,
        module: Module
    ) {
        val basePath = baseDir.path
        val projectName = project.name.ifBlank { baseDir.name }

        ApplicationManager.getApplication().runWriteAction {
            try {
                // --- pyproject.toml ---
                val pyproject = buildPyproject(projectName, settings)
                writeFile(baseDir, "pyproject.toml", pyproject)

                // --- src/ directory + main.py ---
                val srcDir = baseDir.createChildDirectory(this, "src")
                writeFile(srcDir, "main.py", buildMainPy(settings.chip))

                log.info("PyMCU project scaffolded at $basePath")
            } catch (e: Exception) {
                log.error("Failed to scaffold PyMCU project", e)
            }
        }

        // Run dependency sync in background after scaffolding
        ApplicationManager.getApplication().executeOnPooledThread {
            runSync(basePath, settings.packageManager)
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private fun writeFile(dir: VirtualFile, name: String, content: String) {
        val child = dir.findOrCreateChildData(this, name)
        child.setBinaryContent(content.toByteArray(Charsets.UTF_8))
    }

    private fun buildPyproject(name: String, s: PyMcuNewProjectSettings): String = """
        [project]
        name = "$name"
        version = "0.1.0"
        requires-python = ">=3.11"
        dependencies = [
            "pymcu-stdlib>=0.1.2a5",
            "pymcu-compiler>=0.1.0a27"
        ]

        [tool.pymcu]
        chip = "${s.chip}"
        frequency = ${s.frequency}
        sources = "src"
        entry = "main.py"
    """.trimIndent()

    private fun buildMainPy(chip: String): String {
        val isAvr = chip.startsWith("atmega") || chip.startsWith("attiny")
        return if (isAvr) buildAvrTemplate(chip) else buildGenericTemplate(chip)
    }

    private fun buildAvrTemplate(chip: String): String = """
        # PyMCU project — target: $chip
        from pymcu.types import uint8
        from pymcu.hal.gpio import Pin
        from pymcu.time import delay_ms


        def main():
            led = Pin("PB5", Pin.OUT)   # Arduino Uno built-in LED (change for your board)

            while True:
                led.high()
                delay_ms(500)
                led.low()
                delay_ms(500)
    """.trimIndent()

    private fun buildGenericTemplate(chip: String): String = """
        # PyMCU project — target: $chip
        from pymcu.types import uint8
        from pymcu.time import delay_ms


        def main():
            while True:
                delay_ms(1000)
    """.trimIndent()

    private fun runSync(basePath: String, packageManager: String) {
        val command: List<String> = when (packageManager) {
            "uv"     -> listOf("uv", "sync")
            "poetry" -> listOf("poetry", "install")
            "pipenv" -> listOf("pipenv", "install")
            "pip"    -> listOf("pip", "install", "-e", ".")
            else     -> listOf("uv", "sync")
        }
        log.info("PyMCU new-project sync: ${command.joinToString(" ")} in $basePath")
        try {
            ProcessBuilder(command)
                .directory(java.io.File(basePath))
                .redirectErrorStream(true)
                .start()
                .waitFor()
        } catch (e: Exception) {
            log.warn("PyMCU sync after project creation failed", e)
        }
    }
}
