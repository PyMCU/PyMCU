package dev.begeistert.pymcu.debug

import com.intellij.execution.Executor
import com.intellij.execution.configurations.ConfigurationFactory
import com.intellij.execution.configurations.RunConfigurationBase
import com.intellij.execution.configurations.RunProfileState
import com.intellij.execution.configurations.RunnerSettings
import com.intellij.execution.runners.ExecutionEnvironment
import com.intellij.openapi.options.SettingsEditor
import com.intellij.openapi.project.Project
import com.intellij.ui.components.JBLabel
import com.intellij.util.ui.FormBuilder
import org.jdom.Element
import javax.swing.JComponent
import javax.swing.JPanel
import javax.swing.JSpinner
import javax.swing.SpinnerNumberModel

@Suppress("UnstableApiUsage")
class PyMcuDebugConfiguration(
    project: Project,
    factory: ConfigurationFactory,
    name: String
) : RunConfigurationBase<RunnerSettings>(project, factory, name) {

    var serverPort: Int = 57000

    override fun readExternal(element: Element) {
        super.readExternal(element)
        serverPort = element.getAttributeValue("serverPort")?.toIntOrNull() ?: 57000
    }

    override fun writeExternal(element: Element) {
        super.writeExternal(element)
        element.setAttribute("serverPort", serverPort.toString())
    }

    override fun getConfigurationEditor(): SettingsEditor<PyMcuDebugConfiguration> =
        PyMcuDebugConfigurationEditor()

    // GenericProgramRunner.execute() checks state != null before calling doExecute().
    // The runner handles everything itself; this stub satisfies the framework contract.
    override fun getState(executor: Executor, environment: ExecutionEnvironment): RunProfileState =
        RunProfileState { _, _ -> null }

    private inner class PyMcuDebugConfigurationEditor : SettingsEditor<PyMcuDebugConfiguration>() {

        private val portSpinner = JSpinner(SpinnerNumberModel(57000, 1024, 65535, 1))

        override fun resetEditorFrom(config: PyMcuDebugConfiguration) {
            portSpinner.value = config.serverPort
        }

        override fun applyEditorTo(config: PyMcuDebugConfiguration) {
            config.serverPort = portSpinner.value as Int
        }

        override fun createEditor(): JComponent =
            FormBuilder.createFormBuilder()
                .addLabeledComponent(JBLabel("Debug server port:"), portSpinner, 1, false)
                .addComponentFillVertically(JPanel(), 0)
                .panel
    }
}
