package dev.begeistert.pymcu

import dev.begeistert.pymcu.stdlib.PyMcuStubInstaller
import org.junit.Assert.assertEquals
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

    // ── helpers ──────────────────────────────────────────────────────────────

    /**
     * Creates a fake .venv layout under [base] with the given [packageName] and
     * [moduleFiles] inside it, then returns the site-packages directory.
     */
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

    // ── CircuitPython ─────────────────────────────────────────────────────────

    @Test
    fun `installs digitalio shim for circuitpython stdlib`() {
        val base = tmp.newFolder("project-cp")
        fakeSitePackages(base, "pymcu_circuitpython",
            moduleFiles = listOf("digitalio.py", "busio.py"),
            subPackages = mapOf("boards" to listOf("arduino_uno.py"))
        )

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "arduino_uno")

        val shim = base.resolve(".venv/lib/python3.14/site-packages/digitalio.py")
        assertTrue("digitalio.py shim should exist", shim.exists())
        assertTrue(shim.readText().contains("from pymcu_circuitpython.digitalio import *"))
    }

    @Test
    fun `installs busio shim for circuitpython stdlib`() {
        val base = tmp.newFolder("project-cp-busio")
        fakeSitePackages(base, "pymcu_circuitpython",
            moduleFiles = listOf("busio.py"),
            subPackages = mapOf("boards" to listOf("arduino_uno.py"))
        )

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "arduino_uno")

        val shim = base.resolve(".venv/lib/python3.14/site-packages/busio.py")
        assertTrue(shim.exists())
        assertTrue(shim.readText().contains("from pymcu_circuitpython.busio import *"))
    }

    @Test
    fun `installs board shim pointing to correct board module`() {
        val base = tmp.newFolder("project-board")
        fakeSitePackages(base, "pymcu_circuitpython",
            moduleFiles = listOf("digitalio.py"),
            subPackages = mapOf("boards" to listOf("arduino_uno.py", "arduino_nano.py"))
        )

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "arduino_nano")

        val shim = base.resolve(".venv/lib/python3.14/site-packages/board.py")
        assertTrue("board.py shim should exist", shim.exists())
        assertTrue(shim.readText().contains("pymcu_circuitpython.boards.arduino_nano"))
    }

    @Test
    fun `board shim falls back to arduino_uno when board not found`() {
        val base = tmp.newFolder("project-board-fallback")
        fakeSitePackages(base, "pymcu_circuitpython",
            subPackages = mapOf("boards" to listOf("arduino_uno.py"))
        )

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "unknown_board")

        val shim = base.resolve(".venv/lib/python3.14/site-packages/board.py")
        assertTrue(shim.exists())
        assertTrue(shim.readText().contains("arduino_uno"))
    }

    @Test
    fun `does not install shim when package module file is absent`() {
        val base = tmp.newFolder("project-cp-missing")
        // analogio.py is NOT in the package
        fakeSitePackages(base, "pymcu_circuitpython",
            moduleFiles = listOf("digitalio.py"),
            subPackages = mapOf("boards" to listOf("arduino_uno.py"))
        )

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "arduino_uno")

        val missing = base.resolve(".venv/lib/python3.14/site-packages/analogio.py")
        assertFalse("analogio.py shim should NOT be created", missing.exists())
    }

    @Test
    fun `does nothing when circuitpython package not installed`() {
        val base = tmp.newFolder("project-cp-nopkg")
        // site-packages exists but pymcu_circuitpython is absent
        base.resolve(".venv/lib/python3.14/site-packages").mkdirs()

        PyMcuStubInstaller.install(base.absolutePath, listOf("circuitpython"), "arduino_uno")

        val shim = base.resolve(".venv/lib/python3.14/site-packages/digitalio.py")
        assertFalse(shim.exists())
    }

    // ── MicroPython ───────────────────────────────────────────────────────────

    @Test
    fun `installs machine shim for micropython stdlib`() {
        val base = tmp.newFolder("project-mp")
        fakeSitePackages(base, "pymcu_micropython",
            moduleFiles = listOf("machine.py", "utime.py", "micropython.py")
        )

        PyMcuStubInstaller.install(base.absolutePath, listOf("micropython"), null)

        val shim = base.resolve(".venv/lib/python3.14/site-packages/machine.py")
        assertTrue(shim.exists())
        assertEquals("from pymcu_micropython.machine import *\n", shim.readText())
    }

    @Test
    fun `installs utime shim for micropython stdlib`() {
        val base = tmp.newFolder("project-mp-utime")
        fakeSitePackages(base, "pymcu_micropython",
            moduleFiles = listOf("utime.py")
        )

        PyMcuStubInstaller.install(base.absolutePath, listOf("micropython"), null)

        val shim = base.resolve(".venv/lib/python3.14/site-packages/utime.py")
        assertTrue(shim.exists())
        assertTrue(shim.readText().contains("from pymcu_micropython.utime import *"))
    }

    // ── site-packages discovery ───────────────────────────────────────────────

    @Test
    fun `findSitePackages returns null when no venv`() {
        val base = tmp.newFolder("no-venv")
        assertNull(PyMcuStubInstaller.findSitePackages(base.absolutePath))
    }

    @Test
    fun `findSitePackages finds venv directory as fallback`() {
        val base = tmp.newFolder("project-venv")
        val sp = base.resolve("venv/lib/python3.12/site-packages")
        sp.mkdirs()

        val found = PyMcuStubInstaller.findSitePackages(base.absolutePath)
        assertEquals(sp.canonicalPath, found?.toFile()?.canonicalPath)
    }

    @Test
    fun `install is no-op when stdlib is empty`() {
        val base = tmp.newFolder("project-nostdlib")
        // No .venv at all — should not throw
        PyMcuStubInstaller.install(base.absolutePath, emptyList(), null)
    }
}
