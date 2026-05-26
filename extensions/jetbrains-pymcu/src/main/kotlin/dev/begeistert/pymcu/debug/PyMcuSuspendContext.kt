package dev.begeistert.pymcu.debug

import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.xdebugger.XDebugSession
import com.intellij.xdebugger.XSourcePosition
import com.intellij.xdebugger.evaluation.XDebuggerEvaluator
import com.intellij.xdebugger.frame.XCompositeNode
import com.intellij.xdebugger.frame.XExecutionStack
import com.intellij.xdebugger.frame.XNamedValue
import com.intellij.xdebugger.frame.XStackFrame
import com.intellij.xdebugger.frame.XSuspendContext
import com.intellij.xdebugger.frame.XValueChildrenList
import com.intellij.xdebugger.frame.XValueNode
import com.intellij.xdebugger.frame.XValuePlace
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.xdebugger.frame.presentation.XValuePresentation
import com.intellij.xdebugger.impl.XSourcePositionImpl

class PyMcuSuspendContext(
    private val session: XDebugSession,
    private val event: StoppedEvent,
    private val client: PyMcuDebugClient
) : XSuspendContext() {

    private val log = Logger.getInstance(PyMcuSuspendContext::class.java)

    init {
        log.info("PyMCU[suspend] created for event: $event (${event.frames.size} frames in stack)")
    }

    private val stack = PyMcuExecutionStack(session.project, event, client)

    override fun getActiveExecutionStack(): XExecutionStack = stack
    override fun getExecutionStacks(): Array<XExecutionStack> = arrayOf(stack)
}

private class PyMcuExecutionStack(
    project: Project,
    event: StoppedEvent,
    client: PyMcuDebugClient
) : XExecutionStack("PyMCU") {

    private val frames: List<XStackFrame> = buildFrameList(project, event, client)

    private fun buildFrameList(
        project: Project,
        event: StoppedEvent,
        client: PyMcuDebugClient
    ): List<XStackFrame> {
        // Use the server-provided frames array if present; fall back to single-frame mode.
        val infos = event.frames.ifEmpty {
            listOf(FrameInfo(event.file, event.line, event.pc))
        }
        return infos.mapIndexed { idx, fi ->
            PyMcuStackFrame(project, fi.file, fi.line, fi.pc, client, isTopFrame = idx == 0)
        }
    }

    override fun getTopFrame(): XStackFrame? = frames.firstOrNull()

    override fun computeStackFrames(firstFrameIndex: Int, container: XExecutionStack.XStackFrameContainer?) {
        val slice = if (firstFrameIndex < frames.size) frames.subList(firstFrameIndex, frames.size) else emptyList()
        container?.addStackFrames(slice, true)
    }
}

