import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import * as cp from 'child_process';
import { DebugSession, type StoppedEvent, type FrameInfo } from './debugSession.js';
import { VarMap, regPlusOne } from './varMap.js';

// ── DAP message types (minimal subset needed) ────────────────────────────────

interface DapRequest  { seq: number; type: 'request';  command: string; arguments?: Record<string, unknown>; }
interface DapResponse { seq: number; type: 'response'; request_seq: number; success: boolean; command: string; body?: Record<string, unknown>; message?: string; }
interface DapEvent    { seq: number; type: 'event';    event: string; body?: Record<string, unknown>; }

// ── Inline DebugAdapter using VS Code's DebugAdapter API ─────────────────────

export class PyMcuDebugAdapterFactory implements vscode.DebugAdapterDescriptorFactory {
    createDebugAdapterDescriptor(
        _session: vscode.DebugSession,
        _executable: vscode.DebugAdapterExecutable | undefined
    ): vscode.ProviderResult<vscode.DebugAdapterDescriptor> {
        return new vscode.DebugAdapterInlineImplementation(new PyMcuDebugAdapter());
    }
}

class PyMcuDebugAdapter implements vscode.DebugAdapter {
    private readonly _onDidSendMessage = new vscode.EventEmitter<vscode.DebugProtocolMessage>();
    readonly onDidSendMessage = this._onDidSendMessage.event;

    private seq = 1;
    private session?: DebugSession;
    private serverProcess?: cp.ChildProcess;
    private workspaceRoot = '';
    private sourcesDir = 'src';

    // Breakpoints pending until server is ready: file → sorted line array
    private pendingBreakpoints = new Map<string, number[]>();
    private serverReady = false;

    // Last stopped event — used to answer stackTrace / scopes / variables requests
    private lastStoppedEvent?: StoppedEvent;
    // frameId → FrameInfo mapping (rebuilt on each stopped event)
    private frames: FrameInfo[] = [];

    handleMessage(message: vscode.DebugProtocolMessage): void {
        const req = message as DapRequest;
        void this.handleRequest(req);
    }

    private async handleRequest(req: DapRequest): Promise<void> {
        switch (req.command) {
            case 'initialize':       this.handleInitialize(req); break;
            case 'launch':           await this.handleLaunch(req); break;
            case 'setBreakpoints':   this.handleSetBreakpoints(req); break;
            case 'setExceptionBreakpoints': this.sendResponse(req, {}); break;
            case 'configurationDone': this.handleConfigurationDone(req); break;
            case 'continue':         this.handleContinue(req); break;
            case 'next':             this.handleNext(req); break;
            case 'stepIn':           this.handleStepIn(req); break;
            case 'pause':            this.handlePause(req); break;
            case 'stackTrace':       await this.handleStackTrace(req); break;
            case 'scopes':           this.handleScopes(req); break;
            case 'variables':        await this.handleVariables(req); break;
            case 'evaluate':         await this.handleEvaluate(req); break;
            case 'threads':          this.sendResponse(req, { threads: [{ id: 1, name: 'AVR' }] }); break;
            case 'disconnect':
            case 'terminate':        this.handleDisconnect(req); break;
            default:                 this.sendResponse(req, {}); break;
        }
    }

    private handleInitialize(req: DapRequest): void {
        this.sendResponse(req, {
            supportsStepInTargetsRequest:        false,
            supportsConfigurationDoneRequest:    true,
            supportsEvaluateForHovers:           true,
            supportsTerminateRequest:            true,
        });
        this.sendEvent('initialized');
    }

