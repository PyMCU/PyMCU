package dev.begeistert.pymcu.debug

import com.intellij.openapi.diagnostic.Logger
import com.intellij.xdebugger.breakpoints.XBreakpointHandler
import com.intellij.xdebugger.breakpoints.XBreakpointProperties
import com.intellij.xdebugger.breakpoints.XLineBreakpoint
import com.jetbrains.python.debugger.PyLineBreakpointType

class PyMcuLineBreakpointHandler(
    private val client: PyMcuDebugClient,
    private val basePath: String,
    private val sourcesDir: String = "src"
) : XBreakpointHandler<XLineBreakpoint<XBreakpointProperties<*>>>(
    PyLineBreakpointType::class.java
) {
    private val log = Logger.getInstance(PyMcuLineBreakpointHandler::class.java)
    private val breakpoints = mutableMapOf<XLineBreakpoint<*>, Pair<String, Int>>()

    override fun registerBreakpoint(bp: XLineBreakpoint<XBreakpointProperties<*>>) {
        val rawFile = bp.sourcePosition?.file?.path
        val rawLine = bp.sourcePosition?.line

        log.info("PyMCU[bphandler] registerBreakpoint called: rawFile=$rawFile rawLine=$rawLine")

        val file = rawFile ?: run {
            log.warn("PyMCU[bphandler] sourcePosition.file is null — skipping")
            return
        }
        val line = (rawLine ?: run {
            log.warn("PyMCU[bphandler] sourcePosition.line is null — skipping")
            return
        }) + 1  // 1-based for the server

        // Convert absolute path → project-relative, then strip the sources dir prefix.
        // The linemap uses paths relative to the sources directory (e.g. "main.py"),
        // but the IDE gives us paths relative to the project root (e.g. "src/main.py").
        var relFile = if (file.startsWith(basePath)) file.removePrefix("$basePath/") else file
        log.info("PyMCU[bphandler] after basePath strip: relFile=$relFile  (basePath=$basePath)")
        val prefix = "$sourcesDir/"
        if (relFile.startsWith(prefix)) relFile = relFile.removePrefix(prefix)
        log.info("PyMCU[bphandler] after sourcesDir strip: relFile=$relFile  line=$line  (sourcesDir=$sourcesDir)")

        breakpoints[bp] = Pair(relFile, line)
        log.info("PyMCU[bphandler] registered ${breakpoints.size} breakpoint(s) total")
        sendAllBreakpoints(relFile)
    }

    override fun unregisterBreakpoint(bp: XLineBreakpoint<XBreakpointProperties<*>>, temporary: Boolean) {
        val entry = breakpoints.remove(bp)
        log.info("PyMCU[bphandler] unregisterBreakpoint: entry=$entry temporary=$temporary")
        val (file, _) = entry ?: return
        sendAllBreakpoints(file)
    }

    private fun sendAllBreakpoints(file: String) {
        val lines = breakpoints.values
            .filter { it.first == file }
            .map { it.second }
        log.info("PyMCU[bphandler] sendAllBreakpoints file=$file lines=$lines")
        client.send("type" to "setBreakpoints", "file" to file, "lines" to lines)
    }

    fun flushAll() {
        val files = breakpoints.values.map { it.first }.distinct()
        log.info("PyMCU[bphandler] flushAll: re-sending breakpoints for files=$files")
        files.forEach { sendAllBreakpoints(it) }
    }
}
