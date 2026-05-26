// SPDX-License-Identifier: Apache-2.0
package dev.begeistert.pymcu.debug

import com.intellij.execution.ExecutionException
import com.intellij.execution.configurations.RunProfile
import com.intellij.execution.configurations.RunProfileState
import com.intellij.execution.configurations.RunnerSettings
import com.intellij.execution.executors.DefaultDebugExecutor
import com.intellij.execution.runners.ExecutionEnvironment
import com.intellij.execution.runners.GenericProgramRunner
import com.intellij.execution.ui.RunContentDescriptor
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.Logger
import com.intellij.xdebugger.XDebugProcess
import com.intellij.xdebugger.XDebugProcessStarter
import com.intellij.xdebugger.XDebugSession
import com.intellij.xdebugger.XDebuggerManager
import dev.begeistert.pymcu.settings.PyMcuSettings
import java.nio.file.Path

@Suppress("UnstableApiUsage")
class PyMcuDebugRunner : GenericProgramRunner<RunnerSettings>() {

    private val log = Logger.getInstance(PyMcuDebugRunner::class.java)

    override fun getRunnerId(): String = "PyMcuDebugRunner"

    override fun canRun(executorId: String, profile: RunProfile): Boolean =
        executorId == DefaultDebugExecutor.EXECUTOR_ID && profile is PyMcuDebugConfiguration

