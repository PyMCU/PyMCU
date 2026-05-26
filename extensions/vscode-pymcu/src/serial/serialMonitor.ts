import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';

function autoDetectPort(): string | undefined {
    try {
        if (process.platform === 'darwin' || process.platform === 'linux') {
            const prefixes = process.platform === 'darwin'
                ? ['cu.usbmodem', 'cu.usbserial', 'tty.usbmodem', 'tty.usbserial']
                : ['ttyACM', 'ttyUSB'];
            for (const prefix of prefixes) {
                const match = fs.readdirSync('/dev').find(e => e.startsWith(prefix));
                if (match) { return path.join('/dev', match); }
            }
        }
    } catch { /* ignore */ }
    return undefined;
}

function getSerialConfig(): { port: string | undefined; baud: number } {
    const config = vscode.workspace.getConfiguration('pymcu');
    const port   = config.get<string>('serialPort') || autoDetectPort();
    const baud   = config.get<number>('serialBaudRate') ?? 9600;
    return { port, baud };
}

export async function openSerialMonitor(): Promise<void> {
    let { port, baud } = getSerialConfig();

    if (!port) {
        port = await vscode.window.showInputBox({
            title: 'PyMCU: Serial Monitor',
            prompt: 'Serial port not detected. Enter port manually:',
            placeHolder: process.platform === 'darwin' ? '/dev/cu.usbmodem…' : 'COM3 or /dev/ttyACM0',
        });
        if (!port) { return; }
    }

    const baudInput = await vscode.window.showInputBox({
        title: 'PyMCU: Serial Monitor — Baud Rate',
        value: String(baud),
        prompt: 'Baud rate',
        validateInput: v => (isNaN(Number(v)) || Number(v) <= 0) ? 'Enter a valid baud rate' : null,
    });
    if (!baudInput) { return; }
    baud = parseInt(baudInput, 10);

    // Try python3 -m serial.tools.miniterm first (pyserial)
    const terminal = vscode.window.createTerminal({
        name: `PyMCU Serial — ${port}`,
        cwd: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath,
    });
    terminal.show();

    // Try uv run (respects project venv) first, then fall back to system python3
    const folder = vscode.workspace.workspaceFolders?.[0];
    let cmd: string;
    if (folder && fs.existsSync(path.join(folder.uri.fsPath, '.venv'))) {
        cmd = `uv run python -m serial.tools.miniterm --filter printable ${port} ${baud}`;
    } else {
        cmd = `python3 -m serial.tools.miniterm --filter printable ${port} ${baud}`;
    }

    terminal.sendText(cmd);

    vscode.window.showInformationMessage(
        `PyMCU Serial Monitor opened on ${port} @ ${baud} baud. Press Ctrl+] to exit.`
    );
}
