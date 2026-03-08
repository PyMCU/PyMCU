package dev.begeistert.pymcu.newproject

import com.intellij.ide.util.projectWizard.SettingsStep
import com.intellij.openapi.ui.ValidationInfo
import com.intellij.platform.ProjectGeneratorPeer
import java.awt.GridBagConstraints
import java.awt.GridBagLayout
import java.awt.Insets
import javax.swing.*

/**
 * The settings panel shown in PyCharm's New Project wizard for PyMCU projects.
 */
class PyMcuProjectGeneratorPeer : ProjectGeneratorPeer<PyMcuNewProjectSettings> {

    private val chipItems = arrayOf(
        "atmega328p", "atmega2560", "atmega168", "atmega88",
        "attiny85", "attiny84", "attiny2313"
    )
    private val freqItems = arrayOf(
        "16000000 Hz (16 MHz)",
        "8000000 Hz  (8 MHz)",
        "4000000 Hz  (4 MHz)",
        "1000000 Hz  (1 MHz)"
    )
    private val freqValues = intArrayOf(16_000_000, 8_000_000, 4_000_000, 1_000_000)
    private val pmItems = arrayOf("uv", "pip", "poetry", "pipenv")

    private val chipCombo   = JComboBox(chipItems)
    private val freqCombo   = JComboBox(freqItems)
    private val pmCombo     = JComboBox(pmItems)

    private val panel: JPanel = JPanel(GridBagLayout()).apply {
        val gbc = GridBagConstraints().apply {
            anchor = GridBagConstraints.WEST
            insets = Insets(4, 4, 4, 8)
        }

        fun label(text: String): JLabel = JLabel(text)

        // Row 0 — chip
        gbc.gridx = 0; gbc.gridy = 0; gbc.fill = GridBagConstraints.NONE
        add(label("Target chip:"), gbc)
        gbc.gridx = 1; gbc.fill = GridBagConstraints.HORIZONTAL; gbc.weightx = 1.0
        add(chipCombo, gbc)

        // Row 1 — frequency
        gbc.gridx = 0; gbc.gridy = 1; gbc.fill = GridBagConstraints.NONE; gbc.weightx = 0.0
        add(label("CPU frequency:"), gbc)
        gbc.gridx = 1; gbc.fill = GridBagConstraints.HORIZONTAL; gbc.weightx = 1.0
        add(freqCombo, gbc)

        // Row 2 — package manager
        gbc.gridx = 0; gbc.gridy = 2; gbc.fill = GridBagConstraints.NONE; gbc.weightx = 0.0
        add(label("Package manager:"), gbc)
        gbc.gridx = 1; gbc.fill = GridBagConstraints.HORIZONTAL; gbc.weightx = 1.0
        add(pmCombo, gbc)

        // Row 3 — hint
        gbc.gridx = 0; gbc.gridy = 3; gbc.gridwidth = 2
        gbc.fill = GridBagConstraints.NONE; gbc.weightx = 0.0
        val hint = JLabel(
            "<html><font color='gray' size='2'>Creates pyproject.toml + src/main.py and runs <i>uv sync</i>.</font></html>"
        )
        add(hint, gbc)
    }

    override fun getComponent(): JComponent = panel

    override fun buildUI(settingsStep: SettingsStep) {
        settingsStep.addSettingsComponent(panel)
    }

    override fun getSettings(): PyMcuNewProjectSettings = PyMcuNewProjectSettings(
        chip           = chipCombo.selectedItem as String,
        frequency      = freqValues[freqCombo.selectedIndex],
        packageManager = pmCombo.selectedItem as String
    )

    override fun validate(): ValidationInfo? = null

    override fun isBackgroundJobRunning(): Boolean = false
}
