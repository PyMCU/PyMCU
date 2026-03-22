package dev.begeistert.whisnake.statusbar

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
import dev.begeistert.whisnake.config.WhisnakeConfigReader
import dev.begeistert.whisnake.settings.WhisnakeSettingsConfigurable
import java.awt.event.MouseEvent

private const val WIDGET_ID = "WhisnakeStatusBarWidget"

// ---------------------------------------------------------------------------
// Widget
// ---------------------------------------------------------------------------

/**
 * Status bar widget that shows the target chip name (e.g., "⚙ atmega328p").
 * Clicking the widget opens Settings > Tools > Whisnake.
 */
class WhisnakeStatusBarWidget(private val project: Project) :
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
        val config = WhisnakeConfigReader.findConfig(project) ?: return "No Whisnake project detected"
        val sb = StringBuilder("Whisnake: ${config.displayName}")
        if (config.frequency != null) sb.append(" @ ${config.frequency} Hz")
        if (config.stdlib.isNotEmpty()) sb.append(" · ${config.stdlib.joinToString(", ")}")
        if (config.hasFfi) sb.append(" · C/C++ FFI")
        return sb.toString()
    }

    override fun getClickConsumer(): Consumer<MouseEvent> = Consumer {
        ShowSettingsUtil.getInstance().showSettingsDialog(
            project,
            WhisnakeSettingsConfigurable::class.java
        )
    }

    // Helpers ------------------------------------------------------------------

    private fun buildChipText(): String {
        val config = WhisnakeConfigReader.findConfig(project) ?: return ""
        return "\u2699 ${config.displayName}"
    }
}

// ---------------------------------------------------------------------------
// Factory
// ---------------------------------------------------------------------------

/**
 * Registers [WhisnakeStatusBarWidget] with the IDE status bar.
 * The widget is only shown when a pymcu project is detected.
 */
@Suppress("UnstableApiUsage")
class WhisnakeStatusBarWidgetFactory : StatusBarWidgetFactory {

    override fun getId(): String = WIDGET_ID

    override fun getDisplayName(): String = "Whisnake Chip"

    override fun isAvailable(project: Project): Boolean =
        WhisnakeConfigReader.findConfig(project) != null

    override fun createWidget(project: Project): StatusBarWidget =
        WhisnakeStatusBarWidget(project)

    override fun disposeWidget(widget: StatusBarWidget) {
        widget.dispose()
    }

    override fun canBeEnabledOn(statusBar: StatusBar): Boolean = true
}