class PyMcuStackFrame(
    private val project: Project,
    private val file: String,
    private val line: Int,
    private val pc: Int,
    private val client: PyMcuDebugClient,
    private val isTopFrame: Boolean = true
) : XStackFrame() {

    private val log = Logger.getInstance(PyMcuStackFrame::class.java)

    override fun getSourcePosition(): XSourcePosition? {
        log.info("PyMCU[frame] getSourcePosition: file='$file' line=$line isTop=$isTopFrame")

        if (file.isEmpty()) {
            // PC is in code with no Python source mapping (stdlib, runtime).
            // Navigate to the disassembly virtual file at the line for this PC.
            val vf      = client.disasmVf
            val lineIdx = client.disasmPcToLine[pc]
            if (vf != null && lineIdx != null) {
                log.info("PyMCU[frame] navigating to disassembly at line $lineIdx (pc=0x${pc.toString(16)})")
                return XSourcePositionImpl.create(vf, lineIdx)
            }
            log.warn("PyMCU[frame] file is empty — disassembly not yet loaded or pc=0x${pc.toString(16)} not in map")
            return null
        }
        val basePath = project.basePath ?: run {
            log.warn("PyMCU[frame] project.basePath is null")
            return null
        }

        val candidates = listOf(
            "$basePath/${file}",
            "$basePath/src/${file}"
        )
        log.info("PyMCU[frame] probing paths: $candidates")

        val vf = candidates.firstNotNullOfOrNull { path ->
            LocalFileSystem.getInstance().findFileByPath(path).also { vf ->
                if (vf != null) log.info("PyMCU[frame] resolved VirtualFile: $path")
                else log.info("PyMCU[frame] not found: $path")
            }
        }

        if (vf == null) {
            log.warn("PyMCU[frame] could not find VirtualFile for '$file' — no source highlight")
            return null
        }

        val pos = XSourcePositionImpl.create(vf, line - 1)
        log.info("PyMCU[frame] XSourcePosition: file=${vf.path} line=${line - 1} (0-based)")
        return pos
    }

    override fun computeChildren(node: XCompositeNode) {
        if (!isTopFrame) {
            // Caller frames: AVR has no automatic register save, so register values reflect
            // only the current frame. Show an empty variable list for caller frames.
            log.info("PyMCU[frame] computeChildren: caller frame ($file:$line) — skipping registers")
            node.addChildren(XValueChildrenList(), true)
            return
        }

        // If we're in disassembly mode (no Python source), show AVR CPU registers.
        if (file.isEmpty()) {
            client.requestRegisters { regs ->
                val list = XValueChildrenList()
                list.add(RegVal("PC",   pc,                   byteWidth = 2, isHex = true))
                for (i in 0..31) {
                    val v = regs["R$i"] ?: 0
                    list.add(RegVal("R$i", v))
                }
                list.add(RegVal("SP",   regs["SP"]   ?: 0, byteWidth = 2, isHex = true))
                list.add(SregVal(regs["SREG"] ?: 0))
                node.addChildren(list, true)
            }
            return
        }

        client.requestRegisters { regs ->
            val list   = XValueChildrenList()
            val varMap = client.varMap

            if (varMap == null) {
                log.warn("PyMCU[frame] varmap is null — no variables available")
                node.addChildren(list, true)
                return@requestRegisters
            }

            log.info("PyMCU[frame] varmap has ${varMap.scopes.size} scope(s):")
            varMap.scopes.forEach { s ->
                log.info("PyMCU[frame]   scope: fn=${s.function} file=${s.file} " +
                         "startLine=${s.startLine} vars=[${s.vars.keys.joinToString()}] " +
                         "stackVars=[${s.stackVars.keys.joinToString()}]")
            }

            val scope = varMap.getScope(file, line)
            if (scope == null) {
                log.warn("PyMCU[frame] no varmap scope matched for file=$file line=$line — variables cannot be shown")
                node.addChildren(list, true)
                return@requestRegisters
            }

            val prefix = "${scope.function}."
            log.info("PyMCU[frame] matched scope '${scope.function}' (startLine=${scope.startLine}) for $file:$line")

            // On fresh function entry, clear seen-state for all variables in this scope so
            // a re-entry (second call to the same function) starts visibility from scratch.
            if (line == scope.startLine) {
                (scope.vars.keys + scope.stackVars.keys).forEach { client.previousValues.remove(it) }
            }

            // --- Register-allocated variables ---
            for ((varName, reg) in scope.vars) {
                val declLine = scope.varLines[varName] ?: scope.startLine
                val inScope  = varName.startsWith(prefix)
                log.info("PyMCU[frame]   reg var '$varName' → $reg (declLine=$declLine, inScope=$inScope, currentLine=$line)")
                // Skip compiler-internal variables (declLine before function start = wrong attribution).
                // Parameters show from first line of function; locals show after declaration,
                // but remain visible on subsequent loop iterations ("sticky once seen").
                val isParam     = varName in scope.params
                val alreadySeen = client.previousValues.containsKey(varName)
                val skipByLine  = if (isParam) line < declLine else (!alreadySeen && line <= declLine)
                if (!inScope || declLine < scope.startLine || skipByLine) continue
                val displayName = varName.removePrefix(prefix)
                // INT16 uses a register pair: reg (lo) + reg+1 (hi).
                val lo      = regs[reg] ?: 0
                val hi      = regs[regPlusOne(reg)] ?: 0
                val rawValue = (hi shl 8) or (lo and 0xFF)
                val raw    = rawValue and 0xFFFF
                val signed = if (raw >= 0x8000) raw - 0x10000 else raw
                val prevVal = client.previousValues[varName]
                val changed = prevVal != null && prevVal != signed
                client.previousValues[varName] = signed
                list.add(NamedVarVal(displayName, rawValue, changed))
            }

            // --- Stack-spilled variables ---
            val relevantStackVars = scope.stackVars.filter { (n, _) -> n.startsWith(prefix) }
            if (relevantStackVars.isEmpty()) {
                if (list.size() == 0)
                    log.warn("PyMCU[frame] 0 variables visible — varmap completeness issue in compiler (fn=${scope.function})")
                node.addChildren(list, true)
                return@requestRegisters
            }

            // Read a single contiguous SRAM window covering all spilled vars in this scope.
            val minAddr = relevantStackVars.values.min()
            val maxAddr = relevantStackVars.values.max() + 2   // +2 for max INT16 var size
            val length  = maxAddr - minAddr
            log.info("PyMCU[frame] requesting memory: addr=0x${minAddr.toString(16)} len=$length for ${relevantStackVars.size} stack vars")

            client.requestMemory(minAddr, length) { _, bytes ->
                for ((varName, addr) in relevantStackVars) {
                    val declLine = scope.stackVarLines[varName] ?: scope.startLine
                    val inScope  = varName.startsWith(prefix)
                    log.info("PyMCU[frame]   stack var '$varName' → 0x${addr.toString(16)} (declLine=$declLine, inScope=$inScope, currentLine=$line)")
                    // Same visibility rules as register vars above.
                    val isParam     = varName in scope.params
                    val alreadySeen = client.previousValues.containsKey(varName)
                    val skipByLine  = if (isParam) line < declLine else (!alreadySeen && line <= declLine)
                    if (!inScope || declLine < scope.startLine || skipByLine) continue
                    val offset = addr - minAddr
                    // Read 2 bytes little-endian (INT16). For 1-byte vars the high byte is 0.
                    val lo    = if (offset < bytes.size)     (bytes[offset].toInt() and 0xFF) else 0
                    val hi    = if (offset + 1 < bytes.size) (bytes[offset + 1].toInt() and 0xFF) else 0
                    val rawValue = (hi shl 8) or lo
                    val displayName = varName.removePrefix(prefix)
                    val raw    = rawValue and 0xFFFF
                    val signed = if (raw >= 0x8000) raw - 0x10000 else raw
                    val prevVal = client.previousValues[varName]
                    val changed = prevVal != null && prevVal != signed
                    client.previousValues[varName] = signed
                    list.add(NamedVarVal(displayName, rawValue, changed))
                }
                if (list.size() == 0)
                    log.warn("PyMCU[frame] 0 variables visible after memory read — varmap completeness issue (fn=${scope.function})")
                node.addChildren(list, true)
            }
        }
    }

    override fun getEvaluator(): XDebuggerEvaluator? =
        if (isTopFrame) VarEvaluator(client, file, line) else null
}

