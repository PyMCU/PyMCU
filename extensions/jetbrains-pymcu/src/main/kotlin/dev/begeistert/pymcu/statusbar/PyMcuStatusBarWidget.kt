package dev.begeistert.pymcu.statusbar

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.options.ShowSettingsUtil
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.openapi.vfs.newvfs.BulkFileListener
import com.intellij.openapi.vfs.newvfs.events.VFileEvent
import com.intellij.openapi.wm.StatusBar
import com.intellij.openapi.wm.StatusBarWidget
import com.intellij.openapi.wm.StatusBarWidgetFactory
import com.intellij.util.Consumer
import dev.begeistert.pymcu.config.PyMcuConfigReader
import dev.begeistert.pymcu.settings.PyMcuSettingsConfigurable
import java.awt.event.MouseEvent

private const val WIDGET_ID = "PyMcuStatusBarWidget"

// ---------------------------------------------------------------------------
// Widget
// ---------------------------------------------------------------------------

/**
 * Status bar widget that shows the target chip name (e.g., "⚙ atmega328p").
 * Clicking the widget opens Settings > Tools > PyMCU.
 */
class PyMcuStatusBarWidget(private val project: Project) :
    StatusBarWidget, StatusBarWidget.TextPresentation {

    private var statusBar: StatusBar? = null
    private var chipText: String = buildChipText()

    init {
        // Listen for pyproject.toml changes and refresh the displayed chip name
        project.messageBus.connect().subscribe(
            VirtualFileManager.VFS_CHANGES,
            object : BulkFileListener {
                override fun after(events: List<VFileEvent>) {
                    if (events.any { it.file?.name == "pyproject.toml" }) {
                        chipText = buildChipText()
                        ApplicationManager.getApplication().invokeLater {
                            statusBar?.updateWidget(ID())
                        }
                    }
                }
            }
        )
    }

    override fun ID(): String = WIDGET_ID

    override fun getPresentation(): StatusBarWidget.WidgetPresentation = this

    override fun install(statusBar: StatusBar) {
        this.statusBar = statusBar
    }

    override fun dispose() {
        statusBar = null
    }

    // TextPresentation ---------------------------------------------------------

    override fun getText(): String = chipText

    override fun getAlignment(): Float = 0.5f  // center

    override fun getTooltipText(): String {
        val config = PyMcuConfigReader.findConfig(project)
        return if (config?.chip != null) "PyMCU: ${config.chip}" else "No PyMCU project detected"
    }

    override fun getClickConsumer(): Consumer<MouseEvent> = Consumer {
        ShowSettingsUtil.getInstance().showSettingsDialog(
            project,
            PyMcuSettingsConfigurable::class.java
        )
    }

    // Helpers ------------------------------------------------------------------

    private fun buildChipText(): String {
        val config = PyMcuConfigReader.findConfig(project)
        return if (config?.chip != null) "\u2699 ${config.chip}" else ""
    }
}

// ---------------------------------------------------------------------------
// Factory
// ---------------------------------------------------------------------------

/**
 * Registers [PyMcuStatusBarWidget] with the IDE status bar.
 * The widget is only shown when a pymcu project is detected.
 */
@Suppress("UnstableApiUsage")
class PyMcuStatusBarWidgetFactory : StatusBarWidgetFactory {

    override fun getId(): String = WIDGET_ID

    override fun getDisplayName(): String = "PyMCU Chip"

    override fun isAvailable(project: Project): Boolean =
        PyMcuConfigReader.findConfig(project) != null

    override fun createWidget(project: Project): StatusBarWidget =
        PyMcuStatusBarWidget(project)

    override fun disposeWidget(widget: StatusBarWidget) {
        widget.dispose()
    }

    override fun canBeEnabledOn(statusBar: StatusBar): Boolean = true
}
