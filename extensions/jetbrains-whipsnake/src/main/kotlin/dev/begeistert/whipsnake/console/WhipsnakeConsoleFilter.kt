package dev.begeistert.whipsnake.console

import com.intellij.execution.filters.ConsoleFilterProvider
import com.intellij.execution.filters.Filter
import com.intellij.execution.filters.HyperlinkInfo
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileEditor.OpenFileDescriptor
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem

/**
 * Parses GCC-style diagnostic lines emitted by pymcuc:
 *   src/main.py:16:1: error: CompileError: ...
 *
 * Creates a clickable hyperlink on the file:line:col portion that opens the
 * offending source file at the indicated line in the editor.
 */
class WhipsnakeConsoleFilter(private val project: Project) : Filter {

    companion object {
        // Matches: <file>:<line>:<col>: (error|warning|info): <message>
        private val DIAGNOSTIC_RE = Regex(
            """^(.+):(\d+):(\d+):\s+(error|warning|info):\s+(.+)$"""
        )
    }

    override fun applyFilter(line: String, entireLength: Int): Filter.Result? {
        val match = DIAGNOSTIC_RE.find(line.trimEnd()) ?: return null

        val filePath = match.groupValues[1]
        val lineNumber = match.groupValues[2].toIntOrNull()?.minus(1) ?: 0  // 0-based
        val colNumber = match.groupValues[3].toIntOrNull()?.minus(1) ?: 0   // 0-based

        // Hyperlink covers the "file:line:col" portion (indices into `line`)
        val linkStart = entireLength - line.length
        val linkEnd = linkStart + match.range.last + 1

        val hyperlinkInfo = HyperlinkInfo { proj ->
            val basePath = proj.basePath ?: return@HyperlinkInfo
            // Try absolute path first, then relative to base
            val vf = LocalFileSystem.getInstance().findFileByPath(filePath)
                ?: LocalFileSystem.getInstance().findFileByPath("$basePath/$filePath")
                ?: return@HyperlinkInfo

            val descriptor = OpenFileDescriptor(proj, vf, lineNumber, colNumber)
            FileEditorManager.getInstance(proj).openTextEditor(descriptor, true)
        }

        return Filter.Result(linkStart, linkEnd, hyperlinkInfo)
    }
}

/**
 * Provides [WhipsnakeConsoleFilter] for all run/debug consoles in a project.
 */
class WhipsnakeConsoleFilterProvider : ConsoleFilterProvider {
    override fun getDefaultFilters(project: Project): Array<Filter> =
        arrayOf(WhipsnakeConsoleFilter(project))
}
