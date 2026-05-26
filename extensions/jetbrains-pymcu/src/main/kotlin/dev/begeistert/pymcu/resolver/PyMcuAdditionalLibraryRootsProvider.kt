package dev.begeistert.pymcu.resolver

import com.intellij.navigation.ItemPresentation
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.roots.AdditionalLibraryRootsProvider
import com.intellij.openapi.roots.SyntheticLibrary
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import dev.begeistert.pymcu.config.PyMcuConfigReader
import java.io.File
import java.nio.file.Path
import javax.swing.Icon

/**
 * Resolves PyMCU library roots for PyCharm's import analysis.
 *
 * We inject the REAL installed source packages from .venv — not generated stubs.
 * This gives full traceability: Ctrl+Click navigates to actual HAL source,
 * docstrings are visible, decorators (@inline, @property) are auditable.
 *
 * Roots added (when present):
 *   .venv/.../site-packages/pymcu/           — core HAL (gpio, uart, spi, etc.)
 *   .venv/.../site-packages/pymcu_micropython/  — if stdlib=["micropython"]
 *   .venv/.../site-packages/pymcu_circuitpython/ — if stdlib=["circuitpython"]
 *   dist/_generated/                         — board.py (generated from pyproject.toml)
 *
 * When .venv is absent (project not yet synced), only dist/_generated/ is added.
 * The user is expected to run `pymcu sync` to populate the venv.
 */
class PyMcuAdditionalLibraryRootsProvider : AdditionalLibraryRootsProvider() {

    private val log = Logger.getInstance(PyMcuAdditionalLibraryRootsProvider::class.java)

    override fun getAdditionalProjectLibraries(project: Project): Collection<SyntheticLibrary> {
        val config   = PyMcuConfigReader.findConfig(project) ?: return emptyList()
        val basePath = project.basePath ?: return emptyList()
        val lfs      = LocalFileSystem.getInstance()
        val roots    = mutableListOf<VirtualFile>()

        val sp = findSitePackages(basePath)

        // Always add pymcu core HAL for import navigation (gpio, uart, etc.)
        if (sp != null) {
            resolvePackageDir(sp, "pymcu")?.let { addIfExists(lfs, it, roots, "pymcu core") }
        }

        // Compat layers — real installed source packages
        if ("micropython" in config.stdlib && sp != null) {
            resolvePackageDir(sp, "pymcu_micropython")?.let { addIfExists(lfs, it, roots, "pymcu_micropython") }
        }

        if ("circuitpython" in config.stdlib && sp != null) {
            resolvePackageDir(sp, "pymcu_circuitpython")?.let { addIfExists(lfs, it, roots, "pymcu_circuitpython") }
        }

        // Generated board.py — always try this regardless of venv state
        addIfExists(lfs, Path.of(basePath, "dist", "_generated"), roots, "dist/_generated")

        if (roots.isEmpty()) return emptyList()
        return listOf(PyMcuSourceLibrary(roots))
    }

    override fun getRootsToWatch(project: Project): Collection<VirtualFile> {
        val basePath = project.basePath ?: return emptyList()
        val sp = findSitePackages(basePath) ?: return emptyList()
        val lfs = LocalFileSystem.getInstance()
        return buildList {
            for (pkg in listOf("pymcu", "pymcu_micropython", "pymcu_circuitpython")) {
                resolvePackageDir(sp, pkg)?.let { lfs.findFileByNioFile(it)?.let { vf -> add(vf) } }
            }
        }
    }

    /**
     * Resolves the actual on-disk directory for a Python package.
     *
     * For regular installs the package lives directly in site-packages.
     * For editable (path=…) installs, hatchling writes a
     * `_editable_impl_<pkg>.pth` file whose content is the source root;
     * the actual package directory is `<pth_content>/<pkg>/`.
     */
    private fun resolvePackageDir(sp: Path, packageName: String): Path? {
        val direct = sp.resolve(packageName)
        if (direct.toFile().isDirectory) return direct

        val pthFile = sp.resolve("_editable_impl_${packageName}.pth").toFile()
        if (pthFile.exists()) {
            val srcRoot = Path.of(pthFile.readText().trim())
            val candidate = srcRoot.resolve(packageName)
            if (candidate.toFile().isDirectory) return candidate
        }
        return null
    }

    private fun addIfExists(
        lfs: LocalFileSystem, path: Path,
        roots: MutableList<VirtualFile>, label: String
    ) {
        lfs.findFileByNioFile(path)?.let {
            log.debug("PyMCU resolver: adding $label → ${it.path}")
            roots.add(it)
        }
    }
}

/**
 * Locate the site-packages directory inside the first .venv or venv found.
 * Handles any Python version (python3.12, python3.14, etc.).
 */
internal fun findSitePackages(basePath: String): Path? {
    for (venvName in listOf(".venv", "venv")) {
        val libDir = File(basePath).resolve("$venvName/lib")
        if (!libDir.isDirectory) continue
        val pythonDir = libDir.listFiles()?.firstOrNull { it.name.startsWith("python") } ?: continue
        val sp = pythonDir.resolve("site-packages")
        if (sp.isDirectory) return sp.toPath()
    }
    return null
}

private class PyMcuSourceLibrary(private val roots: List<VirtualFile>) :
    SyntheticLibrary(), ItemPresentation {

    override fun getSourceRoots(): Collection<VirtualFile> = roots

    override fun getPresentableText(): String = "PyMCU Sources"
    override fun getIcon(unused: Boolean): Icon? = null

    override fun equals(other: Any?): Boolean =
        other is PyMcuSourceLibrary && roots == other.roots

    override fun hashCode(): Int = roots.hashCode()
}
