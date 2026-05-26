import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import { execSync } from 'child_process';
import { byManufacturer, type BoardEntry } from '../boards/boardRegistry.js';

const PACKAGE_MANAGERS = ['uv', 'pip', 'poetry', 'pipenv'] as const;
type PackageManager = typeof PACKAGE_MANAGERS[number];

function syncCmd(pm: PackageManager): string {
    switch (pm) {
        case 'uv':     return 'uv sync';
        case 'poetry': return 'poetry install';
        case 'pipenv': return 'pipenv install';
        case 'pip':    return 'pip install -e .';
    }
}

export async function runNewProjectWizard(): Promise<void> {
    // Step 1: project name
    const projectName = await vscode.window.showInputBox({
        title: 'PyMCU: New Project (1/3)',
        prompt: 'Enter the project name',
        placeHolder: 'my-mcu-project',
        validateInput: v => {
            if (!v?.trim()) { return 'Project name is required'; }
            if (/[^a-zA-Z0-9_\-.]/.test(v)) { return 'Only letters, numbers, hyphens, underscores, dots'; }
            return null;
        },
    });
    if (!projectName) { return; }

    // Step 2: board selection (grouped QuickPick)
    type BoardPickItem = vscode.QuickPickItem & { board?: BoardEntry };
    const boardItems: BoardPickItem[] = [];
    for (const [mfr, boards] of byManufacturer) {
        boardItems.push({ label: mfr, kind: vscode.QuickPickItemKind.Separator });
        for (const b of boards) {
            boardItems.push({
                board: b,
                label: b.name,
                description: `${b.chip} · ${b.frequency / 1_000_000} MHz · ${b.flashKb} KB flash`,
            });
        }
    }

    const boardPick = await vscode.window.showQuickPick(boardItems, {
        title: 'PyMCU: New Project (2/3) — Select Target Board',
        placeHolder: 'Filter boards…',
        matchOnDescription: true,
    });
    if (!boardPick?.board) { return; }
    const board = boardPick.board;

    // Step 3: package manager
    const pmPick = await vscode.window.showQuickPick(
        PACKAGE_MANAGERS.map(pm => ({ label: pm, description: pm === 'uv' ? '(recommended)' : '' })),
        { title: 'PyMCU: New Project (3/3) — Package Manager', placeHolder: 'Select package manager' }
    );
    if (!pmPick) { return; }
    const pm = pmPick.label as PackageManager;

    // Determine parent directory
    let parentDir: vscode.Uri | undefined;
    const workspace = vscode.workspace.workspaceFolders?.[0];
    if (workspace) {
        parentDir = workspace.uri;
    } else {
        const selected = await vscode.window.showOpenDialog({
            canSelectFolders: true,
            canSelectFiles: false,
            canSelectMany: false,
            openLabel: 'Select parent directory',
        });
        if (!selected?.length) { return; }
        parentDir = selected[0];
    }

    const projectDir = path.join(parentDir.fsPath, projectName);

    if (fs.existsSync(projectDir)) {
        const overwrite = await vscode.window.showWarningMessage(
            `Directory "${projectName}" already exists. Overwrite?`,
            'Overwrite', 'Cancel'
        );
        if (overwrite !== 'Overwrite') { return; }
    }

    // Scaffold
    try {
        fs.mkdirSync(projectDir, { recursive: true });
        fs.mkdirSync(path.join(projectDir, 'src'), { recursive: true });

        fs.writeFileSync(
            path.join(projectDir, 'pyproject.toml'),
            buildPyproject(projectName, board, pm)
        );
        fs.writeFileSync(
            path.join(projectDir, 'src', 'main.py'),
            buildMainPy(board)
        );
    } catch (e) {
        vscode.window.showErrorMessage(`Failed to scaffold project: ${e}`);
        return;
    }

    // Open project folder
    const openUri = vscode.Uri.file(projectDir);
    await vscode.commands.executeCommand('vscode.openFolder', openUri);

    // Run sync in terminal (visible to the user)
    const terminal = vscode.window.createTerminal({
        name: 'PyMCU: Project Setup',
        cwd: openUri,
    });
    terminal.show();
    terminal.sendText(syncCmd(pm));
}

function buildPyproject(name: string, board: BoardEntry, pm: PackageManager): string {
    const pmSection = pm === 'uv'
        ? `\n[tool.uv]\nmanaged = true\n`
        : '';

    return `[project]
name = "${name}"
version = "0.1.0"
description = "PyMCU project targeting ${board.name}"
requires-python = ">=3.11"
dependencies = ["pymcu-compiler"]

[tool.pymcu]
board = "${board.id}"
chip = "${board.chip}"
frequency = ${board.frequency}
sources = "src"
entry = "main.py"
${pmSection}`;
}

function buildMainPy(board: BoardEntry): string {
    const chip = board.chip;
    if (chip.startsWith('attiny')) {
        return `from pymcu import *


# ${board.name} — ATtiny firmware
# Blink on PB0 (pin 5)
def main() -> None:
    DDRB = 0xFF  # all outputs
    while True:
        PORTB = 0xFF
        delay_ms(500)
        PORTB = 0x00
        delay_ms(500)


main()
`;
    }

    // ATmega / default
    return `from pymcu import *


# ${board.name} — Blink LED on PB5 (Arduino pin 13)
def main() -> None:
    DDRB = 0x20  # PB5 output
    while True:
        PORTB = PORTB | 0x20   # LED on
        delay_ms(500)
        PORTB = PORTB & ~0x20  # LED off
        delay_ms(500)


main()
`;
}

/** Checks if a command is on PATH. */
function commandExists(cmd: string): boolean {
    try { execSync(`which ${cmd}`, { stdio: 'ignore' }); return true; }
    catch { return false; }
}
