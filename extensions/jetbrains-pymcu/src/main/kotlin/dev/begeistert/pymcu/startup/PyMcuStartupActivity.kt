package dev.begeistert.pymcu.startup

import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import dev.begeistert.pymcu.config.PyMcuConfigReader
import dev.begeistert.pymcu.settings.PyMcuSettings
import dev.begeistert.pymcu.stdlib.PyMcuStubInstaller

/**
 * Runs once after a project is fully opened.
 *
 * When a [tool.pymcu] section is detected in pyproject.toml it:
 *  1. Immediately installs compat shims (board.py, digitalio.py, machine.py…)
 *     into .venv/site-packages so PyCharm resolves CircuitPython/MicroPython imports.
 *  2. Runs a background dependency sync (uv sync / pip install / …).
 *  3. Reinstalls shims after sync in case the .venv was freshly created.
 */
class PyMcuStartupActivity : ProjectActivity {

    private val log = Logger.getInstance(PyMcuStartupActivity::class.java)

    override suspend fun execute(project: Project) {
        val config = PyMcuConfigReader.findConfig(project) ?: return
        val basePath = project.basePath ?: return

        log.info("PyMCU project detected (${config.displayName}), starting setup.")

        // Install shims immediately — covers projects whose .venv already exists
        if (config.stdlib.isNotEmpty()) {
            PyMcuStubInstaller.install(basePath, config.stdlib, config.board)
        }

        val settings = PyMcuSettings.getInstance()
        ApplicationManager.getApplication().executeOnPooledThread {
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
                // Reinstall shims — .venv may have just been created by this sync
                if (stdlib.isNotEmpty()) {
                    PyMcuStubInstaller.install(basePath, stdlib, board)
                }
                notifySuccess(project)
            } else {
                log.warn("PyMCU sync failed (exit $exitCode):\n$output")
                notifyFailure(project, exitCode)
            }
        } catch (e: Exception) {
            log.error("PyMCU sync error", e)
            notifyFailure(project, -1)
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
