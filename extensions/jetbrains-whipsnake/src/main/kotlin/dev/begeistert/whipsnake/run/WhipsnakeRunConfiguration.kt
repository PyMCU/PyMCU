package dev.begeistert.whipsnake.run

import com.intellij.execution.ExecutionException
import com.intellij.execution.Executor
import com.intellij.execution.configurations.CommandLineState
import com.intellij.execution.configurations.ConfigurationFactory
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.configurations.RunConfigurationBase
import com.intellij.execution.configurations.RunnerSettings
import com.intellij.execution.process.KillableColoredProcessHandler
import com.intellij.execution.process.ProcessHandler
import com.intellij.execution.runners.ExecutionEnvironment
import com.intellij.openapi.options.SettingsEditor
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.ComboBox
import com.intellij.ui.components.JBLabel
import com.intellij.util.ui.FormBuilder
import dev.begeistert.whipsnake.settings.WhipsnakeSettings
import org.jdom.Element
import javax.swing.JComponent
import javax.swing.JPanel

/**
 * A single Whipsnake run configuration that runs one of: build, flash, clean.
 */
@Suppress("UnstableApiUsage")
class WhipsnakeRunConfiguration(
    project: Project,
    factory: ConfigurationFactory,
    name: String
) : RunConfigurationBase<RunnerSettings>(project, factory, name) {

    /** The whip sub-command to execute. */
    var command: String = "build"

    // -------------------------------------------------------------------------
    // Persistence
    // -------------------------------------------------------------------------

    override fun readExternal(element: Element) {
        super.readExternal(element)
        command = element.getAttributeValue("command") ?: "build"
    }

    override fun writeExternal(element: Element) {
        super.writeExternal(element)
        element.setAttribute("command", command)
    }

    // -------------------------------------------------------------------------
    // Editor
    // -------------------------------------------------------------------------

    override fun getConfigurationEditor(): SettingsEditor<WhipsnakeRunConfiguration> =
        WhipsnakeRunConfigurationEditor()

    // -------------------------------------------------------------------------
    // Execution
    // -------------------------------------------------------------------------

    override fun getState(executor: Executor, environment: ExecutionEnvironment): CommandLineState {
        val settings = WhipsnakeSettings.getInstance()
        val basePath = project.basePath
            ?: throw ExecutionException("Cannot determine project base directory.")

        return object : CommandLineState(environment) {
            override fun startProcess(): ProcessHandler {
                val cmdLine = GeneralCommandLine(settings.executablePath, command)
                    .withWorkDirectory(basePath)
                    .withRedirectErrorStream(true)

                // KillableColoredProcessHandler sends SIGTERM before SIGKILL on destroy
                return KillableColoredProcessHandler(cmdLine)
            }
        }
    }

    // -------------------------------------------------------------------------
    // Inner editor class
    // -------------------------------------------------------------------------

    private inner class WhipsnakeRunConfigurationEditor : SettingsEditor<WhipsnakeRunConfiguration>() {

        private val commandCombo = ComboBox(arrayOf("build", "flash", "clean", "new", "version"))

        override fun resetEditorFrom(config: WhipsnakeRunConfiguration) {
            commandCombo.selectedItem = config.command
        }

        override fun applyEditorTo(config: WhipsnakeRunConfiguration) {
            config.command = commandCombo.selectedItem as? String ?: "build"
        }

        override fun createEditor(): JComponent {
            return FormBuilder.createFormBuilder()
                .addLabeledComponent(JBLabel("Command:"), commandCombo, 1, false)
                .addComponentFillVertically(JPanel(), 0)
                .panel
        }
    }
}
