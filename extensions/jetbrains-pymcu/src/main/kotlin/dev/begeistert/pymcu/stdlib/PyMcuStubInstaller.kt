package dev.begeistert.pymcu.stdlib

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
 * VFS refresh:
 *   Files are written via standard Java I/O (bypassing IntelliJ's VFS), so we
 *   call LocalFileSystem.refresh() afterward to trigger PyCharm re-indexing.
 */
object PyMcuStubInstaller {

    private val log = Logger.getInstance(PyMcuStubInstaller::class.java)

    // Legacy .py shim names — cleaned up if present from a previous install
    private val CP_PY_SHIMS = listOf("board.py", "digitalio.py", "busio.py",
        "analogio.py", "pwmio.py", "microcontroller.py")
    private val MP_PY_SHIMS = listOf("machine.py", "utime.py", "micropython.py")

    /**
     * Writes `.pyi` stubs into `.venv/site-packages`. Returns the site-packages
     * [Path] if any stubs were written (so the caller can refresh the VFS), or
     * null if nothing changed or no `.venv` was found.
     */
    fun install(basePath: String, stdlib: List<String>, board: String?): Path? {
        val sitePackages = findSitePackages(basePath) ?: return null
        var changed = false

        if ("circuitpython" in stdlib) {
            changed = installCircuitPython(sitePackages, board) || changed
        }
        if ("micropython" in stdlib) {
            changed = installMicroPython(sitePackages) || changed
        }

        return if (changed) sitePackages else null
    }

    // ── CircuitPython ────────────────────────────────────────────────────────

    private fun installCircuitPython(sitePackages: Path, board: String?): Boolean {
        val pkg = sitePackages.resolve("pymcu_circuitpython")
        if (!pkg.exists()) {
            log.warn("PyMCU stubs: pymcu_circuitpython not found in $sitePackages — run sync first")
            return false
        }

        // Remove any legacy .py re-export shims that break compilation
        CP_PY_SHIMS.forEach { sitePackages.resolve(it).toFile().delete() }

        // board.pyi — generated from the board constants file
        val boardId = board?.replace("-", "_") ?: "arduino_uno"
        val boardFile = pkg.resolve("boards/$boardId.py")
        val fallback  = pkg.resolve("boards/arduino_uno.py")
        val sourceBoard = when {
            boardFile.exists() -> boardFile
            fallback.exists()  -> fallback
            else               -> null
        }
        if (sourceBoard != null) {
            writeStub(sitePackages.resolve("board.pyi"), buildBoardStub(sourceBoard))
        }

        writeStub(sitePackages.resolve("digitalio.pyi"), DIGITALIO_STUB)
        writeStub(sitePackages.resolve("busio.pyi"),     BUSIO_STUB)

        if (pkg.resolve("analogio.py").exists())
            writeStub(sitePackages.resolve("analogio.pyi"), ANALOGIO_STUB)
        if (pkg.resolve("pwmio.py").exists())
            writeStub(sitePackages.resolve("pwmio.pyi"),    PWMIO_STUB)
        if (pkg.resolve("microcontroller.py").exists())
            writeStub(sitePackages.resolve("microcontroller.pyi"), MICROCONTROLLER_STUB)

        log.info("PyMCU stubs: CircuitPython .pyi stubs installed in $sitePackages")
        return true
    }

    /** Parse `VAR = "VALUE"` lines from a board constants file → `VAR: str` stubs. */
    private fun buildBoardStub(boardFile: Path): String {
        val lineRe = Regex("""^\s*([A-Z][A-Z0-9_]*)\s*=\s*["']""")
        val sb = StringBuilder("# PyMCU — generated board pin stubs\n")
        boardFile.toFile().forEachLine { line ->
            lineRe.find(line)?.let { sb.append("${it.groupValues[1]}: str\n") }
        }
        return sb.toString()
    }

    // ── MicroPython ──────────────────────────────────────────────────────────

    private fun installMicroPython(sitePackages: Path): Boolean {
        val pkg = sitePackages.resolve("pymcu_micropython")
        if (!pkg.exists()) {
            log.warn("PyMCU stubs: pymcu_micropython not found in $sitePackages — run sync first")
            return false
        }

        // Remove any legacy .py re-export shims
        MP_PY_SHIMS.forEach { sitePackages.resolve(it).toFile().delete() }

        writeStub(sitePackages.resolve("machine.pyi"),     MACHINE_STUB)
        writeStub(sitePackages.resolve("utime.pyi"),       UTIME_STUB)
        writeStub(sitePackages.resolve("micropython.pyi"), MICROPYTHON_STUB)

        log.info("PyMCU stubs: MicroPython .pyi stubs installed in $sitePackages")
        return true
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private fun writeStub(target: Path, content: String) {
        try {
            Files.writeString(target, content)
        } catch (e: Exception) {
            log.warn("PyMCU stubs: failed to write ${target.fileName}", e)
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

    // ── Stub content ─────────────────────────────────────────────────────────

    private val DIGITALIO_STUB = """
# PyMCU — CircuitPython digitalio stubs
class Direction:
    INPUT: int
    OUTPUT: int

class Pull:
    NONE: int
    UP: int
    DOWN: int

class DriveMode:
    PUSH_PULL: int
    OPEN_DRAIN: int

class DigitalInOut:
    direction: int
    value: int
    pull: int
    drive_mode: int
    def __init__(self, pin: str) -> None: ...
    def set_direction(self, d: int) -> None: ...
    def set_value(self, val: int) -> None: ...
    def get_value(self) -> int: ...
    def set_pull(self, p: int) -> None: ...
    def deinit(self) -> None: ...
""".trimIndent() + "\n"

    private val BUSIO_STUB = """
# PyMCU — CircuitPython busio stubs
class UART:
    def __init__(self, tx: str, rx: str, baudrate: int = 9600) -> None: ...
    def write(self, data: int) -> None: ...
    def write_str(self, s: str) -> None: ...
    def println(self, s: str) -> None: ...
    def print_byte(self, value: int) -> None: ...
    def read(self) -> int: ...

class I2C:
    def __init__(self, scl: str, sda: str, frequency: int = 100000) -> None: ...
    def scan(self) -> list: ...
    def readfrom_into(self, address: int, buffer: object) -> None: ...
    def writeto(self, address: int, buffer: bytes) -> None: ...

class SPI:
    def __init__(self, clock: str, MOSI: str = ..., MISO: str = ...) -> None: ...
    def configure(self, baudrate: int = 100000, polarity: int = 0, phase: int = 0, bits: int = 8) -> None: ...
    def write(self, buf: bytes) -> None: ...
    def readinto(self, buf: object) -> None: ...
""".trimIndent() + "\n"

    private val ANALOGIO_STUB = """
# PyMCU — CircuitPython analogio stubs
class AnalogIn:
    value: int
    reference_voltage: float
    def __init__(self, pin: str) -> None: ...
    def deinit(self) -> None: ...

class AnalogOut:
    value: int
    def __init__(self, pin: str) -> None: ...
    def deinit(self) -> None: ...
""".trimIndent() + "\n"

    private val PWMIO_STUB = """
# PyMCU — CircuitPython pwmio stubs
class PWMOut:
    duty_cycle: int
    frequency: int
    def __init__(self, pin: str, frequency: int = 500, duty_cycle: int = 0) -> None: ...
    def deinit(self) -> None: ...
""".trimIndent() + "\n"

    private val MICROCONTROLLER_STUB = """
# PyMCU — CircuitPython microcontroller stubs
def delay_us(duration: int) -> None: ...
def reset() -> None: ...
""".trimIndent() + "\n"

    private val MACHINE_STUB = """
# PyMCU — MicroPython machine stubs
class Pin:
    IN: int
    OUT: int
    PULL_UP: int
    PULL_DOWN: int
    def __init__(self, id: object, mode: int = ..., pull: int = ...) -> None: ...
    def value(self, x: int = ...) -> int: ...
    def high(self) -> None: ...
    def low(self) -> None: ...
    def toggle(self) -> None: ...

class UART:
    def __init__(self, id: int, baudrate: int = 9600, tx: object = None, rx: object = None) -> None: ...
    def read(self, nbytes: int = ...) -> bytes: ...
    def write(self, buf: object) -> int: ...
    def any(self) -> int: ...

class ADC:
    def __init__(self, pin: object) -> None: ...
    def read(self) -> int: ...
    def read_u16(self) -> int: ...

class PWM:
    def __init__(self, pin: object, freq: int = 500, duty: int = 0) -> None: ...
    def freq(self, value: int = ...) -> int: ...
    def duty(self, value: int = ...) -> int: ...
    def duty_u16(self, value: int = ...) -> int: ...

class SPI:
    def __init__(self, id: int, baudrate: int = 100000, polarity: int = 0, phase: int = 0, bits: int = 8, sck: object = None, mosi: object = None, miso: object = None) -> None: ...
    def write(self, buf: bytes) -> None: ...
    def read(self, nbytes: int, write: int = 0) -> bytes: ...
    def readinto(self, buf: object, write: int = 0) -> None: ...
    def write_readinto(self, write_buf: bytes, read_buf: object) -> None: ...

class I2C:
    def __init__(self, id: int = 0, scl: object = None, sda: object = None, freq: int = 400000) -> None: ...
    def scan(self) -> list: ...
    def readfrom(self, addr: int, nbytes: int) -> bytes: ...
    def readfrom_into(self, addr: int, buf: object) -> None: ...
    def writeto(self, addr: int, buf: bytes) -> None: ...
    def writeto_mem(self, addr: int, memaddr: int, buf: bytes) -> None: ...
    def readfrom_mem_into(self, addr: int, memaddr: int, buf: object) -> None: ...
""".trimIndent() + "\n"

    private val UTIME_STUB = """
# PyMCU — MicroPython utime stubs
def sleep_ms(ms: int) -> None: ...
def sleep_us(us: int) -> None: ...
def sleep(seconds: int) -> None: ...
def ticks_ms() -> int: ...
def ticks_diff(new_ticks: int, old_ticks: int) -> int: ...
""".trimIndent() + "\n"

    private val MICROPYTHON_STUB = """
# PyMCU — MicroPython micropython stubs
def const(x: int) -> int: ...
def opt_level(level: int = ...) -> int: ...
def mem_info(verbose: int = ...) -> None: ...
def qstr_info(verbose: int = ...) -> None: ...
""".trimIndent() + "\n"
}
