import * as net from 'net';
import { EventEmitter } from 'events';
import { VarMap } from './varMap.js';

export interface FrameInfo { file: string; line: number; pc: number; }

export interface StoppedEvent {
    reason: string;
    file: string;
    line: number;
    pc: number;
    frames: FrameInfo[];
}

type PendingRegsCallback   = (regs: Record<string, number>) => void;
type PendingMemoryCallback = (address: number, data: Buffer) => void;

/** TCP client for the pymcuc-avr-debugserver JSON line protocol. */
export class DebugSession extends EventEmitter {
    private socket?: net.Socket;
    private buffer = '';
    private pendingRegs?: PendingRegsCallback;
    private pendingMemory?: PendingMemoryCallback;
    private readyResolve?: () => void;
    private readyReject?: (e: Error) => void;
    private pendingMessages: string[] = [];

    varMap?: VarMap;
    /** Tracks previous values to detect changes (varName → signed int). */
    readonly previousValues = new Map<string, number>();

    connect(port: number): Promise<void> {
        return new Promise((resolve, reject) => {
            const sock = net.createConnection(port, '127.0.0.1');
            this.socket = sock;

            sock.on('connect', () => {
                // Flush queued messages
                for (const msg of this.pendingMessages) { sock.write(msg + '\n'); }
                this.pendingMessages = [];
                resolve();
            });

            sock.on('data', (chunk: Buffer) => {
                this.buffer += chunk.toString('utf-8');
                const lines = this.buffer.split('\n');
                this.buffer = lines.pop() ?? '';
                for (const line of lines) {
                    const trimmed = line.trim();
                    if (trimmed) { this.dispatch(trimmed); }
                }
            });

            sock.on('error', reject);
            sock.on('close', () => this.emit('terminated'));
        });
    }

    private dispatch(json: string): void {
        let msg: Record<string, unknown>;
        try { msg = JSON.parse(json); }
        catch { return; }

        const type = msg['type'] as string | undefined;
        switch (type) {
            case 'stopped': {
                const frames: FrameInfo[] = (msg['frames'] as FrameInfo[] | undefined) ?? [];
                this.emit('stopped', {
                    reason: (msg['reason'] as string) ?? 'breakpoint',
                    file:   (msg['file']   as string) ?? '',
                    line:   (msg['line']   as number) ?? 0,
                    pc:     (msg['pc']     as number) ?? 0,
                    frames,
                } as StoppedEvent);
                break;
            }
            case 'registers': {
                const data = msg['data'] as Record<string, number> | undefined;
                if (data && this.pendingRegs) {
                    const cb = this.pendingRegs;
                    this.pendingRegs = undefined;
                    cb(data);
                }
                break;
            }
            case 'memory': {
                const addr  = msg['address'] as number | undefined;
                const bytes = msg['data']    as number[] | undefined;
                if (addr !== undefined && bytes && this.pendingMemory) {
                    const cb = this.pendingMemory;
                    this.pendingMemory = undefined;
                    cb(addr, Buffer.from(bytes));
                }
                break;
            }
            case 'ready':
                this.readyResolve?.();
                this.readyResolve = undefined;
                this.readyReject  = undefined;
                break;
            case 'terminated':
                this.emit('terminated');
                break;
            case 'error':
                // non-fatal server error
                break;
        }
    }

    send(obj: Record<string, unknown>): void {
        const msg = JSON.stringify(obj);
        if (this.socket?.writable) {
            this.socket.write(msg + '\n');
        } else {
            this.pendingMessages.push(msg);
        }
    }

    waitForReady(timeoutMs = 8_000): Promise<void> {
        return new Promise((resolve, reject) => {
            this.readyResolve = resolve;
            this.readyReject  = reject;
            setTimeout(() => reject(new Error(`pymcuc-avr-debugserver did not send 'ready' within ${timeoutMs}ms`)), timeoutMs);
        });
    }

    requestRegisters(): Promise<Record<string, number>> {
        return new Promise(resolve => {
            this.pendingRegs = resolve;
            this.send({ type: 'getRegisters' });
        });
    }

    requestMemory(address: number, length: number): Promise<Buffer> {
        return new Promise(resolve => {
            this.pendingMemory = (_, data) => resolve(data);
            this.send({ type: 'getMemory', address, length });
        });
    }

    close(): void {
        this.readyReject?.(new Error('session closed'));
        this.readyResolve = undefined;
        this.readyReject  = undefined;
        try { this.socket?.destroy(); } catch { /* ignore */ }
    }
}