    override fun doExecute(state: RunProfileState, environment: ExecutionEnvironment): RunContentDescriptor? {
        val config   = environment.runProfile as? PyMcuDebugConfiguration ?: return null
        val project  = environment.project
        val basePath = project.basePath ?: throw ExecutionException("No project base path.")
        val settings = PyMcuSettings.getInstance()

        // Step 1 — build firmware with --debug (emits linemap.json + varmap.json).
        log.info("PyMCU debug: running pymcu build --debug")
        val (hexFile, lineMapFile, varMapFile) = runBuildDebug(settings.executablePath, basePath)

        // Step 2 — locate the debug server binary.
        val serverBin = findDebugServerBinary(basePath)
            ?: throw ExecutionException(
                "pymcuc-avr-debugserver not found.\n" +
                "Build it: dotnet publish extensions/pymcu-avr/src/csharp/debugserver/"
            )

        // Step 3 — ensure the server binary is code-signed (required on macOS for native AOT
        //           binaries; ad-hoc signing is sufficient and safe to repeat on every launch).
        ensureSigned(serverBin)

        // Step 3b — kill any zombie from a previous session, then launch the debug server.
        log.info("PyMCU debug: killing any existing pymcuc-avr-debugserver on port ${config.serverPort}")
        runCatching {
            val killer = ProcessBuilder("sh", "-c", "lsof -ti :${config.serverPort} | xargs kill -9 2>/dev/null || true")
                .start()
            killer.waitFor()
            log.info("PyMCU debug: port cleanup done (exit=${killer.exitValue()})")
        }.onFailure { log.warn("PyMCU debug: port cleanup failed: ${it.message}") }
        Thread.sleep(200)

        log.info("PyMCU debug: starting server: $serverBin --port ${config.serverPort}")
        val serverProcess = ProcessBuilder(serverBin, "--port", config.serverPort.toString())
            .directory(java.io.File(basePath))
            .redirectErrorStream(true)
            .start()

        log.info("PyMCU debug: sleeping 600ms for server to bind port")
        Thread.sleep(600)

        if (!serverProcess.isAlive) {
            val out = serverProcess.inputStream.bufferedReader().readText()
            log.error("PyMCU debug: server exited immediately. output:\n$out")
            throw ExecutionException("pymcuc-avr-debugserver exited immediately:\n$out")
        }
        log.info("PyMCU debug: server process alive (pid=${serverProcess.pid()})")

        // Forward server stdout/stderr to idea.log so [DEBUGSERVER] lines are visible.
        Thread({
            serverProcess.inputStream.bufferedReader().use { r ->
                var line: String?
                while (r.readLine().also { line = it } != null)
                    log.info("DEBUGSERVER: ${line!!}")
            }
        }, "pymcu-debugserver-log").also { it.isDaemon = true; it.start() }

        // Step 4 — create the XDebugSession (must be called from the runner thread,
        //           which GenericProgramRunner calls via startRunProfile on a pooled thread).
        var debugProcess: PyMcuDebugProcess? = null

        val session = XDebuggerManager.getInstance(project).startSession(
            environment,
            object : XDebugProcessStarter() {
                override fun start(session: XDebugSession): XDebugProcess {
                    val client = PyMcuDebugClient(
                        port         = config.serverPort,
                        onStopped    = { event ->
                            // positionReached must be called from a background thread — XDebugger
                            // internally marshals to EDT. Calling it from invokeLater (EDT) causes
                            // the frames panel to not refresh on subsequent stops.
                            log.info("PyMCU debug: onStopped event: $event — calling positionReached")
                            if (!session.isStopped) {
                                session.positionReached(PyMcuSuspendContext(session, event, debugProcess!!.client))
                            } else {
                                log.warn("PyMCU debug: session already stopped, ignoring onStopped")
                            }
                        },
                        onTerminated = {
                            log.info("PyMCU debug: onTerminated called")
                            ApplicationManager.getApplication().invokeLater {
                                if (!session.isStopped) session.stop()
                            }
                        }
                    )
                    client.varMap = VarMap.load(varMapFile).also {
                        if (it != null) log.info("PyMCU debug: varmap loaded — ${it.scopes.size} scopes")
                        else log.warn("PyMCU debug: varmap not loaded from $varMapFile")
                    }
                    debugProcess = PyMcuDebugProcess(session, client, serverProcess)
                    return debugProcess!!
                }
            }
        )

        // Step 5 — connect to the server and send the launch command on a pooled thread,
        //           so the TCP handshake does not block the runner or the EDT.
        log.info("PyMCU debug: scheduling connectAndLaunch on pooled thread")
        ApplicationManager.getApplication().executeOnPooledThread {
            try {
                log.info("PyMCU debug: pooled thread sleeping 200ms for XDebugSession init")
                Thread.sleep(200)
                val dp = debugProcess
                if (dp == null) {
                    log.warn("PyMCU debug: debugProcess is null at launch time!")
                } else {
                    log.info("PyMCU debug: calling connectAndLaunch")
                    dp.connectAndLaunch(hexFile, lineMapFile)
                    log.info("PyMCU debug: connectAndLaunch returned — session is paused")
                }
            } catch (e: Exception) {
                log.error("PyMCU debug: connectAndLaunch failed: ${e.message}", e)
                ApplicationManager.getApplication().invokeLater {
                    if (!session.isStopped) session.stop()
                }
            }
        }

        return session.runContentDescriptor
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private fun runBuildDebug(pymcu: String, basePath: String): Triple<String, String, String> {
        val proc = ProcessBuilder(pymcu, "build", "--debug")
            .directory(java.io.File(basePath))
            .redirectErrorStream(true)
            .start()
        val output = proc.inputStream.bufferedReader().readText()
        val exit   = proc.waitFor()

        if (exit != 0) throw ExecutionException("pymcu build --debug failed (exit $exit):\n$output")

        val hexFile     = "$basePath/dist/firmware.hex"
        val lineMapFile = "$basePath/dist/_debug/linemap.json"
        val varMapFile  = "$basePath/dist/_debug/varmap.json"

        if (!java.io.File(hexFile).exists())
            throw ExecutionException("HEX file not found: $hexFile")
        if (!java.io.File(lineMapFile).exists())
            throw ExecutionException(
                "linemap.json not found: $lineMapFile\n" +
                "Make sure the AVR backend (pymcu-avr) is installed."
            )

        log.info("PyMCU debug: hex=$hexFile  linemap=$lineMapFile  varmap=$varMapFile")
        return Triple(hexFile, lineMapFile, varMapFile)
    }

    private fun findDebugServerBinary(basePath: String): String? {
        val candidates = buildList {
            // 1. On PATH
            runCatching {
                ProcessBuilder("which", "pymcuc-avr-debugserver")
                    .start().inputStream.bufferedReader().readText().trim().ifBlank { null }
            }.getOrNull()?.let { add(it) }

            // 2. Ask the venv Python where pymcu.backend.avr is installed.
            //    Works for both editable (path=…) and wheel installs regardless of
            //    where the repo lives on disk.
            findAvrModuleDir(basePath)?.let { avrDir ->
                // 2a. Alongside the pymcuc-avr binary in the same package directory.
                add("$avrDir/pymcuc-avr-debugserver")

                // 2b. Dev csharp build outputs — walk up to the pymcu-avr extension root.
                val extRoot = generateSequence(java.io.File(avrDir)) { it.parentFile }
                    .firstOrNull { it.name == "pymcu-avr" }
                    ?.absolutePath
                if (extRoot != null) {
                    add("$extRoot/src/csharp/debugserver/bin/Debug/net10.0/pymcuc-avr-debugserver")
                    add("$extRoot/src/csharp/debugserver/bin/Release/net10.0/osx-arm64/publish/pymcuc-avr-debugserver")
                    add("$extRoot/src/csharp/debugserver/bin/Release/net10.0/linux-x64/publish/pymcuc-avr-debugserver")
                }
            }
        }

        return candidates.firstOrNull { java.io.File(it).exists() }.also {
            if (it == null) log.warn("PyMCU debug: pymcuc-avr-debugserver not found in: $candidates")
            else log.info("PyMCU debug: using server at $it")
        }
    }

    /** Returns the directory of the installed pymcu.backend.avr package by querying the project venv. */
    private fun findAvrModuleDir(basePath: String): String? {
        val python = listOf("$basePath/.venv/bin/python3", "$basePath/.venv/bin/python")
            .firstOrNull { java.io.File(it).exists() } ?: return null
        return runCatching {
            ProcessBuilder(python, "-c",
                "import pymcu.backend.avr as m, pathlib; print(pathlib.Path(m.__file__).parent)")
                .start().inputStream.bufferedReader().readText().trim().ifBlank { null }
        }.getOrNull()
    }

    /**
     * On macOS, applies an ad-hoc code signature to [binaryPath] if needed.
     * Native AOT .NET binaries are unsigned by default and macOS kills them on launch.
     * Ad-hoc signing (`codesign -s -`) is a no-op when the binary is already signed.
     */
    private fun ensureSigned(binaryPath: String) {
        if (!System.getProperty("os.name", "").lowercase().contains("mac")) return
        log.info("PyMCU debug: applying ad-hoc codesign to $binaryPath")
        runCatching {
            val proc = ProcessBuilder("codesign", "-s", "-", "--force", binaryPath)
                .redirectErrorStream(true)
                .start()
            val out  = proc.inputStream.bufferedReader().readText()
            val exit = proc.waitFor()
            if (exit == 0) log.info("PyMCU debug: codesign succeeded")
            else           log.warn("PyMCU debug: codesign exit=$exit output=$out")
        }.onFailure { log.warn("PyMCU debug: codesign failed (codesign not available?): ${it.message}") }
    }
}
