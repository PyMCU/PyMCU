package dev.begeistert.pymcu.toolwindow

import com.intellij.icons.AllIcons
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.openapi.vfs.newvfs.BulkFileListener
import com.intellij.openapi.vfs.newvfs.events.VFileEvent
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.ui.ColoredTreeCellRenderer
import com.intellij.ui.SimpleTextAttributes
import com.intellij.ui.content.ContentFactory
import com.intellij.util.ui.JBUI
import dev.begeistert.pymcu.config.PyMcuConfigReader
import dev.begeistert.pymcu.settings.PyMcuSettings
import java.awt.BorderLayout
import java.awt.Font
import javax.swing.*
import javax.swing.tree.DefaultMutableTreeNode
import javax.swing.tree.DefaultTreeModel
import javax.swing.tree.TreeSelectionModel

// ── Data model ──────────────────────────────────────────────────────────────

private sealed class TaskNode {
    data class Category(val label: String) : TaskNode()
    data class Command(val label: String, val cmd: String, val isSync: Boolean = false) : TaskNode()
}

private val TASK_GROUPS: List<Pair<String, List<TaskNode.Command>>> = listOf(
    "General" to listOf(
        TaskNode.Command("Build",  "build"),
        TaskNode.Command("Flash",  "flash"),
        TaskNode.Command("Clean",  "clean"),
    ),
    "Analysis" to listOf(
        TaskNode.Command("Profile", "profile"),
        TaskNode.Command("Bench",   "bench"),
    ),
    "Project" to listOf(
        TaskNode.Command("Sync Dependencies", "__sync__", isSync = true),
    ),
)

// ── Tool window factory ──────────────────────────────────────────────────────

class PyMcuToolWindowFactory : ToolWindowFactory {
    override fun createToolWindowContent(project: Project, toolWindow: ToolWindow) {
        val panel = PyMcuTaskPanel(project)
        val content = ContentFactory.getInstance().createContent(panel, "", false)
        toolWindow.contentManager.addContent(content)
    }
    override fun shouldBeAvailable(project: Project): Boolean = true
}

// ── Main panel ──────────────────────────────────────────────────────────────

private class PyMcuTaskPanel(private val project: Project) : JPanel(BorderLayout()) {

    private val log = Logger.getInstance(PyMcuTaskPanel::class.java)

    private val outputArea = JTextArea().apply {
        isEditable = false
        font = Font(Font.MONOSPACED, Font.PLAIN, 12)
        margin = JBUI.insets(4)
    }

    init {
        val tree  = buildTree()
        val north = buildHeader()

        val treeScroll   = JScrollPane(tree).apply { border = BorderFactory.createEmptyBorder() }
        val outputScroll = JScrollPane(outputArea).apply { border = BorderFactory.createEmptyBorder() }

        val split = JSplitPane(JSplitPane.VERTICAL_SPLIT, treeScroll, outputScroll).apply {
            resizeWeight = 0.55
            border = BorderFactory.createEmptyBorder()
        }

        add(north, BorderLayout.NORTH)
        add(split,  BorderLayout.CENTER)

        listenForProjectChanges(north)
    }

    // ── Header (board info + Clear button) ──────────────────────────────────

    private fun buildHeader(): JPanel {
        val label = boardLabel()
        val clearBtn = JButton("Clear").apply {
            icon = AllIcons.Actions.GC
            toolTipText = "Clear output"
            addActionListener { outputArea.text = "" }
        }
        return JPanel(BorderLayout()).apply {
            border = JBUI.Borders.empty(4, 6, 4, 6)
            add(label,   BorderLayout.CENTER)
            add(clearBtn, BorderLayout.EAST)
        }
    }

    private fun boardLabel(): JLabel {
        val config = PyMcuConfigReader.findConfig(project)
        val text = if (config == null) "No PyMCU project detected"
        else buildString {
            append(if (config.board != null) "Board: " else "Chip: ")
            append(config.displayName)
            if (config.frequency != null) append(" @ ${config.frequency} Hz")
        }
        return JLabel(text).apply { border = JBUI.Borders.emptyRight(4) }
    }

    private fun listenForProjectChanges(header: JPanel) {
        project.messageBus.connect().subscribe(
            VirtualFileManager.VFS_CHANGES,
            object : BulkFileListener {
                override fun after(events: List<VFileEvent>) {
                    if (events.any { it.file?.name == "pyproject.toml" }) {
                        SwingUtilities.invokeLater {
                            val lbl = header.getComponent(0) as? JLabel ?: return@invokeLater
                            val config = PyMcuConfigReader.findConfig(project)
                            lbl.text = if (config == null) "No PyMCU project detected"
                            else buildString {
                                append(if (config.board != null) "Board: " else "Chip: ")
                                append(config.displayName)
                                if (config.frequency != null) append(" @ ${config.frequency} Hz")
                            }
                        }
                    }
                }
            }
        )
    }

    // ── Command tree ────────────────────────────────────────────────────────

