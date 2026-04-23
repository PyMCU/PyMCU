package dev.begeistert.pymcu.resolver

import com.intellij.navigation.ItemPresentation
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.roots.AdditionalLibraryRootsProvider
import com.intellij.openapi.roots.SyntheticLibrary
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import dev.begeistert.pymcu.config.PyMCUConfigReader
import dev.begeistert.pymcu.stdlib.PyMCUStubInstaller
import java.nio.file.Path
import javax.swing.Icon

/**
 * Adds compat package directories to PyCharm's module resolution path for
 * PyMCU projects that use CircuitPython or MicroPython stdlib compat layers.
 *
 * The pymcu compiler resolves:
 *   import digitalio  →  pymcu_circuitpython/digitalio.py
 *   import machine    →  pymcu_micropython/machine.py
 *   import board      →  dist/_generated/board.py
 *
 * By adding the compat package directory as an extra library root, PyCharm
 * finds the same files the compiler does — no shim files needed.
 *
 * Registered via <additionalLibraryRootsProvider> in plugin.xml.
 */
class PyMCUAdditionalLibraryRootsProvider : AdditionalLibraryRootsProvider() {

    private val log = Logger.getInstance(PyMCUAdditionalLibraryRootsProvider::class.java)

    override fun getAdditionalProjectLibraries(project: Project): Collection<SyntheticLibrary> {
        val config   = PyMCUConfigReader.findConfig(project) ?: return emptyList()
        if (config.stdlib.isEmpty()) return emptyList()

        val basePath = project.basePath ?: return emptyList()
        val sp       = PyMCUStubInstaller.findSitePackages(basePath)
        val lfs      = LocalFileSystem.getInstance()
        val roots    = mutableListOf<VirtualFile>()

        if ("circuitpython" in config.stdlib && sp != null) {
            lfs.findFileByNioFile(sp.resolve("pymcu_circuitpython"))
                ?.also { log.debug("PyMCU: adding library root ${it.path}") }
                ?.let  { roots.add(it) }
        }

        if ("micropython" in config.stdlib && sp != null) {
            lfs.findFileByNioFile(sp.resolve("pymcu_micropython"))
                ?.also { log.debug("PyMCU: adding library root ${it.path}") }
                ?.let  { roots.add(it) }
        }

        // dist/_generated/ contains board.py (written by the build or proactively by startup)
        lfs.findFileByNioFile(Path.of(basePath, "dist", "_generated"))
            ?.also { log.debug("PyMCU: adding generated root ${it.path}") }
            ?.let  { roots.add(it) }

        if (roots.isEmpty()) return emptyList()
        return listOf(PyMCUCompatLibrary(roots))
    }

    override fun getRootsToWatch(project: Project): Collection<VirtualFile> {
        val config   = PyMCUConfigReader.findConfig(project) ?: return emptyList()
        val basePath = project.basePath ?: return emptyList()
        val sp       = PyMCUStubInstaller.findSitePackages(basePath) ?: return emptyList()
        val lfs      = LocalFileSystem.getInstance()

        return buildList {
            if ("circuitpython" in config.stdlib)
                lfs.findFileByNioFile(sp.resolve("pymcu_circuitpython"))?.let { add(it) }
            if ("micropython" in config.stdlib)
                lfs.findFileByNioFile(sp.resolve("pymcu_micropython"))?.let { add(it) }
        }
    }

}

private class PyMCUCompatLibrary(private val roots: List<VirtualFile>) :
    SyntheticLibrary(), ItemPresentation {

    override fun getSourceRoots(): Collection<VirtualFile> = roots

    override fun getPresentableText(): String = "PyMCU Compat"
    override fun getIcon(unused: Boolean): Icon? = null

    override fun equals(other: Any?): Boolean =
        other is PyMCUCompatLibrary && roots == other.roots

    override fun hashCode(): Int = roots.hashCode()
}
