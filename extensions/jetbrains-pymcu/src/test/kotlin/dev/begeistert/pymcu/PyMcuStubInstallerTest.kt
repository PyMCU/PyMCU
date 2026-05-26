package dev.begeistert.pymcu

import dev.begeistert.pymcu.stdlib.PyMcuStubInstaller
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class PyMcuStubInstallerTest {

    @Rule
    @JvmField
    val tmp = TemporaryFolder()

    private fun fakeSitePackages(
        base: File,
        packageName: String,
        moduleFiles: List<String> = emptyList(),
        subPackages: Map<String, List<String>> = emptyMap()
    ): File {
        val sp = base.resolve(".venv/lib/python3.14/site-packages")
        val pkg = sp.resolve(packageName)
        pkg.mkdirs()
        pkg.resolve("__init__.py").writeText("# stub")
        for (f in moduleFiles) pkg.resolve(f).writeText("# $f")
        for ((sub, files) in subPackages) {
            val dir = pkg.resolve(sub)
            dir.mkdirs()
            dir.resolve("__init__.py").writeText("# $sub")
            for (f in files) dir.resolve(f).writeText("# $f")
        }
        return sp
    }

    // ── CircuitPython: .pyi stubs written ────────────────────────────────────

    @Test
    fun `writes digitalio pyi stub for circuitpython stdlib`() {
        val base = tmp.newFolder("cp-digitalio")
        fakeSitePackages(base, "pymcu_circuitpython",
            moduleFiles = listOf("digitalio.py"),
            subPackages = mapOf("boards" to listOf("arduino_uno.py"))
        )

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "arduino_uno")

        val stub = base.resolve(".venv/lib/python3.14/site-packages/digitalio.pyi")
        assertTrue("digitalio.pyi should exist", stub.exists())
        assertTrue(stub.readText().contains("class DigitalInOut"))
        assertTrue(stub.readText().contains("class Direction"))
    }

    @Test
    fun `writes busio pyi stub for circuitpython stdlib`() {
        val base = tmp.newFolder("cp-busio")
        fakeSitePackages(base, "pymcu_circuitpython",
            moduleFiles = listOf("busio.py"),
            subPackages = mapOf("boards" to listOf("arduino_uno.py"))
        )

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "arduino_uno")

        val stub = base.resolve(".venv/lib/python3.14/site-packages/busio.pyi")
        assertTrue(stub.exists())
        assertTrue(stub.readText().contains("class UART"))
    }

    @Test
    fun `board pyi contains constants from board file`() {
        val base = tmp.newFolder("cp-board")
        val sp = fakeSitePackages(base, "pymcu_circuitpython",
            subPackages = mapOf("boards" to emptyList())
        )
        // Write a minimal board file with pin constants
        sp.resolve("pymcu_circuitpython/boards/arduino_uno.py").writeText(
            "LED = \"PB5\"\nLED_BUILTIN = \"PB5\"\nD13 = \"PB5\"\n"
        )

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "arduino_uno")

        val stub = base.resolve(".venv/lib/python3.14/site-packages/board.pyi")
        assertTrue("board.pyi should exist", stub.exists())
        val content = stub.readText()
        assertTrue("LED should be str", content.contains("LED: str"))
        assertTrue("LED_BUILTIN should be str", content.contains("LED_BUILTIN: str"))
        assertTrue("D13 should be str", content.contains("D13: str"))
    }

    @Test
    fun `board pyi uses specified board id`() {
        val base = tmp.newFolder("cp-board-nano")
        val sp = fakeSitePackages(base, "pymcu_circuitpython",
            subPackages = mapOf("boards" to emptyList())
        )
        sp.resolve("pymcu_circuitpython/boards/arduino_uno.py").writeText("LED = \"PB5\"\n")
        sp.resolve("pymcu_circuitpython/boards/arduino_nano.py").writeText("LED_NANO = \"PB5\"\n")

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "arduino_nano")

        val stub = base.resolve(".venv/lib/python3.14/site-packages/board.pyi")
        assertTrue(stub.readText().contains("LED_NANO: str"))
        assertFalse("should NOT contain arduino_uno constants", stub.readText().contains("LED: str\n"))
    }

    @Test
    fun `board pyi falls back to arduino_uno when board not found`() {
        val base = tmp.newFolder("cp-board-fallback")
        val sp = fakeSitePackages(base, "pymcu_circuitpython",
            subPackages = mapOf("boards" to emptyList())
        )
        sp.resolve("pymcu_circuitpython/boards/arduino_uno.py").writeText("LED_UNO = \"PB5\"\n")

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "nonexistent_board")

        val stub = base.resolve(".venv/lib/python3.14/site-packages/board.pyi")
        assertTrue(stub.exists())
        assertTrue(stub.readText().contains("LED_UNO: str"))
    }

    @Test
    fun `does NOT write py shims — only pyi stubs`() {
        val base = tmp.newFolder("cp-no-py-shims")
        fakeSitePackages(base, "pymcu_circuitpython",
            moduleFiles = listOf("digitalio.py", "busio.py"),
            subPackages = mapOf("boards" to listOf("arduino_uno.py"))
        )

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "arduino_uno")

        val sp = base.resolve(".venv/lib/python3.14/site-packages")
        assertFalse(".py shim must NOT exist", sp.resolve("digitalio.py").exists())
        assertFalse(".py shim must NOT exist", sp.resolve("busio.py").exists())
        assertTrue(".pyi stub must exist",     sp.resolve("digitalio.pyi").exists())
        assertTrue(".pyi stub must exist",     sp.resolve("busio.pyi").exists())
    }

    @Test
    fun `removes legacy py shims if present`() {
        val base = tmp.newFolder("cp-remove-legacy")
        val sp = fakeSitePackages(base, "pymcu_circuitpython",
            moduleFiles = listOf("digitalio.py"),
            subPackages = mapOf("boards" to listOf("arduino_uno.py"))
        )
        // Simulate old .py shims left from a previous plugin version
        sp.resolve("digitalio.py").writeText("from pymcu_circuitpython.digitalio import *\n")
        sp.resolve("board.py").writeText("from pymcu_circuitpython.boards.arduino_uno import *\n")

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "arduino_uno")

        assertFalse("legacy board.py should be removed",    sp.resolve("board.py").exists())
        assertFalse("legacy digitalio.py should be removed", sp.resolve("digitalio.py").exists())
    }

    @Test
    fun `does nothing when circuitpython package not installed`() {
        val base = tmp.newFolder("cp-nopkg")
        base.resolve(".venv/lib/python3.14/site-packages").mkdirs()

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "arduino_uno")

        assertFalse(base.resolve(".venv/lib/python3.14/site-packages/digitalio.pyi").exists())
    }

    // ── MicroPython: .pyi stubs written ──────────────────────────────────────

    @Test
    fun `writes machine pyi stub for micropython stdlib`() {
        val base = tmp.newFolder("mp-machine")
        fakeSitePackages(base, "pymcu_micropython",
            moduleFiles = listOf("machine.py", "utime.py", "micropython.py")
        )

        PyMcuStubInstaller.install(base.absolutePath, listOf("micropython"), null)

        val stub = base.resolve(".venv/lib/python3.14/site-packages/machine.pyi")
        assertTrue(stub.exists())
        assertTrue(stub.readText().contains("class Pin"))
        assertTrue(stub.readText().contains("class UART"))
        assertTrue(stub.readText().contains("class ADC"))
    }

    @Test
    fun `writes utime pyi stub for micropython stdlib`() {
        val base = tmp.newFolder("mp-utime")
        fakeSitePackages(base, "pymcu_micropython", moduleFiles = listOf("utime.py"))

        PyMcuStubInstaller.install(base.absolutePath, listOf("micropython"), null)

        val stub = base.resolve(".venv/lib/python3.14/site-packages/utime.pyi")
        assertTrue(stub.exists())
        assertTrue(stub.readText().contains("def sleep_ms"))
        assertTrue(stub.readText().contains("def ticks_ms"))
    }

    @Test
    fun `does NOT write py shims for micropython — only pyi`() {
        val base = tmp.newFolder("mp-no-py")
        fakeSitePackages(base, "pymcu_micropython", moduleFiles = listOf("machine.py", "utime.py"))

        PyMcuStubInstaller.install(base.absolutePath, listOf("micropython"), null)

        val sp = base.resolve(".venv/lib/python3.14/site-packages")
        assertFalse(sp.resolve("machine.py").exists())
        assertFalse(sp.resolve("utime.py").exists())
        assertTrue(sp.resolve("machine.pyi").exists())
        assertTrue(sp.resolve("utime.pyi").exists())
    }

    // ── site-packages discovery ───────────────────────────────────────────────

    @Test
    fun `findSitePackages returns null when no venv`() {
        val base = tmp.newFolder("no-venv")
        assertNull(PyMcuStubInstaller.findSitePackages(base.absolutePath))
    }

    @Test
    fun `findSitePackages finds venv directory as fallback`() {
        val base = tmp.newFolder("venv-fallback")
        val sp = base.resolve("venv/lib/python3.12/site-packages")
        sp.mkdirs()

        val found = PyMcuStubInstaller.findSitePackages(base.absolutePath)
        assertTrue(found != null)
        assertTrue(found!!.toFile().canonicalPath == sp.canonicalPath)
    }

    @Test
    fun `install is no-op when stdlib is empty`() {
        val base = tmp.newFolder("empty-stdlib")
        PyMcuStubInstaller.install(base.absolutePath, emptyList(), null)  // must not throw
    }
}
