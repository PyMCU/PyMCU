package dev.begeistert.pymcu.startup

import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.project.ProjectManagerListener
import com.intellij.openapi.roots.ex.ProjectRootManagerEx
import com.intellij.openapi.vfs.LocalFileSystem
import dev.begeistert.pymcu.config.PyMcuConfigReader
import dev.begeistert.pymcu.settings.PyMcuSettings
import dev.begeistert.pymcu.stdlib.PyMcuStubInstaller

/**
 * Reacts to project-open events via ProjectManagerListener, which fires reliably
 * in PyCharm Community regardless of plugin dependency ordering.
 *
 * Neither ProjectActivity (coroutine-based) nor StartupActivity.DumbAware fire
 * reliably in PyCharm Community 2024.3 when com.intellij.modules.python is a
 * plugin dependency — the message-bus listener approach is the safe alternative.
 *
 * When a [tool.pymcu] section is detected in pyproject.toml it:
 *  1. Dispatches to a pooled thread; installs compat stubs and board.py into
 *     .venv/site-packages / dist/_generated/ so PyCharm resolves imports.
 *  2. Runs a background dependency sync (uv sync / pip install / …).
 *  3. Reinstalls stubs after sync in case the .venv was freshly created.
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

        val settings = PyMcuSettings.getInstance()
        ApplicationManager.getApplication().executeOnPooledThread {
            // Install stubs for a pre-existing .venv, then sync to ensure the
            // venv is up-to-date, then reinstall stubs regardless of sync result.
            if (config.stdlib.isNotEmpty()) {
                val sp = PyMcuStubInstaller.install(basePath, config.stdlib, config.board)
                refreshAndNotify(project, basePath, sp)
            }
            runSync(project, basePath, settings.packageManager, config.stdlib, config.board)
        }
    }

    private fun runSync(
        project: Project,
        basePath: String,
        packageManager: String,
        stdlib: List<String>,
        board: String?
    ) {
        val command: List<String> = when (packageManager) {
            "uv"     -> listOf("uv", "sync")
            "poetry" -> listOf("poetry", "install")
            "pipenv" -> listOf("pipenv", "install")
            "pip"    -> listOf("pip", "install", "-e", ".")
            else     -> listOf("uv", "sync")
        }

        log.info("PyMCU sync: running ${command.joinToString(" ")} in $basePath")

        try {
            val process = ProcessBuilder(command)
                .directory(java.io.File(basePath))
                .redirectErrorStream(true)
                .start()

            val output = process.inputStream.bufferedReader().readText()
            val exitCode = process.waitFor()

            if (exitCode == 0) {
                log.info("PyMCU sync succeeded.")
                notifySuccess(project)
            } else {
                log.warn("PyMCU sync failed (exit $exitCode):\n$output")
                notifyFailure(project, exitCode)
            }

            // Reinstall stubs regardless of sync result — if .venv was already
            // populated (e.g. by a previous manual sync or a private registry that
            // the plugin's subprocess can't authenticate to), we still want the IDE
            // stubs and dist/_generated/board.py to be in place.
            if (stdlib.isNotEmpty()) {
                val sp = PyMcuStubInstaller.install(basePath, stdlib, board)
                refreshAndNotify(project, basePath, sp)
            }
        } catch (e: Exception) {
            log.error("PyMCU sync error", e)
            notifyFailure(project, -1)
        }
    }

    /**
     * Two-step VFS + roots notification — called from a background thread after
     * stubs/board.py are written to disk.
     *
     * **Why two steps are needed:**
     * `AdditionalLibraryRootsProvider.getAdditionalProjectLibraries()` is always
     * called under a read action. Inside a read action, `refreshAndFindFileByNioFile`
     * cannot be called (it acquires a VFS write lock, which deadlocks under a read
     * lock). So the provider uses `findFileByNioFile`, which returns `null` if the
     * VirtualFile is not already in the VFS cache.
     *
     * Step 1 — pre-populate VFS:
     *   `refreshAndFindFileByNioFile` is safe on background threads. It forces the
     *   VFS to create VirtualFile entries for `pymcu_circuitpython/`,
     *   `pymcu_micropython/`, and `dist/_generated/`, so subsequent
     *   `findFileByNioFile` calls (inside the read-action provider) return non-null.
     *
     * Step 2 — fire roots-changed event:
     *   `ProjectRootManagerEx.makeRootsChange()` fires `PROJECT_ROOTS_CHANGED`,
     *   which invalidates the cached additional library roots and causes IntelliJ
     *   to re-call `getAdditionalProjectLibraries()`. This second call succeeds
     *   because VFS is now populated (step 1 already ran).
     */
    private fun refreshAndNotify(
        project: Project,
        basePath: String,
        sitePackages: java.nio.file.Path?
    ) {
        // Step 1: pre-populate VFS (safe from background thread, outside read action)
        val lfs = LocalFileSystem.getInstance()
        sitePackages?.let { sp ->
            lfs.refreshAndFindFileByNioFile(sp.resolve("pymcu_circuitpython"))
            lfs.refreshAndFindFileByNioFile(sp.resolve("pymcu_micropython"))
        }
        lfs.refreshAndFindFileByNioFile(java.nio.file.Path.of(basePath, "dist", "_generated"))

        // Also refresh site-packages so new .pyi stubs are indexed by PyCharm's
        // Python analysis engine (handles the case where the SDK is correctly pointed
        // at this .venv and PyCharm indexes site-packages as SDK library roots).
        sitePackages?.let { sp ->
            lfs.refreshNioFiles(
                listOf(sp),
                /* async = */ true,
                /* recursive = */ false,
                /* postRunnable = */ null
            )
        }

        // Step 2: re-trigger AdditionalLibraryRootsProvider evaluation on EDT
        ApplicationManager.getApplication().invokeLater {
            if (!project.isDisposed) {
                ApplicationManager.getApplication().runWriteAction {
                    ProjectRootManagerEx.getInstanceEx(project)
                        .makeRootsChange(Runnable { /* no-op */ }, /* isFakeChange = */ false, /* fireWorkerThreads = */ false)
                }
            }
        }
    }

    private fun notifySuccess(project: Project) {
        ApplicationManager.getApplication().invokeLater {
            try {
                NotificationGroupManager.getInstance()
                    .getNotificationGroup("PyMCU")
                    ?.createNotification(
                        "PyMCU Sync",
                        "Project synced successfully.",
                        NotificationType.INFORMATION
                    )
                    ?.notify(project)
            } catch (_: Exception) { }
        }
    }

    private fun notifyFailure(project: Project, exitCode: Int) {
        ApplicationManager.getApplication().invokeLater {
            try {
                NotificationGroupManager.getInstance()
                    .getNotificationGroup("PyMCU")
                    ?.createNotification(
                        "PyMCU Sync",
                        "Project sync failed (exit code $exitCode).",
                        NotificationType.WARNING
                    )
                    ?.notify(project)
            } catch (_: Exception) { }
        }
    }
}