/** Returns the next register name (e.g. "R4" → "R5"), used for reading INT16 high byte. */
private fun regPlusOne(reg: String): String {
    val n = reg.removePrefix("R").toIntOrNull() ?: return reg
    return "R${n + 1}"
}

// Shows a named variable with its 16-bit signed integer value.
// If `changed` is true, renders the value with the IDE's standard "changed value" highlight color.
private val CHANGED_VALUE_KEY = TextAttributesKey.createTextAttributesKey("CHANGED_DEBUGGER_VALUE")

private class NamedVarVal(name: String, private val value: Int, private val changed: Boolean = false) : XNamedValue(name) {
    override fun computePresentation(node: XValueNode, place: XValuePlace) {
        val raw    = value and 0xFFFF
        val signed = if (raw >= 0x8000) raw - 0x10000 else raw
        val text   = "$signed  (0x%04X)".format(raw)
        if (changed) {
            node.setPresentation(null, object : XValuePresentation() {
                override fun getType() = "int"
                override fun renderValue(renderer: XValueTextRenderer) {
                    renderer.renderValue(text, CHANGED_VALUE_KEY)
                }
            }, false)
        } else {
            node.setPresentation(null, "int", text, false)
        }
    }
}

private class RegVal(
    name: String,
    private val value: Int,
    private val byteWidth: Int = 1,
    private val isHex: Boolean = false
) : XNamedValue(name) {
    override fun computePresentation(node: XValueNode, place: XValuePlace) {
        val masked  = if (byteWidth == 1) value and 0xFF else value and 0xFFFF
        val hex     = if (byteWidth == 1) "0x%02X".format(masked) else "0x%04X".format(masked)
        val display = if (isHex) hex else "$hex ($masked)"
        node.setPresentation(null, "uint${byteWidth * 8}", display, false)
    }
}