    private async handleLaunch(req: DapRequest): Promise<void> {
        const args = req.arguments ?? {};
        this.workspaceRoot = (args['workspaceRoot'] as string | undefined) ?? '';
        const config = vscode.workspace.getConfiguration('pymcu');
        const serverPort = config.get<number>('debugServerPort') ?? 4712;
        this.sourcesDir = (args['sourcesDir'] as string | undefined) ?? 'src';

        try {
            const executable = config.get<string>('executablePath') ?? 'pymcu';
            await this.runBuildDebug(executable);

            const serverBin = this.findDebugServerBinary();
            if (!serverBin) {
                throw new Error(
                    'pymcuc-avr-debugserver not found.\n' +
                    'Build it: dotnet publish extensions/pymcu-avr/src/csharp/debugserver/'
                );
            }

            this.ensureSigned(serverBin);
            this.killZombie(serverPort);

            await new Promise<void>(r => setTimeout(r, 200));

            this.serverProcess = cp.spawn(serverBin, ['--port', String(serverPort)], {
                cwd:   this.workspaceRoot,
                stdio: ['ignore', 'pipe', 'pipe'],
            });

            this.serverProcess.stdout?.on('data', (d: Buffer) => {
                this.sendEvent('output', { category: 'console', output: `[debugserver] ${d.toString()}` });
            });
            this.serverProcess.stderr?.on('data', (d: Buffer) => {
                this.sendEvent('output', { category: 'console', output: `[debugserver] ${d.toString()}` });
            });

            await new Promise<void>(r => setTimeout(r, 600));

            if (this.serverProcess.exitCode !== null) {
                throw new Error('pymcuc-avr-debugserver exited immediately.');
            }

            this.session = new DebugSession();
            this.session.varMap = VarMap.load(
                path.join(this.workspaceRoot, 'dist', '_debug', 'varmap.json')
            );

            this.session.on('stopped', (ev: StoppedEvent) => {
                this.lastStoppedEvent = ev;
                this.frames = ev.frames.length > 0
                    ? ev.frames
                    : [{ file: ev.file, line: ev.line, pc: ev.pc }];
                this.sendEvent('stopped', { reason: ev.reason, threadId: 1, allThreadsStopped: true });
            });
            this.session.on('terminated', () => {
                this.sendEvent('terminated');
                this.sendEvent('exited', { exitCode: 0 });
                this.cleanup();
            });

            await this.session.connect(serverPort);

            const hexFile     = path.join(this.workspaceRoot, 'dist', 'firmware.hex');
            const lineMapFile = path.join(this.workspaceRoot, 'dist', '_debug', 'linemap.json');
            this.session.send({ type: 'launch', hexFile, lineMapFile });

            await this.session.waitForReady();

            // Flush any breakpoints that were set before the server was ready
            this.serverReady = true;
            for (const [file, lines] of this.pendingBreakpoints) {
                this.session.send({ type: 'setBreakpoints', file, lines });
            }
            this.pendingBreakpoints.clear();

            this.sendResponse(req, {});
            this.sendEvent('output', { category: 'console', output: 'PyMCU AVR debugger ready.\n' });

            // Auto-continue to start simulation
            this.session.send({ type: 'continue' });

        } catch (e: unknown) {
            const msg = e instanceof Error ? e.message : String(e);
            this.sendResponse(req, {}, false, msg);
            this.sendEvent('terminated');
            this.cleanup();
        }
    }

