// SPDX-License-Identifier: Apache-2.0
package dev.begeistert.pymcu.boards

data class BoardEntry(
    val id: String,
    val displayName: String,
    val chip: String,
    val manufacturer: String,
    val arch: String,           // "avr", "arm", etc. — for backend routing
    val freqHz: Int,
    val flashKb: Int,
    val ramBytes: Int
) {
    val freqMhz: Int    get() = freqHz / 1_000_000
    val ramDisplay: String get() = if (ramBytes >= 1024) "${ramBytes / 1024}K" else "${ramBytes}B"

    /** PlatformIO-style parenthetical detail shown in the tree cell. */
    val treeDetail: String
        get() = "(${chip.uppercase()}, ${freqMhz}MHz, ROM: ${flashKb}K, RAM: $ramDisplay)"

    val summary: String
        get() = "${chip.uppercase()}  •  ${freqMhz} MHz  •  ${flashKb} KB flash  •  $ramDisplay RAM"
}