private class SregVal(private val sreg: Int) : XNamedValue("SREG") {
    override fun computePresentation(node: XValueNode, place: XValuePlace) {
        val v     = sreg and 0xFF
        val flags = "I:${(v shr 7) and 1} T:${(v shr 6) and 1} H:${(v shr 5) and 1} " +
                    "S:${(v shr 4) and 1} V:${(v shr 3) and 1} N:${(v shr 2) and 1} " +
                    "Z:${(v shr 1) and 1} C:${v and 1}"
        node.setPresentation(null, "uint8", "0x%02X  [%s]".format(v, flags), false)
    }
}

// Evaluator: resolves named vars first (register or stack), then falls back to register names.
private class VarEvaluator(
    private val client: PyMcuDebugClient,
    private val file: String,
    private val line: Int
) : XDebuggerEvaluator() {
    override fun evaluate(
        expression: String,
        callback: XEvaluationCallback,
        expressionPosition: XSourcePosition?
    ) {
        val expr = expression.trim()
        client.requestRegisters { regs ->
            val scope = client.varMap?.getScope(file, line)
            if (scope != null) {
                val prefix  = "${scope.function}."
                val fullKey = if (expr.contains('.')) expr else "$prefix$expr"

                // 1a. Register-allocated variable?
                val reg = scope.vars[fullKey] ?: scope.vars[expr]
                if (reg != null) {
                    val lo = regs[reg] ?: 0
                    val hi = regs[regPlusOne(reg)] ?: 0
                    callback.evaluated(NamedVarVal(expr, (hi shl 8) or (lo and 0xFF)))
                    return@requestRegisters
                }

                // 1b. Stack-spilled variable?
                val addr = scope.stackVars[fullKey] ?: scope.stackVars[expr]
                if (addr != null) {
                    client.requestMemory(addr, 2) { _, bytes ->
                        val lo = if (bytes.isNotEmpty()) (bytes[0].toInt() and 0xFF) else 0
                        val hi = if (bytes.size > 1)    (bytes[1].toInt() and 0xFF) else 0
                        callback.evaluated(NamedVarVal(expr, (hi shl 8) or lo))
                    }
                    return@requestRegisters
                }
            }
            // 2. Fall back to direct register name.
            val regName = expr.uppercase()
            val value   = regs[regName]
            if (value != null) callback.evaluated(RegVal(regName, value))
            else callback.errorOccurred("Unknown variable or register: $expr")
        }
    }
}