    private handleSetBreakpoints(req: DapRequest): void {
        const args      = req.arguments ?? {};
        const src       = args['source'] as { path?: string } | undefined;
        const srcPath   = src?.path ?? '';
        const bps       = (args['breakpoints'] as Array<{ line: number }> | undefined) ?? [];
        const lines     = bps.map(b => b.line);

        // Convert absolute path → relative to sourcesDir
        let relFile = srcPath;
        if (this.workspaceRoot && relFile.startsWith(this.workspaceRoot)) {
            relFile = relFile.slice(this.workspaceRoot.length).replace(/^\//, '');
        }
        const prefix = `${this.sourcesDir}/`;
        if (relFile.startsWith(prefix)) { relFile = relFile.slice(prefix.length); }

        if (this.serverReady && this.session) {
            this.session.send({ type: 'setBreakpoints', file: relFile, lines });
        } else {
            this.pendingBreakpoints.set(relFile, lines);
        }

        this.sendResponse(req, {
            breakpoints: lines.map(l => ({ verified: true, line: l })),
        });
    }

    private handleConfigurationDone(req: DapRequest): void {
        this.sendResponse(req, {});
    }

    private handleContinue(req: DapRequest): void {
        this.session?.send({ type: 'continue' });
        this.sendResponse(req, { allThreadsContinued: true });
    }

    private handleNext(req: DapRequest): void {
        this.session?.send({ type: 'stepOver' });
        this.sendResponse(req, {});
    }

    private handleStepIn(req: DapRequest): void {
        this.session?.send({ type: 'stepInto' });
        this.sendResponse(req, {});
    }

    private handlePause(req: DapRequest): void {
        this.session?.send({ type: 'pause' });
        this.sendResponse(req, {});
    }

    private async handleStackTrace(req: DapRequest): Promise<void> {
        const stackFrames = this.frames.map((fi, idx) => {
            const absFile = this.resolveSourceFile(fi.file);
            return {
                id:     idx,
                name:   fi.file || `frame ${idx}`,
                line:   fi.line,
                column: 1,
                source: absFile ? { name: path.basename(absFile), path: absFile } : undefined,
            };
        });
        this.sendResponse(req, { stackFrames, totalFrames: stackFrames.length });
    }

    private handleScopes(req: DapRequest): void {
        const frameId = (req.arguments?.['frameId'] as number | undefined) ?? 0;
        this.sendResponse(req, {
            scopes: [{
                name:               'Variables',
                variablesReference: frameId + 1,
                expensive:          false,
            }],
        });
    }

    private async handleVariables(req: DapRequest): Promise<void> {
        const varRef  = (req.arguments?.['variablesReference'] as number | undefined) ?? 0;
        const frameId = varRef - 1;  // reverse of scope's variablesReference
        const frame   = this.frames[frameId];

        if (!frame || !this.session) {
            this.sendResponse(req, { variables: [] });
            return;
        }

        // Only the top frame (frameId === 0) has meaningful register values
        if (frameId !== 0) {
            this.sendResponse(req, { variables: [] });
            return;
        }

        const varMap = this.session.varMap;
        if (!varMap) {
            this.sendResponse(req, { variables: [] });
            return;
        }

        const scope = varMap.getScope(frame.file, frame.line);
        if (!scope) {
            this.sendResponse(req, { variables: [] });
            return;
        }

        const regs = await this.session.requestRegisters();
        const prefix = `${scope.function}.`;
        const variables: unknown[] = [];

        // Register-allocated variables
        for (const [varName, reg] of Object.entries(scope.vars)) {
            const declLine = scope.varLines[varName] ?? scope.startLine;
            if (!varName.startsWith(prefix) || declLine < scope.startLine || frame.line <= declLine) {
                continue;
            }
            const lo      = regs[reg]             ?? 0;
            const hi      = regs[regPlusOne(reg)]  ?? 0;
            const rawVal  = ((hi << 8) | (lo & 0xFF)) & 0xFFFF;
            const signed  = rawVal >= 0x8000 ? rawVal - 0x10000 : rawVal;
            const prevVal = this.session.previousValues.get(varName);
            const changed = prevVal !== undefined && prevVal !== signed;
            this.session.previousValues.set(varName, signed);
            variables.push({
                name:               varName.slice(prefix.length),
                value:              `${signed}  (0x${rawVal.toString(16).padStart(4, '0').toUpperCase()})`,
                type:               'int',
                variablesReference: 0,
                presentationHint:   changed ? { attributes: ['modified'] } : undefined,
            });
        }

        // Stack-spilled variables
        const relevant = Object.entries(scope.stackVars).filter(([n]) => n.startsWith(prefix));
        if (relevant.length > 0) {
            const addrs = relevant.map(([, a]) => a);
            const minAddr = Math.min(...addrs);
            const maxAddr = Math.max(...addrs) + 2;
            const len     = maxAddr - minAddr;

            const buf = await this.session.requestMemory(minAddr, len);

            for (const [varName, addr] of relevant) {
                const declLine = scope.stackVarLines[varName] ?? scope.startLine;
                if (declLine < scope.startLine || frame.line <= declLine) { continue; }
                const offset = addr - minAddr;
                const lo     = offset < buf.length     ? (buf[offset]     & 0xFF) : 0;
                const hi     = offset + 1 < buf.length ? (buf[offset + 1] & 0xFF) : 0;
                const rawVal = ((hi << 8) | lo) & 0xFFFF;
                const signed = rawVal >= 0x8000 ? rawVal - 0x10000 : rawVal;
                const prevVal = this.session.previousValues.get(varName);
                const changed = prevVal !== undefined && prevVal !== signed;
                this.session.previousValues.set(varName, signed);
                variables.push({
                    name:               varName.slice(prefix.length),
                    value:              `${signed}  (0x${rawVal.toString(16).padStart(4, '0').toUpperCase()})`,
                    type:               'int',
                    variablesReference: 0,
                    presentationHint:   changed ? { attributes: ['modified'] } : undefined,
                });
            }
        }

        this.sendResponse(req, { variables });
    }

    private async handleEvaluate(req: DapRequest): Promise<void> {
        const expr  = ((req.arguments?.['expression'] as string | undefined) ?? '').trim();
        const frame = this.frames[0];

        if (!frame || !this.session) {
            this.sendErrorResponse(req, `Cannot evaluate '${expr}': no active frame`);
            return;
        }

        const varMap = this.session.varMap;
        const scope  = varMap?.getScope(frame.file, frame.line);
        const regs   = await this.session.requestRegisters();

        if (scope) {
            const prefix  = `${scope.function}.`;
            const fullKey = expr.includes('.') ? expr : `${prefix}${expr}`;

            // Register-allocated?
            const reg = scope.vars[fullKey] ?? scope.vars[expr];
            if (reg) {
                const lo  = regs[reg]             ?? 0;
                const hi  = regs[regPlusOne(reg)]  ?? 0;
                const raw = ((hi << 8) | (lo & 0xFF)) & 0xFFFF;
                const s   = raw >= 0x8000 ? raw - 0x10000 : raw;
                this.sendResponse(req, {
                    result:             `${s}  (0x${raw.toString(16).padStart(4, '0').toUpperCase()})`,
                    type:               'int',
                    variablesReference: 0,
                });
                return;
            }

            // Stack-spilled?
            const addr = scope.stackVars[fullKey] ?? scope.stackVars[expr];
            if (addr !== undefined) {
                const buf = await this.session.requestMemory(addr, 2);
                const lo  = buf.length > 0 ? (buf[0] & 0xFF) : 0;
                const hi  = buf.length > 1 ? (buf[1] & 0xFF) : 0;
                const raw = ((hi << 8) | lo) & 0xFFFF;
                const s   = raw >= 0x8000 ? raw - 0x10000 : raw;
                this.sendResponse(req, {
                    result:             `${s}  (0x${raw.toString(16).padStart(4, '0').toUpperCase()})`,
                    type:               'int',
                    variablesReference: 0,
                });
                return;
            }
        }

        // Fall back to register name
        const regName = expr.toUpperCase();
        if (regs[regName] !== undefined) {
            const v = regs[regName] & 0xFF;
            this.sendResponse(req, {
                result:             `0x${v.toString(16).padStart(2, '0').toUpperCase()}  (${v})`,
                type:               'uint8',
                variablesReference: 0,
            });
            return;
        }

        this.sendErrorResponse(req, `Unknown variable or register: ${expr}`);
    }

    private handleDisconnect(req: DapRequest): void {
        this.sendResponse(req, {});
        this.session?.send({ type: 'terminate' });
        this.cleanup();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private resolveSourceFile(relFile: string): string | undefined {
        if (!relFile) { return undefined; }
        const candidates = [
            path.join(this.workspaceRoot, this.sourcesDir, relFile),
            path.join(this.workspaceRoot, relFile),
        ];
        return candidates.find(p => fs.existsSync(p));
    }

    private async runBuildDebug(executable: string): Promise<void> {
        await new Promise<void>((resolve, reject) => {
            const proc = cp.spawn(executable, ['build', '--debug'], {
                cwd:   this.workspaceRoot,
                stdio: ['ignore', 'pipe', 'pipe'],
            });
            let stderr = '';
            proc.stderr?.on('data', (d: Buffer) => { stderr += d.toString(); });
            proc.on('close', code => {
                if (code === 0) { resolve(); }
                else { reject(new Error(`pymcu build --debug failed (exit ${code}):\n${stderr}`)); }
            });
        });

        const hexFile     = path.join(this.workspaceRoot, 'dist', 'firmware.hex');
        const lineMapFile = path.join(this.workspaceRoot, 'dist', '_debug', 'linemap.json');
        if (!fs.existsSync(hexFile)) {
            throw new Error(`HEX file not found: ${hexFile}`);
        }
        if (!fs.existsSync(lineMapFile)) {
            throw new Error(`linemap.json not found: ${lineMapFile}\nMake sure pymcu-avr is installed.`);
        }
    }

    private findDebugServerBinary(): string | undefined {
        const candidates: string[] = [];

        // 1. On PATH
        try {
            const result = cp.execSync('which pymcuc-avr-debugserver', { encoding: 'utf-8' }).trim();
            if (result) { candidates.push(result); }
        } catch { /* not on PATH */ }

        // 2. Via venv Python → find pymcu.backend.avr package location
        const venvPython = this.findVenvPython();
        if (venvPython) {
            try {
                const avrDir = cp.execSync(
                    `${venvPython} -c "import pymcu.backend.avr as m, pathlib; print(pathlib.Path(m.__file__).parent)"`,
                    { encoding: 'utf-8', timeout: 5000 }
                ).trim();
                if (avrDir) {
                    candidates.push(path.join(avrDir, 'pymcuc-avr-debugserver'));

                    // Walk up to find pymcu-avr extension root
                    const parts = avrDir.split(path.sep);
                    const extIdx = parts.lastIndexOf('pymcu-avr');
                    if (extIdx !== -1) {
                        const extRoot = parts.slice(0, extIdx + 1).join(path.sep);
                        candidates.push(
                            path.join(extRoot, 'src', 'csharp', 'debugserver', 'bin', 'Debug', 'net10.0', 'pymcuc-avr-debugserver'),
                            path.join(extRoot, 'src', 'csharp', 'debugserver', 'bin', 'Release', 'net10.0', 'osx-arm64', 'publish', 'pymcuc-avr-debugserver'),
                            path.join(extRoot, 'src', 'csharp', 'debugserver', 'bin', 'Release', 'net10.0', 'linux-x64', 'publish', 'pymcuc-avr-debugserver'),
                        );
                    }
                }
            } catch { /* ignore */ }
        }

        return candidates.find(c => fs.existsSync(c));
    }

    private findVenvPython(): string | undefined {
        const candidates = [
            path.join(this.workspaceRoot, '.venv', 'bin', 'python3'),
            path.join(this.workspaceRoot, '.venv', 'bin', 'python'),
        ];
        return candidates.find(p => fs.existsSync(p));
    }

    private ensureSigned(binaryPath: string): void {
        if (process.platform !== 'darwin') { return; }
        try {
            cp.execSync(`codesign -s - --force "${binaryPath}"`, { stdio: 'ignore' });
        } catch { /* ignore — codesign may not be available */ }
    }

    private killZombie(port: number): void {
        try {
            cp.execSync(`lsof -ti :${port} | xargs kill -9 2>/dev/null || true`, { stdio: 'ignore' });
        } catch { /* ignore */ }
    }

    private cleanup(): void {
        this.session?.close();
        this.session = undefined;
        try { this.serverProcess?.kill('SIGKILL'); } catch { /* ignore */ }
        this.serverProcess = undefined;
    }

    // ── DAP message helpers ───────────────────────────────────────────────────

    private sendResponse(
        req: DapRequest,
        body: Record<string, unknown>,
        success = true,
        message?: string
    ): void {
        const resp: DapResponse = {
            seq:         this.seq++,
            type:        'response',
            request_seq: req.seq,
            success,
            command:     req.command,
            body,
        };
        if (message) { resp.message = message; }
        this._onDidSendMessage.fire(resp as unknown as vscode.DebugProtocolMessage);
    }

    private sendErrorResponse(req: DapRequest, message: string): void {
        this.sendResponse(req, {
            error: { id: 1, format: message }
        }, false, message);
    }

    private sendEvent(event: string, body?: Record<string, unknown>): void {
        const ev: DapEvent = {
            seq:   this.seq++,
            type:  'event',
            event,
        };
        if (body) { ev.body = body; }
        this._onDidSendMessage.fire(ev as unknown as vscode.DebugProtocolMessage);
    }

    dispose(): void {
        this.cleanup();
        this._onDidSendMessage.dispose();
    }
}
