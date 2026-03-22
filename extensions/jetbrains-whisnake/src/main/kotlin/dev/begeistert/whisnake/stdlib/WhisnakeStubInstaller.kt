package dev.begeistert.whisnake.stdlib

import com.intellij.openapi.diagnostic.Logger
import java.io.File
import java.nio.file.Files
import java.nio.file.Path
import kotlin.io.path.exists

/**
 * Writes PEP 561 `.pyi` stub files into `.venv/site-packages` for the
 * CircuitPython and MicroPython compat layers.
 *
 * WHY .pyi instead of .py re-exports:
 *   The pymcu compiler searches include paths in order:
 *     1. dist/_generated          (generated board.py)
 *     2. site-packages            ← .py shims would be found HERE
 *     3. site-packages/pymcu_circuitpython  (actual implementations)
 *
 *   A `.py` re-export shim in site-packages is found before the real module,
 *   so the compiler registers `DigitalInOut` under module name `digitalio` and
 *   generates label `digitalio_DigitalInOut` — but the real label in the
 *   compiled implementation is `pymcu_circuitpython_digitalio_DigitalInOut`.
 *   This mismatch causes assembler "label not found" errors.
 *
 *   `.pyi` files are ignored by the pymcu compiler (which only loads `.py`),
 *   so they are invisible to compilation while still resolving IDE imports.
 *
 * Stubs are generated dynamically from the installed stdlib source files via
 * [PyStubGenerator], so they stay in sync with the stdlib automatically.
 *
 * VFS refresh:
 *   Files are written via standard Java I/O (bypassing IntelliJ's VFS), so we
 *   call LocalFileSystem.refresh() afterward to trigger PyCharm re-indexing.
 */
object WhisnakeStubInstaller {

    private val log = Logger.getInstance(WhisnakeStubInstaller::class.java)

    /**
     * Writes `.pyi` stubs into `.venv/site-packages` and proactively generates
     * `dist/_generated/board.py` so PyCharm can resolve `import board` before the
     * first build. Returns the site-packages [Path] if any stubs were written (so
     * the caller can refresh the VFS), or null if nothing changed or no `.venv`
     * was found.
     */
    fun install(basePath: String, stdlib: List<String>, board: String?): Path? {
        val sitePackages = findSitePackages(basePath)
        var changed = false

        if (sitePackages != null) {
            if ("circuitpython" in stdlib) {
                changed = installPackage(sitePackages, "whisnake_circuitpython") || changed
                changed = installBoardStub(sitePackages, board) || changed
            }
            if ("micropython" in stdlib) {
                changed = installPackage(sitePackages, "whisnake_micropython") || changed
            }
        }

        // Proactively write dist/_generated/board.py so `import board` resolves
        // in the IDE even before the first build.
        if ("circuitpython" in stdlib) {
            changed = generateDistBoard(basePath, board, sitePackages) || changed
        }

        return if (changed) sitePackages else null
    }

    // ── Generic package stub installer ───────────────────────────────────────

    /**
     * For every public `.py` in `site-packages/<pkgName>/`:
     *  1. Removes any legacy `.py` re-export shim at `site-packages/<module>.py`
     *  2. Generates a `.pyi` stub via [PyStubGenerator] and writes it to
     *     `site-packages/<module>.pyi`
     *
     * "Public" means: not `__init__.py`, not starting with `_`, not `board_chips.py`
     * (internal helper). Subdirectories (e.g. `boards/`) are skipped.
     */
    private fun installPackage(sitePackages: Path, pkgName: String): Boolean {
        val pkgDir = sitePackages.resolve(pkgName)
        if (!pkgDir.exists()) {
            log.warn("Whisnake stubs: $pkgName not found in $sitePackages — run sync first")
            return false
        }

        val sourceFiles = pkgDir.toFile()
            .listFiles { f ->
                f.isFile &&
                f.extension == "py" &&
                !f.name.startsWith("_") &&
                f.name != "board_chips.py"
            }
            ?: return false

        if (sourceFiles.isEmpty()) {
            log.warn("Whisnake stubs: no public .py files found in $pkgDir")
            return false
        }

        for (source in sourceFiles) {
            val moduleName = source.nameWithoutExtension
            // Remove any legacy .py re-export shim that would break compilation
            sitePackages.resolve("$moduleName.py").toFile().delete()
            // Generate and write the .pyi stub from the actual source
            val stub = PyStubGenerator.generateFrom(source.toPath()) ?: continue
            writeStub(sitePackages.resolve("$moduleName.pyi"), stub)
        }

        log.info("Whisnake stubs: generated .pyi stubs for $pkgName in $sitePackages")
        return true
    }

