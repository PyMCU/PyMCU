// SPDX-License-Identifier: Apache-2.0
package dev.begeistert.pymcu.newproject

import com.intellij.ide.util.projectWizard.SettingsStep
import com.intellij.openapi.ui.ValidationInfo
import com.intellij.platform.ProjectGeneratorPeer
import com.intellij.util.ui.JBUI
import dev.begeistert.pymcu.boards.BoardTreePanel
import dev.begeistert.pymcu.settings.PyMcuSettings
import java.awt.BorderLayout
import java.awt.Dimension
import java.awt.Font
import javax.swing.*

/**
 * Settings panel for the PyMCU New Project wizard.
 *
 * Layout:
 *   TOP    — BoardTreePanel (Manufacturer → Board, PlatformIO-style)
 *   BOTTOM — Framework (stdlib compat)
 */
class PyMcuProjectGeneratorPeer : ProjectGeneratorPeer<PyMcuNewProjectSettings> {

    private val boardPanel = BoardTreePanel().apply {
        preferredSize = Dimension(460, 280)
    }

    // ── Framework (stdlib compat) ─────────────────────────────────────────────
    private val frameworkItems  = arrayOf(
        "MicroPython (recommended)",
        "CircuitPython",
        "PyMCU native (experimental)"
    )
    private val frameworkValues = arrayOf("micropython", "circuitpython", "none")
    private val frameworkCombo  = JComboBox(frameworkItems)

    private val panel: JPanel = buildPanel()

    init {
        boardPanel.preselect("arduino_uno")
    }

    private fun buildPanel(): JPanel {
        val optionsPanel = JPanel().apply {
            layout = BoxLayout(this, BoxLayout.Y_AXIS)
            border = JBUI.Borders.empty(6, 0, 0, 0)

            fun row(labelText: String, comp: JComponent): JPanel =
                JPanel(BorderLayout(8, 0)).apply {
                    add(JLabel(labelText).apply { preferredSize = Dimension(130, 24) }, BorderLayout.WEST)
                    add(comp, BorderLayout.CENTER)
                    border = JBUI.Borders.empty(2, 0)
                    maximumSize = Dimension(Int.MAX_VALUE, 32)
                }

            add(row("Framework:", frameworkCombo))

            val hint = JLabel("<html><font color='gray' size='2'>Creates pyproject.toml + src/main.py.<br>MicroPython and CircuitPython are stable. PyMCU native HAL is experimental.</font></html>")
            hint.border = JBUI.Borders.empty(6, 0, 0, 0)
            hint.alignmentX = 0f
            add(hint)
        }

        return JPanel(BorderLayout()).apply {
            val boardLabel = JLabel("Board").apply {
                font = font.deriveFont(Font.BOLD, 12f)
                border = JBUI.Borders.empty(0, 0, 4, 0)
            }
            add(boardLabel, BorderLayout.NORTH)
            add(boardPanel, BorderLayout.CENTER)
            add(optionsPanel, BorderLayout.SOUTH)
        }
    }

    override fun getComponent(): JComponent = panel

    override fun buildUI(settingsStep: SettingsStep) {
        settingsStep.addSettingsComponent(panel)
    }

    override fun getSettings(): PyMcuNewProjectSettings {
        val board = boardPanel.selectedBoard()
        return PyMcuNewProjectSettings(
            chip           = board?.chip,
            board          = board?.id,
            frequency      = board?.freqHz ?: 16_000_000,
            packageManager = PyMcuSettings.getInstance().packageManager,
            stdlib         = frameworkValues[frameworkCombo.selectedIndex]
        )
    }

    override fun validate(): ValidationInfo? {
        if (boardPanel.selectedBoard() == null) {
            return ValidationInfo("Please select a target board.", boardPanel)
        }
        return null
    }

    override fun isBackgroundJobRunning(): Boolean = false
}
