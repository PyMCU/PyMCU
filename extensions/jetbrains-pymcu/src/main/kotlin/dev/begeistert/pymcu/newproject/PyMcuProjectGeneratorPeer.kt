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
 *
 * Layout (GridBagLayout rows):
 *   0  Board      — Arduino Uno / Nano / Mega / Micro  or  "Custom chip…"
 *   1  Chip       — visible only when board = "Custom chip…"
 *   2  Frequency  — CPU clock
 *   3  Stdlib     — None / MicroPython compat / CircuitPython compat
 *   4  Pkg mgr    — uv / pip / poetry / pipenv
 *   5  Hint
 */
class PyMcuProjectGeneratorPeer : ProjectGeneratorPeer<PyMcuNewProjectSettings> {

    // ── Board combos ────────────────────────────────────────────────────────
    private val boardDisplayItems = arrayOf(
        "Arduino Uno  (atmega328p, 16 MHz)",
        "Arduino Nano (atmega328p, 16 MHz)",
        "Arduino Mega (atmega2560, 16 MHz)",
        "Arduino Micro (atmega32u4, 16 MHz)",
        "Custom chip…"
    )
    private val boardValues = arrayOf(
        "arduino_uno", "arduino_nano", "arduino_mega", "arduino_micro", null
    )
    // Default chip frequencies for each board slot (used when board is selected)
    private val boardFreqDefaults = intArrayOf(
        16_000_000, 16_000_000, 16_000_000, 16_000_000, 16_000_000
    )

    // ── Custom chip (shown only for "Custom chip…" board selection) ─────────
    private val chipItems = arrayOf(
        "atmega328p", "atmega2560", "atmega32u4", "atmega168", "atmega88",
        "attiny85", "attiny84", "attiny2313"
    )

    // ── Frequency ────────────────────────────────────────────────────────────
    private val freqItems = arrayOf(
        "16000000 Hz (16 MHz)",
        "8000000 Hz  (8 MHz)",
        "4000000 Hz  (4 MHz)",
        "1000000 Hz  (1 MHz)"
    )
    private val freqValues = intArrayOf(16_000_000, 8_000_000, 4_000_000, 1_000_000)

    // ── Stdlib compat ────────────────────────────────────────────────────────
    private val stdlibItems = arrayOf(
        "None (bare PyMCU)",
        "MicroPython compat  (machine, utime…)",
        "CircuitPython compat  (board, digitalio, busio…)"
    )
    private val stdlibValues = arrayOf("none", "micropython", "circuitpython")

    // ── Package manager ──────────────────────────────────────────────────────
    private val pmItems = arrayOf("uv", "pip", "poetry", "pipenv")

    // ── UI components ────────────────────────────────────────────────────────
    private val boardCombo  = JComboBox(boardDisplayItems)
    private val chipCombo   = JComboBox(chipItems)
    private val freqCombo   = JComboBox(freqItems)
    private val stdlibCombo = JComboBox(stdlibItems)
    private val pmCombo     = JComboBox(pmItems)

    private val chipLabel   = JLabel("Chip:")

    private val panel: JPanel = JPanel(GridBagLayout()).apply {
        val gbc = GridBagConstraints().apply {
            anchor = GridBagConstraints.WEST
            insets = Insets(4, 4, 4, 8)
        }
        fun row(r: Int, lbl: String, comp: JComponent) {
            gbc.gridx = 0; gbc.gridy = r; gbc.fill = GridBagConstraints.NONE; gbc.weightx = 0.0
            add(JLabel(lbl), gbc)
            gbc.gridx = 1; gbc.fill = GridBagConstraints.HORIZONTAL; gbc.weightx = 1.0
            add(comp, gbc)
        }

        row(0, "Board:",           boardCombo)
        row(1, "Chip:",            chipCombo)   // conditionally hidden
        row(2, "CPU frequency:",   freqCombo)
        row(3, "Stdlib compat:",   stdlibCombo)
        row(4, "Package manager:", pmCombo)

        gbc.gridx = 0; gbc.gridy = 5; gbc.gridwidth = 2
        gbc.fill = GridBagConstraints.NONE; gbc.weightx = 0.0
        add(JLabel(
            "<html><font color='gray' size='2'>Creates pyproject.toml + src/main.py and runs sync.</font></html>"
        ), gbc)
    }

    init {
        // Show/hide custom chip row based on board selection
        updateChipRowVisibility()
        boardCombo.addActionListener { updateChipRowVisibility() }
    }

    private fun updateChipRowVisibility() {
        val isCustom = boardValues[boardCombo.selectedIndex] == null
        chipLabel.isVisible  = isCustom
        chipCombo.isVisible  = isCustom
    }

    override fun getComponent(): JComponent = panel

    override fun buildUI(settingsStep: SettingsStep) {
        settingsStep.addSettingsComponent(panel)
    }

    override fun getSettings(): PyMcuNewProjectSettings {
        val boardIdx = boardCombo.selectedIndex
        val board    = boardValues[boardIdx]
        val chip     = if (board == null) chipCombo.selectedItem as String else null
        return PyMcuNewProjectSettings(
            chip           = chip,
            board          = board,
            frequency      = freqValues[freqCombo.selectedIndex],
            packageManager = pmCombo.selectedItem as String,
            stdlib         = stdlibValues[stdlibCombo.selectedIndex]
        )
    }

    override fun validate(): ValidationInfo? = null

    override fun isBackgroundJobRunning(): Boolean = false
}
