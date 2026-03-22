package dev.begeistert.whipsnake.newproject

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
 * Adds a "Whipsnake" entry to PyCharm's New Project wizard.
 *
 * On generation it scaffolds:
 *   pyproject.toml   — [project] + [tool.whip] sections
 *   src/main.py      — minimal starter for the chosen chip
 *
 * A background sync is then run using the configured package manager.
 */
class WhipsnakeProjectGenerator : DirectoryProjectGenerator<WhipsnakeNewProjectSettings> {

    private val log = Logger.getInstance(WhipsnakeProjectGenerator::class.java)

    override fun getName(): String = "Whipsnake"

    override fun getDescription(): String =
        "Create a Whipsnake project targeting an AVR or PIC microcontroller."

    override fun getLogo(): Icon? = null   // replace with a real icon once assets are added

    override fun validate(baseDirPath: String): ValidationResult = ValidationResult.OK

    override fun createPeer(): ProjectGeneratorPeer<WhipsnakeNewProjectSettings> =
        WhipsnakeProjectGeneratorPeer()

    override fun generateProject(
        project: Project,
        baseDir: VirtualFile,
        settings: WhipsnakeNewProjectSettings,
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
                writeFile(srcDir, "main.py", buildMainPy(settings))

                log.info("Whipsnake project scaffolded at $basePath")
            } catch (e: Exception) {
                log.error("Failed to scaffold Whipsnake project", e)
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

    private fun buildPyproject(name: String, s: WhipsnakeNewProjectSettings): String {
        // board OR chip line
        val targetLine = if (s.board != null)
            """board = "${s.board}""""
        else
            """chip = "${s.chip ?: "atmega328p"}""""

        // stdlib compat package dependency + config line
        val (stdlibDep, stdlibLine) = when (s.stdlib) {
            "micropython"   -> Pair(
                "\n    \"whipsnake-micropython>=0.1.0a1\",",
                "\nstdlib = [\"micropython\"]"
            )
            "circuitpython" -> Pair(
                "\n    \"whipsnake-circuitpython>=0.1.0a1\",",
                "\nstdlib = [\"circuitpython\"]"
            )
            else -> Pair("", "")
        }

        return """
            [project]
            name = "$name"
            version = "0.1.0"
            requires-python = ">=3.11"
            dependencies = [
                "whipsnake-stdlib>=0.1.0a1",
                "whipsnake>=0.1.0a1"$stdlibDep
            ]

            [tool.whip]
            $targetLine
            frequency = ${s.frequency}
            sources = "src"
            entry = "main.py"$stdlibLine
        """.trimIndent()
    }

    private fun buildMainPy(s: WhipsnakeNewProjectSettings): String {
        val effectiveChip = s.board?.let {
            mapOf(
                "arduino_uno"   to "atmega328p",
                "arduino_nano"  to "atmega328p",
                "arduino_mega"  to "atmega2560",
                "arduino_micro" to "atmega32u4"
            )[it]
        } ?: s.chip ?: "atmega328p"

        return when (s.stdlib) {
            "micropython"   -> buildMicroPythonTemplate(s.board, effectiveChip)
            "circuitpython" -> buildCircuitPythonTemplate(s.board, effectiveChip)
            else -> {
                val isAvr = effectiveChip.startsWith("atmega") || effectiveChip.startsWith("attiny")
                if (isAvr) buildAvrTemplate(s.board, effectiveChip) else buildGenericTemplate(effectiveChip)
            }
        }
    }

    /** Bare Whipsnake template using whipsnake.hal directly. */
    private fun buildAvrTemplate(board: String?, chip: String): String {
        val target = if (board != null) "$board ($chip)" else chip
        val ledPin = if (board?.startsWith("arduino") == true) "PB5" else "PB5"
        return """
            # Whipsnake -- target: $target
            # Blinks the built-in LED on pin $ledPin (Arduino D13).
            from whipsnake.types import uint8
            from whipsnake.hal.gpio import Pin
            from whipsnake.time import delay_ms


            def main():
                led = Pin("$ledPin", Pin.OUT)

                while True:
                    led.high()
                    delay_ms(500)
                    led.low()
                    delay_ms(500)
        """.trimIndent()
    }

    /** Bare Whipsnake template for non-AVR chips. */
    private fun buildGenericTemplate(chip: String): String = """
        # Whipsnake -- target: $chip
        from whipsnake.types import uint8
        from whipsnake.time import delay_ms


        def main():
            while True:
                delay_ms(1000)
    """.trimIndent()

    /**
     * MicroPython compat template (whipsnake-micropython).
     * Uses the `machine` and `utime` modules from the compat layer.
     */
    private fun buildMicroPythonTemplate(board: String?, chip: String): String {
        val target = if (board != null) "$board ($chip)" else chip
        return """
            # Whipsnake -- target: $target  [MicroPython compat]
            # Requires whipsnake-micropython in pyproject.toml dependencies.
            from machine import Pin
            from utime import sleep_ms


            def main():
                led = Pin(13, Pin.OUT)   # D13 = built-in LED on Arduino boards

                while True:
                    led.value(1)
                    sleep_ms(500)
                    led.value(0)
                    sleep_ms(500)
        """.trimIndent()
    }

    /**
     * CircuitPython compat template (whipsnake-circuitpython).
     * Uses the `board`, `digitalio`, and `time` modules from the compat layer.
     */
    private fun buildCircuitPythonTemplate(board: String?, chip: String): String {
        val target = if (board != null) "$board ($chip)" else chip
        return """
            # Whipsnake -- target: $target  [CircuitPython compat]
            # Requires whipsnake-circuitpython in pyproject.toml dependencies.
            import board
            import digitalio
            import time


            def main():
                led = digitalio.DigitalInOut(board.LED_BUILTIN)
                led.direction = digitalio.Direction.OUTPUT

                while True:
                    led.value = True
                    time.sleep_ms(500)
                    led.value = False
                    time.sleep_ms(500)
        """.trimIndent()
    }

    private fun runSync(basePath: String, packageManager: String) {
        val command: List<String> = when (packageManager) {
            "uv"     -> listOf("uv", "sync")
            "poetry" -> listOf("poetry", "install")
            "pipenv" -> listOf("pipenv", "install")
            "pip"    -> listOf("pip", "install", "-e", ".")
            else     -> listOf("uv", "sync")
        }
        log.info("Whipsnake new-project sync: ${command.joinToString(" ")} in $basePath")
        try {
            ProcessBuilder(command)
                .directory(java.io.File(basePath))
                .redirectErrorStream(true)
                .start()
                .waitFor()
        } catch (e: Exception) {
            log.warn("Whipsnake sync after project creation failed", e)
        }
    }
}
