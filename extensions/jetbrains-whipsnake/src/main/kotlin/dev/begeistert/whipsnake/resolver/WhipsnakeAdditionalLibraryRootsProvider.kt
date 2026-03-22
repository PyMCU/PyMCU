package dev.begeistert.whipsnake.resolver

import com.intellij.navigation.ItemPresentation
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.roots.AdditionalLibraryRootsProvider
import com.intellij.openapi.roots.SyntheticLibrary
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import dev.begeistert.whipsnake.config.WhipsnakeConfigReader
import dev.begeistert.whipsnake.stdlib.WhipsnakeStubInstaller
import java.nio.file.Path
import javax.swing.Icon

/**
 * Adds compat package directories to PyCharm's module resolution path for
 * Whipsnake projects that use CircuitPython or MicroPython stdlib compat layers.
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
class WhipsnakeAdditionalLibraryRootsProvider : AdditionalLibraryRootsProvider() {

    private val log = Logger.getInstance(WhipsnakeAdditionalLibraryRootsProvider::class.java)

    override fun getAdditionalProjectLibraries(project: Project): Collection<SyntheticLibrary> {
        val config   = WhipsnakeConfigReader.findConfig(project) ?: return emptyList()
        if (config.stdlib.isEmpty()) return emptyList()

        val basePath = project.basePath ?: return emptyList()
        val sp       = WhipsnakeStubInstaller.findSitePackages(basePath)
        val lfs      = LocalFileSystem.getInstance()
        val roots    = mutableListOf<VirtualFile>()

        if ("circuitpython" in config.stdlib && sp != null) {
            lfs.findFileByNioFile(sp.resolve("whipsnake_circuitpython"))
                ?.also { log.debug("Whipsnake: adding library root ${it.path}") }
                ?.let  { roots.add(it) }
        }

        if ("micropython" in config.stdlib && sp != null) {
            lfs.findFileByNioFile(sp.resolve("whipsnake_micropython"))
                ?.also { log.debug("Whipsnake: adding library root ${it.path}") }
                ?.let  { roots.add(it) }
        }

        // dist/_generated/ contains board.py (written by the build or proactively by startup)
        lfs.findFileByNioFile(Path.of(basePath, "dist", "_generated"))
            ?.also { log.debug("Whipsnake: adding generated root ${it.path}") }
            ?.let  { roots.add(it) }

        if (roots.isEmpty()) return emptyList()
        return listOf(WhipsnakeCompatLibrary(roots))
    }

    override fun getRootsToWatch(project: Project): Collection<VirtualFile> {
        val config   = WhipsnakeConfigReader.findConfig(project) ?: return emptyList()
        val basePath = project.basePath ?: return emptyList()
        val sp       = WhipsnakeStubInstaller.findSitePackages(basePath) ?: return emptyList()
        val lfs      = LocalFileSystem.getInstance()

        return buildList {
            if ("circuitpython" in config.stdlib)
                lfs.findFileByNioFile(sp.resolve("whipsnake_circuitpython"))?.let { add(it) }
            if ("micropython" in config.stdlib)
                lfs.findFileByNioFile(sp.resolve("whipsnake_micropython"))?.let { add(it) }
        }
    }

}

private class WhipsnakeCompatLibrary(private val roots: List<VirtualFile>) :
    SyntheticLibrary(), ItemPresentation {

    override fun getSourceRoots(): Collection<VirtualFile> = roots

    override fun getPresentableText(): String = "Whipsnake Compat"
    override fun getIcon(unused: Boolean): Icon? = null

    override fun equals(other: Any?): Boolean =
        other is WhipsnakeCompatLibrary && roots == other.roots

    override fun hashCode(): Int = roots.hashCode()
}
