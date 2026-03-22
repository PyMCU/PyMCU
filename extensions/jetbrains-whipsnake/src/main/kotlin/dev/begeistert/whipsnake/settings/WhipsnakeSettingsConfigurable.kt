package dev.begeistert.whipsnake.settings

import com.intellij.openapi.options.Configurable
import com.intellij.openapi.ui.ComboBox
import com.intellij.ui.components.JBLabel
import com.intellij.ui.components.JBTextField
import com.intellij.util.ui.FormBuilder
import javax.swing.JComponent
import javax.swing.JPanel

/**
 * Settings UI for Whipsnake, accessible at Settings > Tools > Whipsnake.
 */
class WhipsnakeSettingsConfigurable : Configurable {

    private var executableField: JBTextField? = null
    private var packageManagerCombo: ComboBox<String>? = null
    private var panel: JPanel? = null

    override fun getDisplayName(): String = "Whipsnake"

    override fun createComponent(): JComponent {
        executableField = JBTextField()
        packageManagerCombo = ComboBox(arrayOf("uv", "pip", "poetry", "pipenv"))

        panel = FormBuilder.createFormBuilder()
            .addLabeledComponent(JBLabel("Whipsnake Executable:"), executableField!!, 1, false)
            .addLabeledComponent(JBLabel("Package Manager:"), packageManagerCombo!!, 1, false)
            .addComponentFillVertically(JPanel(), 0)
            .panel

        return panel!!
    }

    override fun isModified(): Boolean {
        val settings = WhipsnakeSettings.getInstance()
        return executableField?.text != settings.executablePath ||
               packageManagerCombo?.selectedItem as? String != settings.packageManager
    }

    override fun apply() {
        val settings = WhipsnakeSettings.getInstance()
        settings.executablePath = executableField?.text?.takeIf { it.isNotBlank() } ?: "whipsnake"
        settings.packageManager = packageManagerCombo?.selectedItem as? String ?: "uv"
    }

    override fun reset() {
        val settings = WhipsnakeSettings.getInstance()
        executableField?.text = settings.executablePath
        packageManagerCombo?.selectedItem = settings.packageManager
    }

    override fun disposeUIResources() {
        panel = null
        executableField = null
        packageManagerCombo = null
    }
}
