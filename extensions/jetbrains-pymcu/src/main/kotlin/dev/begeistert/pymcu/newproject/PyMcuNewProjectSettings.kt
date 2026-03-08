package dev.begeistert.pymcu.newproject

/**
 * Holds the user-configured options from the New Project wizard panel.
 */
data class PyMcuNewProjectSettings(
    val chip: String = "atmega328p",
    val frequency: Int = 16_000_000,
    val packageManager: String = "uv"
)
