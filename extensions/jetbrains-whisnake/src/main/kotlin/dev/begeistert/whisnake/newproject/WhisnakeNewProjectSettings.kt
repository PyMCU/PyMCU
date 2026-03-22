package dev.begeistert.whisnake.newproject

/**
 * Holds the user-configured options from the New Project wizard panel.
 */
data class WhisnakeNewProjectSettings(
    /** Explicit chip name. Null when [board] is set. */
    val chip: String? = "atmega328p",
    /** Board alias e.g. "arduino_uno". When set, [chip] may be null. */
    val board: String? = null,
    val frequency: Int = 16_000_000,
    val packageManager: String = "uv",
    /**
     * Compat stdlib to activate:
     *  "none"          — bare Whisnake (no stdlib compat layer)
     *  "micropython"   — pymcu-micropython (machine, utime, etc.)
     *  "circuitpython" — pymcu-circuitpython (board, digitalio, busio, etc.)
     */
    val stdlib: String = "none"
)
