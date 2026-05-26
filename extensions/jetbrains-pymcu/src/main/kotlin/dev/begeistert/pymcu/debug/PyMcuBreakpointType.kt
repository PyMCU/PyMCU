package dev.begeistert.pymcu.debug

import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.xdebugger.breakpoints.XBreakpointProperties
import com.intellij.xdebugger.breakpoints.XLineBreakpointType
import dev.begeistert.pymcu.config.PyMcuConfigReader

/**
 * Line breakpoint type for PyMCU projects.
 * Only visible in Python files belonging to a project that has a [tool.pymcu] config.
 */
class PyMcuBreakpointType : XLineBreakpointType<XBreakpointProperties<*>>(
    ID, "PyMCU Emulator Breakpoints"
) {
    companion object {
        const val ID = "python-pymcu-line"
    }

    override fun createBreakpointProperties(file: VirtualFile, line: Int): XBreakpointProperties<*>? = null

    override fun canPutAt(file: VirtualFile, line: Int, project: Project): Boolean {
        if (file.extension != "py") return false
        return PyMcuConfigReader.findConfig(project) != null
    }
}
