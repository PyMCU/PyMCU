package dev.begeistert.pymcu.toolwindow

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.openapi.vfs.newvfs.BulkFileListener
import com.intellij.openapi.vfs.newvfs.events.VFileEvent
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.ui.content.ContentFactory
import dev.begeistert.pymcu.config.PyMcuConfigReader
import dev.begeistert.pymcu.settings.PyMcuSettings
import java.awt.BorderLayout
import java.awt.Dimension
import javax.swing.Box
import javax.swing.BoxLayout
import javax.swing.JButton
import javax.swing.JLabel
import javax.swing.JPanel
import javax.swing.JScrollPane
import javax.swing.JTextArea
import javax.swing.SwingUtilities

/**
 * Factory for the "PyMCU" tool window (anchor=bottom).
 *
 * Panel layout (BoxLayout vertical):
 *   - Chip info label
 *   - Build / Flash / Clean / Sync buttons
 *   - Scrollable output text area
 */
class PyMcuToolWindowFactory : ToolWindowFactory {

    override fun createToolWindowContent(project: Project, toolWindow: ToolWindow) {
        val panel = PyMcuToolWindowPanel(project)
        val content = ContentFactory.getInstance()
            .createContent(panel, "", false)
        toolWindow.contentManager.addContent(content)
    }

    override fun shouldBeAvailable(project: Project): Boolean = true
}

private class PyMcuToolWindowPanel(private val project: Project) : JPanel(BorderLayout()) {

    private val log = Logger.getInstance(PyMcuToolWindowPanel::class.java)

    private val chipLabel = JLabel("No PyMCU project detected")
    private val outputArea = JTextArea(8, 60).apply {
        isEditable = false
        font = java.awt.Font(java.awt.Font.MONOSPACED, java.awt.Font.PLAIN, 12)
    }

    init {
        val topPanel = JPanel().apply {
            layout = BoxLayout(this, BoxLayout.Y_AXIS)
            add(chipLabel)
            add(Box.createVerticalStrut(6))
            add(buildButtonRow())
            add(Box.createVerticalStrut(4))
        }

        add(topPanel, BorderLayout.NORTH)
        add(JScrollPane(outputArea), BorderLayout.CENTER)

        refreshChipLabel()
        registerVfsListener()
    }

    private fun buildButtonRow(): JPanel {
        val row = JPanel().apply {
            layout = BoxLayout(this, BoxLayout.X_AXIS)
        }

        listOf("Build" to "build", "Flash" to "flash", "Clean" to "clean").forEach { (label, cmd) ->
            val btn = JButton(label).apply {
                maximumSize = Dimension(80, 28)
                addActionListener { runCommand(cmd) }
            }
            row.add(btn)
            row.add(Box.createHorizontalStrut(4))
        }

        val syncBtn = JButton("Sync Project").apply {
            maximumSize = Dimension(110, 28)
            addActionListener { runSync() }
        }
        row.add(syncBtn)
        return row
    }

    private fun refreshChipLabel() {
        val config = PyMcuConfigReader.findConfig(project)
        if (config == null) {
            chipLabel.text = "No PyMCU project detected"
            return
        }
        val sb = StringBuilder(if (config.board != null) "Board: " else "Chip: ")
        sb.append(config.displayName)
        if (config.frequency != null) sb.append(" @ ${config.frequency} Hz")
        if (config.stdlib.isNotEmpty()) sb.append(" · ${config.stdlib.joinToString(", ")}")
        if (config.hasFfi) sb.append(" · C/C++ FFI")
        chipLabel.text = sb.toString()
    }

    private fun registerVfsListener() {
        project.messageBus.connect().subscribe(
            VirtualFileManager.VFS_CHANGES,
            object : BulkFileListener {
                override fun after(events: List<VFileEvent>) {
                    val touchedPyproject = events.any {
                        it.file?.name == "pyproject.toml"
                    }
                    if (touchedPyproject) {
                        SwingUtilities.invokeLater { refreshChipLabel() }
                    }
                }
            }
        )
    }

    private fun runCommand(command: String) {
        val settings = PyMcuSettings.getInstance()
        val basePath = project.basePath ?: run {
            appendOutput("Error: cannot determine project base directory.\n")
            return
        }

        appendOutput("$ ${settings.executablePath} $command\n")

        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                val process = ProcessBuilder(settings.executablePath, command)
                    .directory(java.io.File(basePath))
                    .redirectErrorStream(true)
                    .start()

                val output = process.inputStream.bufferedReader().readText()
                process.waitFor()

                SwingUtilities.invokeLater { appendOutput(output) }
            } catch (e: Exception) {
                SwingUtilities.invokeLater {
                    appendOutput("Error running command: ${e.message}\n")
                }
                log.error("PyMCU toolwindow command error", e)
            }
        }
    }

    private fun runSync() {
        val settings = PyMcuSettings.getInstance()
        val basePath = project.basePath ?: run {
            appendOutput("Error: cannot determine project base directory.\n")
            return
        }

        val command: List<String> = when (settings.packageManager) {
            "uv"     -> listOf("uv", "sync")
            "poetry" -> listOf("poetry", "install")
            "pipenv" -> listOf("pipenv", "install")
            "pip"    -> listOf("pip", "install", "-e", ".")
            else     -> listOf("uv", "sync")
        }

        appendOutput("$ ${command.joinToString(" ")}\n")

        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                val process = ProcessBuilder(command)
                    .directory(java.io.File(basePath))
                    .redirectErrorStream(true)
                    .start()

                val output = process.inputStream.bufferedReader().readText()
                process.waitFor()

                SwingUtilities.invokeLater { appendOutput(output) }
            } catch (e: Exception) {
                SwingUtilities.invokeLater {
                    appendOutput("Error during sync: ${e.message}\n")
                }
                log.error("PyMCU toolwindow sync error", e)
            }
        }
    }

    private fun appendOutput(text: String) {
        outputArea.append(text)
        outputArea.caretPosition = outputArea.document.length
    }
}