    // ── board.pyi ─────────────────────────────────────────────────────────────

    /**
     * Generates `board.pyi` in site-packages from the board constants file bundled
     * inside `pymcu_circuitpython/boards/<boardId>.py`. Each `NAME = "value"` line
     * becomes a typed `NAME: str` stub with an inline docstring.
     */
    private fun installBoardStub(sitePackages: Path, board: String?): Boolean {
        val pkg     = sitePackages.resolve("whisnake_circuitpython")
        val boardId = board?.replace("-", "_") ?: "arduino_uno"
        val source  = listOf(
            pkg.resolve("boards/$boardId.py"),
            pkg.resolve("boards/arduino_uno.py")
        ).firstOrNull { it.exists() } ?: run {
            log.debug("Whisnake: no board constants file found — skipping board.pyi")
            return false
        }
        // Remove legacy .py shim
        sitePackages.resolve("board.py").toFile().delete()
        writeStub(sitePackages.resolve("board.pyi"), buildBoardStub(source))
        return true
    }

    /** Parse `NAME = "value"` lines from a board constants file → typed `NAME: str` stubs. */
    private fun buildBoardStub(boardFile: Path): String {
        val lineRe = Regex("""^\s*([A-Z][A-Z0-9_]*)\s*=\s*["']([^"']*)["']""")
        val sb      = StringBuilder("# Whisnake — generated board pin stubs (do not edit manually)\n")
        val dquote3 = "\"\"\""
        sb.append("$dquote3\n")
        sb.append("Board-specific pin name constants.\n\n")
        sb.append("Each constant is a string identifier that maps to a physical pin on the board.\n")
        sb.append("Pass these directly to DigitalInOut, AnalogIn, PWMOut, busio.UART, etc.\n")
        sb.append("$dquote3\n\n")
        boardFile.toFile().forEachLine { line ->
            lineRe.find(line)?.let { m ->
                val name  = m.groupValues[1]
                val value = m.groupValues[2]
                sb.append("$name: str\n")
                sb.append("${dquote3}Pin identifier for $name (hardware value: \"$value\").${dquote3}\n")
            }
        }
        return sb.toString()
    }

    // ── dist/_generated/board.py ─────────────────────────────────────────────

    /**
     * Copies the board constants file from the installed `pymcu_circuitpython`
     * package into `<projectRoot>/dist/_generated/board.py`.
     *
     * This lets PyCharm resolve `import board` via the AdditionalLibraryRootsProvider
     * even before the first build (which would normally generate the file).
     * The build will overwrite it with identical content; no conflict arises.
     */
    private fun generateDistBoard(basePath: String, board: String?, sitePackages: Path?): Boolean {
        val pkg     = sitePackages?.resolve("whisnake_circuitpython")
        val boardId = board?.replace("-", "_") ?: "arduino_uno"
        val source  = when {
            pkg != null -> listOf(
                pkg.resolve("boards/$boardId.py"),
                pkg.resolve("boards/arduino_uno.py")
            ).firstOrNull { it.exists() }
            else -> null
        }
        if (source == null) {
            log.debug("Whisnake: no board constants file found — skipping dist/_generated/board.py")
            return false
        }
        return try {
            val generatedDir = Path.of(basePath, "dist", "_generated")
            Files.createDirectories(generatedDir)
            Files.writeString(generatedDir.resolve("board.py"), source.toFile().readText())
            log.info("Whisnake: wrote dist/_generated/board.py for IDE `import board` support")
            true
        } catch (e: Exception) {
            log.warn("Whisnake: failed to write dist/_generated/board.py", e)
            false
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private fun writeStub(target: Path, content: String) {
        try {
            Files.writeString(target, content)
        } catch (e: Exception) {
            log.warn("Whisnake stubs: failed to write ${target.fileName}", e)
        }
    }

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
