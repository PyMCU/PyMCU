package dev.begeistert.pymcu.stdlib

import com.intellij.openapi.diagnostic.Logger
import java.io.File
import java.nio.file.Files
import java.nio.file.Path
import kotlin.io.path.exists

/**
 * Installs top-level shim modules into the project's .venv site-packages so
 * PyCharm can resolve CircuitPython and MicroPython imports.
 *
 * The compat packages are installed as:
 *   pymcu_circuitpython/digitalio.py   (not importable as  import digitalio)
 *   pymcu_micropython/machine.py       (not importable as  import machine)
 *
 * This installer writes thin re-export shims at the site-packages root:
 *   digitalio.py  →  from pymcu_circuitpython.digitalio import *
 *   board.py      →  from pymcu_circuitpython.boards.arduino_uno import *
 *   machine.py    →  from pymcu_micropython.machine import *
 *   … etc.
 *
 * Called once on project open (in case .venv already exists) and once after
 * every uv/pip sync (so freshly created envs get shims immediately).
 */
object PyMcuStubInstaller {

    private val log = Logger.getInstance(PyMcuStubInstaller::class.java)

    private val CP_SHIMS = listOf("digitalio", "busio", "analogio", "pwmio", "microcontroller")
    private val MP_SHIMS = listOf("machine", "utime", "micropython")

    fun install(basePath: String, stdlib: List<String>, board: String?) {
        val sitePackages = findSitePackages(basePath) ?: return
        if ("circuitpython" in stdlib) installCircuitPython(sitePackages, board)
        if ("micropython"   in stdlib) installMicroPython(sitePackages)
    }

    // ── CircuitPython ────────────────────────────────────────────────────────

    private fun installCircuitPython(sitePackages: Path, board: String?) {
        val pkg = sitePackages.resolve("pymcu_circuitpython")
        if (!pkg.exists()) {
            log.warn("PyMCU stubs: pymcu_circuitpython not found in $sitePackages — run sync first")
            return
        }

        for (module in CP_SHIMS) {
            if (pkg.resolve("$module.py").exists()) {
                writeShim(sitePackages.resolve("$module.py"),
                    "from pymcu_circuitpython.$module import *\n")
            }
        }

        // board.py is board-specific; fall back to arduino_uno when unset
        val boardId = board?.replace("-", "_") ?: "arduino_uno"
        val boardPkg = "pymcu_circuitpython.boards.$boardId"
        val boardFile = pkg.resolve("boards/$boardId.py")
        val fallback  = pkg.resolve("boards/arduino_uno.py")
        val boardContent = when {
            boardFile.exists()  -> "from $boardPkg import *\n"
            fallback.exists()   -> "from pymcu_circuitpython.boards.arduino_uno import *\n"
            else                -> null
        }
        if (boardContent != null) {
            writeShim(sitePackages.resolve("board.py"), boardContent)
        }

        log.info("PyMCU stubs: CircuitPython shims installed in $sitePackages")
    }

    // ── MicroPython ──────────────────────────────────────────────────────────

    private fun installMicroPython(sitePackages: Path) {
        val pkg = sitePackages.resolve("pymcu_micropython")
        if (!pkg.exists()) {
            log.warn("PyMCU stubs: pymcu_micropython not found in $sitePackages — run sync first")
            return
        }

        for (module in MP_SHIMS) {
            if (pkg.resolve("$module.py").exists()) {
                writeShim(sitePackages.resolve("$module.py"),
                    "from pymcu_micropython.$module import *\n")
            }
        }

        log.info("PyMCU stubs: MicroPython shims installed in $sitePackages")
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private fun writeShim(target: Path, content: String) {
        try {
            Files.writeString(target, content)
        } catch (e: Exception) {
            log.warn("PyMCU stubs: failed to write ${target.fileName}", e)
        }
    }

    /**
     * Finds `.venv/lib/pythonX.Y/site-packages` (or `venv/...`) inside [basePath].
     */
    fun findSitePackages(basePath: String): Path? {
        for (venvName in listOf(".venv", "venv")) {
            val libDir = File(basePath).resolve("$venvName/lib")
            if (!libDir.exists()) continue
            val pythonDir = libDir.listFiles()?.firstOrNull { it.name.startsWith("python") }
                ?: continue
            val sp = pythonDir.resolve("site-packages").toPath()
            if (sp.exists()) return sp
        }
        return null
    }
}
