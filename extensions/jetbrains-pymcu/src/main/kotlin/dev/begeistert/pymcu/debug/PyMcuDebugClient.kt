package dev.begeistert.pymcu.debug

import com.intellij.openapi.diagnostic.Logger
import com.intellij.openapi.fileTypes.PlainTextFileType
import com.intellij.testFramework.LightVirtualFile
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.PrintWriter
import java.net.Socket
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

data class FrameInfo(val file: String, val line: Int, val pc: Int)

data class StoppedEvent(
    val reason: String,
    val file: String,
    val line: Int,
    val pc: Int,
    val frames: List<FrameInfo> = emptyList()
)

/**
 * Manages the TCP connection to the pymcuc-avr-debugserver process.
 * A background thread reads server events; send() writes commands.
 */
class PyMcuDebugClient(
    private val port: Int,
    private val onStopped: (StoppedEvent) -> Unit,
    private val onTerminated: () -> Unit
) {
    private val log = Logger.getInstance(PyMcuDebugClient::class.java)

    private var socket: Socket? = null
    private var writer: PrintWriter? = null
    private var readerThread: Thread? = null

    private val readyLatch = CountDownLatch(1)
    private val pendingRegsCallback = AtomicReference<((Map<String, Int>) -> Unit)?>(null)
    // Queue of pending memory callbacks — FIFO so responses are matched in order.
    // Using a queue rather than a single AtomicReference allows the variables panel
    // and the peripherals panel to both issue getMemory requests concurrently.
    private val pendingMemoryCallbacks = java.util.concurrent.ConcurrentLinkedQueue<(Int, ByteArray) -> Unit>()
    private val pendingMessages = java.util.concurrent.CopyOnWriteArrayList<String>()

    /** Loaded after build; maps (file, line) → variable name → register. */
    @Volatile
    var varMap: VarMap? = null

    /**
     * Tracks the last-seen value for each variable (keyed by full varName like "fibonacci.a").
     * Used to highlight variables whose value changed since the previous stop.
     * Reset on each new session launch.
     */
    val previousValues: java.util.concurrent.ConcurrentHashMap<String, Int> = java.util.concurrent.ConcurrentHashMap()

    /**
     * Source file of the current top stack frame. Empty string means we're stopped inside
     * code that has no Python source mapping (stdlib, runtime stubs → disassembly mode).
     */
    @Volatile
    var currentTopFrameFile: String = ""

    // ── Disassembly ──────────────────────────────────────────────────────────

    /**
     * In-memory virtual file populated once after session launch with the full
     * disassembly of the loaded program. Used as a synthetic "source file" when
     * the PC is in code without a Python source mapping (stdlib functions, runtime).
     */
    @Volatile
    var disasmVf: LightVirtualFile? = null

    /**
     * Maps AVR word address → 0-based line index in [disasmVf].
     * Built at the same time as [disasmVf].
     */
    @Volatile
    var disasmPcToLine: Map<Int, Int> = emptyMap()

    private val pendingDisasmCallback = AtomicReference<((LightVirtualFile, Map<Int, Int>) -> Unit)?>(null)

    /** Connect and start the background reader thread. */
    fun connect() {
        log.info("PyMCU[client] connecting to 127.0.0.1:$port")
        val s = Socket("127.0.0.1", port)
        socket = s
        val w = PrintWriter(s.getOutputStream(), true)
        writer = w
        log.info("PyMCU[client] connected, flushing ${pendingMessages.size} queued messages")
        val queued = pendingMessages.toList(); pendingMessages.clear()
        queued.forEach { msg -> log.info("PyMCU[client] → (flush) $msg"); w.println(msg) }
        log.info("PyMCU[client] starting reader thread")
        readerThread = Thread({ readLoop(s) }, "pymcu-debug-reader").also {
            it.isDaemon = true
            it.start()
        }
    }

    private fun readLoop(s: Socket) {
        log.info("PyMCU[client] reader thread started")
        try {
            BufferedReader(InputStreamReader(s.getInputStream())).use { reader ->
                var line: String?
                while (reader.readLine().also { line = it } != null) {
                    val msg = line ?: continue
                    log.info("PyMCU[client] ← $msg")
                    dispatch(msg)
                }
            }
        } catch (e: Exception) {
            log.warn("PyMCU[client] reader thread ended: ${e.message}")
        } finally {
            log.info("PyMCU[client] reader thread exiting, calling onTerminated")
            onTerminated()
        }
    }

    private fun dispatch(json: String) {
        val type = SimpleJson.getString(json, "type") ?: run {
            log.warn("PyMCU[client] received message with no 'type' field: $json")
            return
        }
        when (type) {
            "stopped" -> {
                val reason = SimpleJson.getString(json, "reason") ?: "breakpoint"
                val file   = SimpleJson.getString(json, "file")   ?: ""
                val line   = SimpleJson.getInt(json,    "line")   ?: 0
                val pc     = SimpleJson.getInt(json,    "pc")     ?: 0
                val frames = SimpleJson.getFrameArray(json, "frames")
                log.info("PyMCU[client] STOPPED reason=$reason file=$file line=$line pc=$pc frames=${frames.size}")
                // Track the top frame's source file so callers can detect disassembly mode.
                currentTopFrameFile = frames.firstOrNull()?.file ?: file
                onStopped(StoppedEvent(reason, file, line, pc, frames))
            }
            "registers" -> {
                val data = SimpleJson.getIntMap(json, "data")
                if (data != null) {
                    log.info("PyMCU[client] REGISTERS received: ${data.size} entries")
                    pendingRegsCallback.getAndSet(null)?.invoke(data)
                } else {
                    log.warn("PyMCU[client] REGISTERS message missing 'data' map")
                }
            }
            "memory" -> {
                val addr  = SimpleJson.getInt(json, "address") ?: 0
                val bytes = SimpleJson.getByteArray(json, "data")
                if (bytes != null) {
                    log.info("PyMCU[client] MEMORY received: addr=0x${addr.toString(16)} len=${bytes.size}")
                    val cb = pendingMemoryCallbacks.poll()
                    if (cb != null) {
                        cb.invoke(addr, bytes)
                    } else {
                        log.warn("PyMCU[client] MEMORY received but no pending callback (addr=0x${addr.toString(16)})")
                    }
                } else {
                    log.warn("PyMCU[client] MEMORY message missing 'data' array")
                }
            }
            "ready"      -> {
                log.info("PyMCU[client] server is READY — releasing latch")
                readyLatch.countDown()
            }
            "disassembly" -> handleDisassembly(json)
            "terminated" -> {
                log.info("PyMCU[client] server TERMINATED")
                onTerminated()
            }
            "error" -> log.warn("PyMCU[client] server ERROR: ${SimpleJson.getString(json, "message")}")
            else -> log.info("PyMCU[client] ignored message type=$type")
        }
    }

    /** Request AVR register snapshot; invokes [cb] on the reader thread when received. */
    fun requestRegisters(cb: (Map<String, Int>) -> Unit) {
        pendingRegsCallback.set(cb)
        send("type" to "getRegisters")
    }

    /**
     * Request a memory read at [address] (absolute AVR data-space address) for [length] bytes.
     * Invokes [cb] on the reader thread when received.
     * Multiple requests can be queued; responses are dispatched FIFO.
     */
    fun requestMemory(address: Int, length: Int, cb: (Int, ByteArray) -> Unit) {
        pendingMemoryCallbacks.add(cb)
        send("type" to "getMemory", "address" to address, "length" to length)
    }

    fun send(vararg pairs: Pair<String, Any?>) {
        val sb = StringBuilder("{")
        pairs.forEachIndexed { i, (k, v) ->
            if (i > 0) sb.append(',')
            sb.append('"').append(k).append('"').append(':')
            when (v) {
                is String  -> sb.append('"').append(v.replace("\"", "\\\"")).append('"')
                is List<*> -> sb.append('[').append(v.joinToString(",")).append(']')
                null       -> sb.append("null")
                else       -> sb.append(v)
            }
        }
        sb.append('}')
        val msg = sb.toString()
        val w = writer
        if (w != null) {
            log.info("PyMCU[client] → $msg")
            w.println(msg)
        } else {
            log.info("PyMCU[client] queued (not connected yet) → $msg")
            pendingMessages.add(msg)
        }
    }

    fun close() {
        log.info("PyMCU[client] closing socket")
        try { socket?.close() } catch (_: Exception) {}
    }

    /**
     * Requests a full program disassembly from the server. The response is built into
     * a [LightVirtualFile] and a pc→line map, then [cb] is called on the reader thread.
     * If disassembly was already loaded, [cb] is called immediately with the cached result.
     */
    fun requestProgramDisassembly(cb: (LightVirtualFile, Map<Int, Int>) -> Unit) {
        val cached = disasmVf
        if (cached != null) {
            cb(cached, disasmPcToLine)
            return
        }
        pendingDisasmCallback.set(cb)
        send("type" to "getProgramDisassembly")
    }

    private fun handleDisassembly(json: String) {
        // Parse instructions array: [{"pc":N,"m":"MNEMONIC","s":"file:line"},...}]
        val instrRe = Regex("""\{"pc":(\d+),"m":"([^"]*)","s":"([^"]*)"\}""")
        val instrs  = instrRe.findAll(json).map { m ->
            Triple(m.groupValues[1].toInt(), m.groupValues[2], m.groupValues[3])
        }.toList()

        if (instrs.isEmpty()) {
            log.warn("PyMCU[client] disassembly response contained 0 instructions")
            return
        }

        // Build the text content and pc→line map.
        val sb = StringBuilder()
        sb.appendLine("; AVR Disassembly — PyMCU / ATmega328P")
        sb.appendLine("; Stepping into library code? Use Step Instruction (→|) to advance one AVR opcode.")
        sb.appendLine(";")
        val pcToLine = mutableMapOf<Int, Int>()
        var lineIdx  = 3  // 0-based; lines 0-2 are the header above

        var lastSrc = ""
        for ((pc, mnemonic, source) in instrs) {
            // Emit source annotation when it changes (groups instructions by Python line).
            if (source.isNotEmpty() && source != lastSrc) {
                sb.appendLine("; ── $source ──────────────────────────")
                lineIdx++
                lastSrc = source
            } else if (source.isEmpty() && lastSrc.isNotEmpty()) {
                sb.appendLine("; ── <library / runtime> ────────────────")
                lineIdx++
                lastSrc = ""
            }
            // Unescape mnemonic (server escapes backslash and quote).
            val mn = mnemonic.replace("\\\\", "\\").replace("\\\"", "\"")
            sb.appendLine("  0x%04X  %s".format(pc, mn))
            pcToLine[pc] = lineIdx
            lineIdx++
        }

        val text = sb.toString()
        val vf   = LightVirtualFile("AVR Disassembly.avrdisasm", PlainTextFileType.INSTANCE, text)
        vf.isWritable = false
        disasmVf      = vf
        disasmPcToLine = pcToLine
        log.info("PyMCU[client] disassembly ready: ${instrs.size} instructions, ${pcToLine.size} pc→line entries")

        pendingDisasmCallback.getAndSet(null)?.invoke(vf, pcToLine)
    }

    /** Blocks until the server sends {"type":"ready"}, or throws after [timeoutMs] ms. */
    fun waitForReady(timeoutMs: Long = 8_000) {
        log.info("PyMCU[client] waiting for 'ready' (timeout ${timeoutMs}ms)")
        if (!readyLatch.await(timeoutMs, TimeUnit.MILLISECONDS))
            throw IllegalStateException("pymcuc-avr-debugserver did not send 'ready' within ${timeoutMs}ms")
        log.info("PyMCU[client] 'ready' received OK")
    }
}

