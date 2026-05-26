package dev.begeistert.pymcu.startup

import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.project.ProjectManagerListener
import com.intellij.openapi.roots.ex.ProjectRootManagerEx
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.openapi.vfs.newvfs.BulkFileListener
import com.intellij.openapi.vfs.newvfs.events.VFileEvent
import dev.begeistert.pymcu.config.PyMcuConfigReader
import dev.begeistert.pymcu.settings.PyMcuSettings
import dev.begeistert.pymcu.stdlib.PyMcuStubInstaller
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Reacts to project-open events via ProjectManagerListener, which fires reliably
 * in PyCharm Community regardless of plugin dependency ordering.
 *
 * On project open:
 *  1. Installs compat stubs / board.py and refreshes library roots.
 *
 * When pyproject.toml is saved:
 *  2. Runs the configured package manager (uv sync / pip install / …)
 *     so new dependencies declared in the file are installed immediately.
 *  3. Reinstalls stubs and refreshes library roots afterwards.
 *
 * The sync is intentionally NOT run on every project open — it destroys
 * editable / local installs that are not in uv.lock.
 */
@Suppress("UnstableApiUsage")
class PyMcuStartupActivity : ProjectManagerListener {

    private val log = Logger.getInstance(PyMcuStartupActivity::class.java)

    override fun projectOpened(project: Project) {
        log.info("PyMCU: projectOpened fired for ${project.name}")

        val config = PyMcuConfigReader.findConfig(project) ?: run {
            log.info("PyMCU: no [tool.pymcu] config found, skipping.")
            return
        }
        val basePath = project.basePath ?: return

        log.info("PyMCU project detected (${config.displayName}), starting setup.")

        // ── Initial stub install (no sync) ────────────────────────────────────
        ApplicationManager.getApplication().executeOnPooledThread {
            if (config.stdlib.isNotEmpty()) {
                val sp = PyMcuStubInstaller.install(basePath, config.stdlib, config.board)
                refreshAndNotify(project, basePath, sp)
            } else {
                refreshAndNotify(project, basePath, null)
            }
        }

        // ── Watch pyproject.toml for changes ──────────────────────────────────
        val syncing = AtomicBoolean(false)
        project.messageBus.connect().subscribe(
            VirtualFileManager.VFS_CHANGES,
            object : BulkFileListener {
                override fun after(events: List<VFileEvent>) {
                    val tomlChanged = events.any { event ->
                        event.file?.name == "pyproject.toml" &&
                        event.file?.path?.startsWith(basePath) == true
                    }
                    if (!tomlChanged) return

                    // Guard against concurrent sync runs triggered by rapid saves.
                    if (!syncing.compareAndSet(false, true)) return

                    log.info("PyMCU: pyproject.toml changed — running sync")
                    ApplicationManager.getApplication().executeOnPooledThread {
                        try {
                            val settings = PyMcuSettings.getInstance()
                            runSync(project, basePath, settings.packageManager)

                            // Re-read config after sync (dependencies may have changed).
                            val newConfig = PyMcuConfigReader.findConfig(project)
                            if (newConfig != null && newConfig.stdlib.isNotEmpty()) {
                                val sp = PyMcuStubInstaller.install(basePath, newConfig.stdlib, newConfig.board)
                                refreshAndNotify(project, basePath, sp)
                            } else {
                                refreshAndNotify(project, basePath, null)
                            }
                        } finally {
                            syncing.set(false)
                        }
                    }
                }
            }
        )
    }

    // ── Package manager sync ──────────────────────────────────────────────────

    private fun runSync(project: Project, basePath: String, packageManager: String) {
        val command: List<String> = when (packageManager) {
            "uv"     -> listOf("uv", "sync")
            "poetry" -> listOf("poetry", "install")
            "pipenv" -> listOf("pipenv", "install")
            "pip"    -> listOf("pip", "install", "-e", ".")
            else     -> listOf("uv", "sync")
        }
        log.info("PyMCU sync: ${command.joinToString(" ")} in $basePath")
        try {
            val process = ProcessBuilder(command)
                .directory(java.io.File(basePath))
                .redirectErrorStream(true)
                .start()
            val output   = process.inputStream.bufferedReader().readText()
            val exitCode = process.waitFor()
            if (exitCode == 0) {
                log.info("PyMCU sync succeeded.")
                notify(project, "PyMCU", "Dependencies synced successfully.", NotificationType.INFORMATION)
            } else {
                log.warn("PyMCU sync failed (exit $exitCode):\n$output")
                notify(project, "PyMCU", "Dependency sync failed (exit $exitCode).", NotificationType.WARNING)
            }
        } catch (e: Exception) {
            log.error("PyMCU sync error", e)
            notify(project, "PyMCU", "Dependency sync error: ${e.message}", NotificationType.WARNING)
        }
    }

    // ── VFS + roots refresh ───────────────────────────────────────────────────

    /**
     * Two-step VFS + roots notification — called from a background thread after
     * stubs/board.py are written to disk.
     *
     * Step 1 — pre-populate VFS:
     *   `refreshAndFindFileByNioFile` forces VirtualFile entries into the VFS cache
     *   so `findFileByNioFile` (called inside the read-action provider) returns non-null.
     *
     * Step 2 — fire roots-changed event:
     *   `ProjectRootManagerEx.makeRootsChange()` invalidates cached additional library
     *   roots and causes IntelliJ to re-call `getAdditionalProjectLibraries()`.
     */
    private fun refreshAndNotify(
        project: Project,
        basePath: String,
        sitePackages: java.nio.file.Path?
    ) {
        val lfs = LocalFileSystem.getInstance()
        sitePackages?.let { sp ->
            lfs.refreshAndFindFileByNioFile(sp.resolve("pymcu"))
            lfs.refreshAndFindFileByNioFile(sp.resolve("pymcu_circuitpython"))
            lfs.refreshAndFindFileByNioFile(sp.resolve("pymcu_micropython"))
            lfs.refreshNioFiles(listOf(sp), true, false, null)
        }
        lfs.refreshAndFindFileByNioFile(java.nio.file.Path.of(basePath, "dist", "_generated"))

        ApplicationManager.getApplication().invokeLater {
            if (!project.isDisposed) {
                ApplicationManager.getApplication().runWriteAction {
                    ProjectRootManagerEx.getInstanceEx(project)
                        .makeRootsChange(Runnable { }, false, false)
                }
            }
        }
    }

    // ── Notification helper ───────────────────────────────────────────────────

    private fun notify(project: Project, title: String, message: String, type: NotificationType) {
        ApplicationManager.getApplication().invokeLater {
            if (project.isDisposed) return@invokeLater
            try {
                NotificationGroupManager.getInstance()
                    .getNotificationGroup("PyMCU")
                    ?.createNotification(title, message, type)
                    ?.notify(project)
            } catch (_: Exception) { }
        }
    }
}
