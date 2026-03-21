package dev.begeistert.pymcu.startup

import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import dev.begeistert.pymcu.config.PyMcuConfigReader
import dev.begeistert.pymcu.settings.PyMcuSettings

/**
 * Runs once after a project is fully opened.
 * When a [tool.pymcu] section is detected in pyproject.toml, it performs a
 * background dependency sync using the configured package manager.
 */
class PyMcuStartupActivity : ProjectActivity {

    private val log = Logger.getInstance(PyMcuStartupActivity::class.java)

    override suspend fun execute(project: Project) {
        val config = PyMcuConfigReader.findConfig(project) ?: return
        log.info("PyMCU project detected (chip=${config.chip}), scheduling sync.")

        val basePath = project.basePath ?: return
        val settings = PyMcuSettings.getInstance()

        ApplicationManager.getApplication().executeOnPooledThread {
            runSync(project, basePath, settings.packageManager)
        }
    }

    private fun runSync(project: Project, basePath: String, packageManager: String) {
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
            } catch (_: Exception) {
                // Notification group may not exist in all environments
            }
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
            } catch (_: Exception) {
                // Notification group may not exist in all environments
            }
        }
    }
}
