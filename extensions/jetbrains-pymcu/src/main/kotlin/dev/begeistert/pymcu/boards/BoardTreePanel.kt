// SPDX-License-Identifier: Apache-2.0
package dev.begeistert.pymcu.boards

import com.intellij.icons.AllIcons
import com.intellij.ui.ColoredTreeCellRenderer
import com.intellij.ui.SimpleTextAttributes
import com.intellij.ui.TreeSpeedSearch
import com.intellij.ui.components.JBScrollPane
import com.intellij.util.ui.JBUI
import java.awt.BorderLayout
import javax.swing.*
import javax.swing.event.TreeSelectionListener
import javax.swing.tree.*

/**
 * Searchable JTree of boards grouped by manufacturer, styled after PlatformIO's
 * board selector. Two-level hierarchy: Manufacturer → Board.
 *
 * Each board leaf shows:
 *   "Arduino Uno  (ATMEGA328P, 16MHz, ROM: 32K, RAM: 2K)"
 *
 * Usage:
 *   val panel = BoardTreePanel()
 *   panel.onBoardSelected = { board -> ... }
 *   panel.preselect("arduino_uno")
 */
class BoardTreePanel : JPanel(BorderLayout()) {

    var onBoardSelected: ((BoardEntry?) -> Unit)? = null

    private val root       = DefaultMutableTreeNode("Boards")
    private val treeModel  = DefaultTreeModel(root)
    private val tree       = JTree(treeModel)
    private val detailLabel = JLabel(" ").apply {
        font   = font.deriveFont(11f)
        border = JBUI.Borders.empty(4, 6)
        foreground = JBUI.CurrentTheme.Label.disabledForeground()
    }

    init {
        buildTree()
        configureTree()
        add(JBScrollPane(tree), BorderLayout.CENTER)
        add(detailLabel, BorderLayout.SOUTH)
    }

    private fun buildTree() {
        root.removeAllChildren()
        BoardRegistry.byManufacturer.forEach { (manufacturer, boards) ->
            val mNode = DefaultMutableTreeNode(manufacturer)
            boards.forEach { board -> mNode.add(DefaultMutableTreeNode(board)) }
            root.add(mNode)
        }
        treeModel.reload()
    }

    private fun configureTree() {
        tree.isRootVisible = false
        tree.showsRootHandles = true
        tree.selectionModel.selectionMode = TreeSelectionModel.SINGLE_TREE_SELECTION
        tree.cellRenderer = BoardTreeCellRenderer()

        // Expand all manufacturer groups by default
        for (i in 0 until tree.rowCount) tree.expandRow(i)

        // Speed-search across display name, chip, and manufacturer
        @Suppress("DEPRECATION")
        TreeSpeedSearch(tree) { path ->
            val node = path.lastPathComponent as? DefaultMutableTreeNode ?: return@TreeSpeedSearch ""
            when (val obj = node.userObject) {
                is BoardEntry -> "${obj.displayName} ${obj.chip} ${obj.manufacturer}"
                else          -> obj.toString()
            }
        }

        tree.addTreeSelectionListener(TreeSelectionListener { e ->
            val node  = e.path?.lastPathComponent as? DefaultMutableTreeNode
            val board = node?.userObject as? BoardEntry
            detailLabel.text = board?.summary ?: " "
            onBoardSelected?.invoke(board)
        })
    }

    fun preselect(boardId: String?) {
        if (boardId == null) return
        val entry = BoardRegistry.findById(boardId) ?: return

        fun findNode(parent: DefaultMutableTreeNode): DefaultMutableTreeNode? {
            if (parent.userObject == entry) return parent
            for (i in 0 until parent.childCount) {
                val result = findNode(parent.getChildAt(i) as DefaultMutableTreeNode)
                if (result != null) return result
            }
            return null
        }
        val node = findNode(root) ?: return
        val path = TreePath(treeModel.getPathToRoot(node))
        tree.selectionPath = path
        tree.scrollPathToVisible(path)
    }

    fun selectedBoard(): BoardEntry? {
        val node = tree.lastSelectedPathComponent as? DefaultMutableTreeNode ?: return null
        return node.userObject as? BoardEntry
    }
}

private class BoardTreeCellRenderer : ColoredTreeCellRenderer() {
    override fun customizeCellRenderer(
        tree: JTree, value: Any?, selected: Boolean,
        expanded: Boolean, leaf: Boolean, row: Int, hasFocus: Boolean
    ) {
        val node = value as? DefaultMutableTreeNode ?: return
        when (val obj = node.userObject) {
            is BoardEntry -> {
                icon = AllIcons.Nodes.Template  // chip/device icon
                append(obj.displayName, SimpleTextAttributes.REGULAR_ATTRIBUTES)
                append("  ${obj.treeDetail}", SimpleTextAttributes.GRAYED_SMALL_ATTRIBUTES)
            }
            is String -> {
                icon = AllIcons.Nodes.ModuleGroup
                append(obj, SimpleTextAttributes.REGULAR_BOLD_ATTRIBUTES)
            }
        }
    }
}
