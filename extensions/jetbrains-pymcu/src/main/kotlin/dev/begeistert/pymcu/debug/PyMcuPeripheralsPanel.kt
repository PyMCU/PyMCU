// SPDX-License-Identifier: Apache-2.0
package dev.begeistert.pymcu.debug

import com.intellij.openapi.diagnostic.Logger
import com.intellij.ui.JBColor
import com.intellij.ui.components.JBScrollPane
import com.intellij.ui.treeStructure.Tree
import java.awt.BorderLayout
import java.awt.Color
import java.awt.Component
import javax.swing.JLabel
import javax.swing.JPanel
import javax.swing.JTree
import javax.swing.tree.DefaultMutableTreeNode
import javax.swing.tree.DefaultTreeCellRenderer
import javax.swing.tree.DefaultTreeModel

/**
 * Peripherals panel for the PyMCU debugger.
 *
 * Displays a tree of ATmega328P I/O register values, grouped by peripheral.
 * On each debug stop the panel requests a memory snapshot from the emulator
 * and highlights any register whose value changed since the previous stop.
 */
class PyMcuPeripheralsPanel(private val client: PyMcuDebugClient) : JPanel(BorderLayout()) {

    private val log = Logger.getInstance(PyMcuPeripheralsPanel::class.java)

    // Live value map: register data-space address → last known byte value (-1 = unknown)
    private val currentValues = HashMap<Int, Int>()
    private val previousValues = HashMap<Int, Int>()
    // Set of addresses whose value changed in the most recent refresh
    private val changedAddresses = HashSet<Int>()

    private val root = DefaultMutableTreeNode("ATmega328P")
    private val treeModel = DefaultTreeModel(root)
    private val tree = Tree(treeModel)

    /** Keyed by register address → the tree node for fast update. */
    private val registerNodes = HashMap<Int, RegisterNode>()

    init {
        buildTree()
        tree.isRootVisible = true
        tree.cellRenderer = PeripheralTreeCellRenderer()
        tree.expandRow(0)
        // Expand all peripheral nodes by default
        for (i in 0 until tree.rowCount) tree.expandRow(i)
        add(JBScrollPane(tree), BorderLayout.CENTER)

        // Refresh button at the top
        val toolbar = JPanel(BorderLayout())
        val refreshLabel = JLabel("Peripherals  (refreshed on each stop)")
        refreshLabel.border = javax.swing.BorderFactory.createEmptyBorder(4, 8, 4, 8)
        toolbar.add(refreshLabel, BorderLayout.WEST)
        add(toolbar, BorderLayout.NORTH)
    }

    private fun buildTree() {
        root.removeAllChildren()
        for (peripheral in AvrPeripheralDefs.peripherals) {
            val pNode = DefaultMutableTreeNode(peripheral.name)
            for (reg in peripheral.registers) {
                val rNode = RegisterNode(reg)
                registerNodes[reg.address] = rNode
                if (reg.fields.isNotEmpty()) {
                    for (field in reg.fields) {
                        rNode.add(BitFieldNode(field, reg.address))
                    }
                }
                pNode.add(rNode)
            }
            root.add(pNode)
        }
        treeModel.reload()
    }

    /**
     * Called from the debug runner on each stop event (background thread OK).
     * Requests a memory snapshot and updates all register values asynchronously.
     */
    fun refresh() {
        log.info("PyMCU[peripherals] requesting memory snapshot addr=0x${AvrPeripheralDefs.SNAPSHOT_BASE.toString(16)} len=${AvrPeripheralDefs.SNAPSHOT_SIZE}")
        client.requestMemory(AvrPeripheralDefs.SNAPSHOT_BASE, AvrPeripheralDefs.SNAPSHOT_SIZE) { addr, bytes ->
            log.info("PyMCU[peripherals] snapshot received: ${bytes.size} bytes @ 0x${addr.toString(16)}")
            updateValues(addr, bytes)
        }
    }

    private fun updateValues(baseAddr: Int, bytes: ByteArray) {
        // Save previous values and detect changes
        previousValues.clear()
        previousValues.putAll(currentValues)
        changedAddresses.clear()

        for (peripheral in AvrPeripheralDefs.peripherals) {
            for (reg in peripheral.registers) {
                val offset = reg.address - baseAddr
                if (offset < 0 || offset >= bytes.size) continue
                val newVal = bytes[offset].toInt() and 0xFF
                val oldVal = currentValues[reg.address]
                if (oldVal != null && oldVal != newVal) {
                    changedAddresses.add(reg.address)
                }
                currentValues[reg.address] = newVal
            }
        }

        // Update tree nodes on EDT
        javax.swing.SwingUtilities.invokeLater {
            for ((addr, node) in registerNodes) {
                val value = currentValues[addr]
                node.updateValue(value, addr in changedAddresses)
            }
            treeModel.nodeChanged(root)
            // Repaint to pick up color changes
            tree.repaint()
        }
    }

    // ─── Tree node types ───────────────────────────────────────────────────────

    inner class RegisterNode(val reg: AvrPeripheralDefs.Register) :
        DefaultMutableTreeNode(reg) {

        var displayValue: Int? = null
        var changed: Boolean = false

        fun updateValue(value: Int?, isChanged: Boolean) {
            displayValue = value
            changed = isChanged
        }

        fun label(): String {
            val addr = "0x${reg.address.toString(16).uppercase().padStart(2, '0')}"
            val valStr = if (displayValue != null)
                "0x${displayValue!!.toString(16).uppercase().padStart(2, '0')}  (${displayValue!!})"
            else "—"
            return "${reg.name}  [$addr] = $valStr"
        }
    }

    inner class BitFieldNode(
        val field: AvrPeripheralDefs.BitField,
        val parentAddr: Int
    ) : DefaultMutableTreeNode(field) {

        fun label(): String {
            val regValue = currentValues[parentAddr]
            val fieldVal = if (regValue != null) {
                val width = field.msb - field.lsb + 1
                val mask = (1 shl width) - 1
                (regValue shr field.lsb) and mask
            } else null
            val valStr = if (fieldVal != null) "$fieldVal" else "—"
            return "${field.name} = $valStr"
        }
    }

    // ─── Cell renderer ─────────────────────────────────────────────────────────

    private inner class PeripheralTreeCellRenderer : DefaultTreeCellRenderer() {

        private val changedColor  = JBColor(Color(0xFF8C00), Color(0xFFAA44))
        private val addressColor  = JBColor(Color(0x888888), Color(0x777777))
        private val bitFieldColor = JBColor(Color(0x4488CC), Color(0x6699DD))

        override fun getTreeCellRendererComponent(
            tree: JTree, value: Any?, selected: Boolean,
            expanded: Boolean, leaf: Boolean, row: Int, hasFocus: Boolean
        ): Component {
            val label = super.getTreeCellRendererComponent(tree, value, selected, expanded, leaf, row, hasFocus) as JLabel
            when (val node = value) {
                is RegisterNode -> {
                    label.text = node.label()
                    if (!selected) {
                        label.foreground = if (node.changed) changedColor else foreground
                    }
                    icon = null
                }
                is BitFieldNode -> {
                    label.text = node.label()
                    if (!selected) label.foreground = bitFieldColor
                    icon = null
                }
                is DefaultMutableTreeNode -> {
                    val obj = node.userObject
                    if (obj is String) label.text = obj
                    icon = null
                }
            }
            return label
        }
    }
}