    private fun buildTree(): JTree {
        val root = DefaultMutableTreeNode("PROJECT TASKS")

        for ((groupName, commands) in TASK_GROUPS) {
            val catNode = DefaultMutableTreeNode(TaskNode.Category(groupName))
            for (cmd in commands) {
                catNode.add(DefaultMutableTreeNode(cmd))
            }
            root.add(catNode)
        }

        val tree = JTree(DefaultTreeModel(root)).apply {
            isRootVisible      = true
            showsRootHandles   = true
            selectionModel.selectionMode = TreeSelectionModel.SINGLE_TREE_SELECTION
            cellRenderer       = TaskTreeRenderer()
            border             = JBUI.Borders.empty(4)
        }

        // Expand all categories by default.
        for (i in 0 until tree.rowCount) tree.expandRow(i)

        tree.addMouseListener(object : java.awt.event.MouseAdapter() {
            override fun mouseClicked(e: java.awt.event.MouseEvent) {
                if (e.clickCount < 2) return
                val path = tree.getPathForLocation(e.x, e.y) ?: return
                val node = path.lastPathComponent as? DefaultMutableTreeNode ?: return
                val cmd  = node.userObject as? TaskNode.Command ?: return
                runCommand(cmd)
            }
        })

        return tree
    }

    // ── Custom tree cell renderer ────────────────────────────────────────────

    private inner class TaskTreeRenderer : ColoredTreeCellRenderer() {
        override fun customizeCellRenderer(
            tree: JTree, value: Any?, selected: Boolean, expanded: Boolean,
            leaf: Boolean, row: Int, hasFocus: Boolean
        ) {
            val node = (value as? DefaultMutableTreeNode)?.userObject ?: return
            when (node) {
                is String -> {  // root "PROJECT TASKS"
                    append(node, SimpleTextAttributes.GRAYED_BOLD_ATTRIBUTES)
                    icon = AllIcons.Nodes.ModuleGroup
                }
                is TaskNode.Category -> {
                    append(node.label, SimpleTextAttributes.REGULAR_BOLD_ATTRIBUTES)
                    icon = AllIcons.Nodes.Folder
                }
                is TaskNode.Command -> {
                    append(node.label, SimpleTextAttributes.REGULAR_ATTRIBUTES)
                    icon = commandIcon(node.cmd)
                }
            }
        }

        private fun commandIcon(cmd: String) = when (cmd) {
            "build"   -> AllIcons.Actions.Compile
            "flash"   -> AllIcons.RunConfigurations.TestState.Run
            "clean"   -> AllIcons.Actions.GC
            "profile" -> AllIcons.Actions.Profile
            "bench"   -> AllIcons.RunConfigurations.TestState.Run_run
            else      -> AllIcons.Actions.Refresh  // sync
        }
    }

    // ── Command execution ────────────────────────────────────────────────────

    private fun runCommand(cmd: TaskNode.Command) {
        if (cmd.isSync) { runSync(); return }

        val settings = PyMcuSettings.getInstance()
        val basePath  = project.basePath ?: run {
            appendOutput("Error: cannot determine project base directory.\n"); return
        }
        appendOutput("\n$ ${settings.executablePath} ${cmd.cmd}\n")

        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                val proc = ProcessBuilder(settings.executablePath, cmd.cmd)
                    .directory(java.io.File(basePath))
                    .redirectErrorStream(true)
                    .start()

                proc.inputStream.bufferedReader().forEachLine { line ->
                    SwingUtilities.invokeLater { appendOutput("$line\n") }
                }
                val exit = proc.waitFor()
                SwingUtilities.invokeLater {
                    appendOutput(if (exit == 0) "✓ Done\n" else "✗ Exited with code $exit\n")
                }
            } catch (e: Exception) {
                SwingUtilities.invokeLater { appendOutput("Error: ${e.message}\n") }
                log.error("PyMCU toolwindow command error", e)
            }
        }
    }

    private fun runSync() {
        val settings = PyMcuSettings.getInstance()
        val basePath  = project.basePath ?: run {
            appendOutput("Error: cannot determine project base directory.\n"); return
        }
        val command = when (settings.packageManager) {
            "uv"     -> listOf("uv", "sync")
            "poetry" -> listOf("poetry", "install")
            "pipenv" -> listOf("pipenv", "install")
            "pip"    -> listOf("pip", "install", "-e", ".")
            else     -> listOf("uv", "sync")
        }
        appendOutput("\n$ ${command.joinToString(" ")}\n")

        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                val proc = ProcessBuilder(command)
                    .directory(java.io.File(basePath))
                    .redirectErrorStream(true)
                    .start()

                proc.inputStream.bufferedReader().forEachLine { line ->
                    SwingUtilities.invokeLater { appendOutput("$line\n") }
                }
                val exit = proc.waitFor()
                SwingUtilities.invokeLater {
                    appendOutput(if (exit == 0) "✓ Done\n" else "✗ Exited with code $exit\n")
                }
            } catch (e: Exception) {
                SwingUtilities.invokeLater { appendOutput("Error: ${e.message}\n") }
                log.error("PyMCU toolwindow sync error", e)
            }
        }
    }

    private fun appendOutput(text: String) {
        outputArea.append(text)
        outputArea.caretPosition = outputArea.document.length
    }
}