/** Minimal regex-based JSON field reader (no deps needed for simple flat objects). */
internal object SimpleJson {
    private val stringRe = Regex(""""([^"]+)"\s*:\s*"([^"]*)"""")
    private val intRe    = Regex(""""([^"]+)"\s*:\s*(-?\d+)""")

    fun getString(json: String, key: String): String? =
        stringRe.findAll(json).firstOrNull { it.groupValues[1] == key }?.groupValues?.get(2)

    fun getInt(json: String, key: String): Int? =
        intRe.findAll(json).firstOrNull { it.groupValues[1] == key }?.groupValues?.get(2)?.toIntOrNull()

    fun getIntMap(json: String, key: String): Map<String, Int>? {
        val objRe = Regex(""""${Regex.escape(key)}"\s*:\s*\{([^}]+)\}""")
        val inner = objRe.find(json)?.groupValues?.get(1) ?: return null
        return Regex(""""([^"]+)"\s*:\s*(-?\d+)""")
            .findAll(inner)
            .associate { it.groupValues[1] to it.groupValues[2].toInt() }
    }

    fun getStringMap(json: String, key: String): Map<String, String>? {
        val objRe = Regex(""""${Regex.escape(key)}"\s*:\s*\{([^}]+)\}""")
        val inner = objRe.find(json)?.groupValues?.get(1) ?: return null
        return Regex(""""([^"]+)"\s*:\s*"([^"]*)"""")
            .findAll(inner)
            .associate { it.groupValues[1] to it.groupValues[2] }
    }

    /**
     * Parses a JSON string array `"key":["a","b",...]` and returns it as a List<String>.
     * Returns an empty list if the key is absent or the array is malformed.
     */
    fun getStringArray(json: String, key: String): List<String> {
        val arrayRe = Regex(""""${Regex.escape(key)}"\s*:\s*\[([^\]]*)\]""")
        val inner   = arrayRe.find(json)?.groupValues?.get(1) ?: return emptyList()
        val trimmed = inner.trim()
        if (trimmed.isEmpty()) return emptyList()
        return Regex(""""([^"]*)"""").findAll(trimmed).map { it.groupValues[1] }.toList()
    }

    /**
     * Returns null if the key is absent or the array is malformed.
     */
    fun getByteArray(json: String, key: String): ByteArray? {
        val arrayRe = Regex(""""${Regex.escape(key)}"\s*:\s*\[([^\]]*)\]""")
        val inner   = arrayRe.find(json)?.groupValues?.get(1) ?: return null
        val nums    = inner.trim()
        if (nums.isEmpty()) return ByteArray(0)
        return nums.split(',').map { it.trim().toIntOrNull() ?: return null }
            .map { it.toByte() }.toByteArray()
    }

    /**
     * Parses a JSON array of frame objects: `"key":[{"file":"...","line":N,"pc":M},...]`.
     * Returns an empty list if the key is absent or the array is malformed.
     */
    fun getFrameArray(json: String, key: String): List<FrameInfo> {
        val arrayRe = Regex(""""${Regex.escape(key)}"\s*:\s*\[([^\]]*)\]""")
        val inner   = arrayRe.find(json)?.groupValues?.get(1) ?: return emptyList()
        val objRe   = Regex("""\{[^}]+\}""")
        return objRe.findAll(inner).mapNotNull { m ->
            val obj  = m.value
            val file = getString(obj, "file") ?: return@mapNotNull null
            val line = getInt(obj, "line")    ?: return@mapNotNull null
            val pc   = getInt(obj, "pc")      ?: return@mapNotNull null
            FrameInfo(file, line, pc)
        }.toList()
    }
}
