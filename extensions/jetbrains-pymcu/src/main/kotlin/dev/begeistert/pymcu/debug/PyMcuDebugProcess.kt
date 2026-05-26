// SPDX-License-Identifier: Apache-2.0
package dev.begeistert.pymcu.debug

import com.intellij.execution.ui.RunnerLayoutUi
import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.fileTypes.PlainTextFileType
import com.intellij.openapi.project.Project
import com.intellij.xdebugger.XDebugProcess
import com.intellij.xdebugger.XDebugSession
import com.intellij.xdebugger.XSourcePosition
import com.intellij.xdebugger.breakpoints.XBreakpointHandler
import com.intellij.xdebugger.evaluation.EvaluationMode
import com.intellij.xdebugger.evaluation.XDebuggerEditorsProvider
import com.intellij.xdebugger.XExpression
import com.intellij.xdebugger.frame.XSuspendContext
import com.intellij.xdebugger.ui.XDebugTabLayouter
import dev.begeistert.pymcu.config.PyMcuConfigReader

class PyMcuDebugProcess(
    session: XDebugSession,
    internal val client: PyMcuDebugClient,
    private val serverProcess: Process
) : XDebugProcess(session) {

    private val log = Logger.getInstance(PyMcuDebugProcess::class.java)

    // Created once in createTabLayouter(); refreshed on each stop.
    internal var peripheralsPanel: PyMcuPeripheralsPanel? = null

    private val breakpointHandler = run {
        val basePath   = session.project.basePath ?: ""
        val sourcesDir = PyMcuConfigReader.findConfig(session.project)?.sources ?: "src"
        log.info("PyMCU[process] creating breakpoint handler: basePath=$basePath sourcesDir=$sourcesDir")
        PyMcuLineBreakpointHandler(client, basePath, sourcesDir)
    }

    override fun getBreakpointHandlers(): Array<XBreakpointHandler<*>> = arrayOf(breakpointHandler)

    // XDebuggerEditorsProvider must return a valid object (never throw).
    override fun getEditorsProvider(): XDebuggerEditorsProvider = NoOpEditorsProvider

    override fun resume(context: XSuspendContext?) {
        log.info("PyMCU[process] resume() called")
        client.send("type" to "continue")
    }

    override fun startStepOver(context: XSuspendContext?) {
        log.info("PyMCU[process] startStepOver() called")
        client.send("type" to "stepOver")
    }

    override fun startStepInto(context: XSuspendContext?) {
        log.info("PyMCU[process] startStepInto() called")
        client.send("type" to "stepInto")
    }

    fun stepInstruction() {
        log.info("PyMCU[process] stepInstruction() called")
        client.send("type" to "stepInstruction")
    }

    override fun createTabLayouter(): XDebugTabLayouter {
        return object : XDebugTabLayouter() {
            override fun registerAdditionalContent(ui: RunnerLayoutUi) {
                val panel = PyMcuPeripheralsPanel(client)
                peripheralsPanel = panel
                val content = ui.createContent(
                    "PyMcuPeripherals", panel,
                    "Peripherals", null, null
                )
                content.isCloseable = false
                ui.addContent(content)
            }
        }
    }

    override fun startPausing() {
        log.info("PyMCU[process] startPausing() called")
        client.send("type" to "pause")
    }

    override fun stop() {
        log.info("PyMCU[process] stop() called — sending terminate and killing server")
        client.send("type" to "terminate")
        client.close()
        serverProcess.destroyForcibly()
    }

    /**
     * Called by PyMcuDebugRunner from a background thread after the server is ready.
     * Opens the TCP socket, sends the launch command, and waits for the server's
     * "ready" response before returning. The session starts PAUSED — the user must
     * click Resume to begin execution, which gives IntelliJ time to register all
     * breakpoints via PyMcuLineBreakpointHandler before the CPU starts running.
     */
    fun connectAndLaunch(hexFile: String, lineMapFile: String) {
        log.info("PyMCU[process] connectAndLaunch: hexFile=$hexFile lineMapFile=$lineMapFile")
        client.connect()
        log.info("PyMCU[process] sending launch command")
        client.send("type" to "launch", "hexFile" to hexFile, "lineMapFile" to lineMapFile)
        log.info("PyMCU[process] waiting for ready...")
        client.waitForReady()
        log.info("PyMCU[process] server ready — flushing pre-registered breakpoints")
        breakpointHandler.flushAll()
        log.info("PyMCU[process] breakpoints flushed — auto-continuing to start simulation")
        client.send("type" to "continue")
        // Request full program disassembly in background — used when stepping into
        // stdlib functions that have no Python source mapping.
        client.requestProgramDisassembly { _, _ ->
            log.info("PyMCU[process] program disassembly loaded")
        }
    }
}

// Minimal editors provider — required by XDebugger framework.
private object NoOpEditorsProvider : XDebuggerEditorsProvider() {
    override fun getFileType() = PlainTextFileType.INSTANCE

    override fun createDocument(
        project: Project,
        expression: XExpression,
        sourcePosition: XSourcePosition?,
        mode: EvaluationMode
    ) = EditorFactory.getInstance().createDocument(expression.expression)
}
