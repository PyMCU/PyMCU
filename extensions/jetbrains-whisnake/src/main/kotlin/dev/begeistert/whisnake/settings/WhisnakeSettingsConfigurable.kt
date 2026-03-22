package dev.begeistert.whisnake.settings

import com.intellij.openapi.options.Configurable
import com.intellij.openapi.ui.ComboBox
import com.intellij.ui.components.JBLabel
import com.intellij.ui.components.JBTextField
import com.intellij.util.ui.FormBuilder
import javax.swing.JComponent
import javax.swing.JPanel

/**
 * Settings UI for Whisnake, accessible at Settings > Tools > Whisnake.
 */
class WhisnakeSettingsConfigurable : Configurable {

    private var executableField: JBTextField? = null
    private var packageManagerCombo: ComboBox<String>? = null
    private var panel: JPanel? = null

    override fun getDisplayName(): String = "Whisnake"

    override fun createComponent(): JComponent {
        executableField = JBTextField()
        packageManagerCombo = ComboBox(arrayOf("uv", "pip", "poetry", "pipenv"))

        panel = FormBuilder.createFormBuilder()
            .addLabeledComponent(JBLabel("Whisnake Executable:"), executableField!!, 1, false)
            .addLabeledComponent(JBLabel("Package Manager:"), packageManagerCombo!!, 1, false)
            .addComponentFillVertically(JPanel(), 0)
            .panel

        return panel!!
    }

    override fun isModified(): Boolean {
        val settings = WhisnakeSettings.getInstance()
        return executableField?.text != settings.executablePath ||
               packageManagerCombo?.selectedItem as? String != settings.packageManager
    }

    override fun apply() {
        val settings = WhisnakeSettings.getInstance()
        settings.executablePath = executableField?.text?.takeIf { it.isNotBlank() } ?: "whisnake"
        settings.packageManager = packageManagerCombo?.selectedItem as? String ?: "uv"
    }

    override fun reset() {
        val settings = WhisnakeSettings.getInstance()
        executableField?.text = settings.executablePath
        packageManagerCombo?.selectedItem = settings.packageManager
    }

    override fun disposeUIResources() {
        panel = null
        executableField = null
        packageManagerCombo = null
    }
}
