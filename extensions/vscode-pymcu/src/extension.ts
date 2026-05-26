import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { execSync, exec } from 'child_process';

import { ProjectConfigPanel } from './panels/projectConfigPanel.js';
import { BoardsTreeProvider, selectBoard } from './boards/boardsView.js';
import { ProjectTasksProvider } from './views/projectTasksView.js';
import { PyMcuStatusBar } from './ui/statusBar.js';
import { runNewProjectWizard } from './wizard/newProjectWizard.js';
import { registerBranchPruningDecorator } from './editor/branchPruningDecorator.js';
import { openSerialMonitor } from './serial/serialMonitor.js';
import { PyMcuDebugAdapterFactory } from './debug/debugAdapter.js';
import { getConfig, clearConfigCache } from './config/configReader.js';

let diagnosticCollection: vscode.DiagnosticCollection;

export function activate(context: vscode.ExtensionContext) {
    console.log('PyMCU extension is now active!');
    vscode.commands.executeCommand('setContext', 'pymcu:active', true);

    checkPymcuInstallation();

    // ── Sidebar providers ──────────────────────────────────────────────────
    const boardsProvider = new BoardsTreeProvider();
    const tasksProvider  = new ProjectTasksProvider();

    context.subscriptions.push(
        vscode.window.registerTreeDataProvider('pymcu-boards', boardsProvider),
        vscode.window.registerTreeDataProvider('pymcu-project-tasks', tasksProvider),
    );

    // ── Status bar ─────────────────────────────────────────────────────────
    const statusBar = new PyMcuStatusBar();
    context.subscriptions.push(...statusBar.getDisposables());

    // ── Debug adapter ──────────────────────────────────────────────────────
    context.subscriptions.push(
        vscode.debug.registerDebugAdapterDescriptorFactory('pymcu', new PyMcuDebugAdapterFactory()),
    );

    // ── Commands ───────────────────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('pymcu.build',            () => runPymcuCommand('build')),
        vscode.commands.registerCommand('pymcu.flash',            () => runPymcuCommand('flash')),
        vscode.commands.registerCommand('pymcu.clean',            () => runPymcuCommand('clean')),
        vscode.commands.registerCommand('pymcu.debugBuild',       () => runPymcuCommand('build --debug')),
        vscode.commands.registerCommand('pymcu.new',              () => runNewProjectWizard()),
        vscode.commands.registerCommand('pymcu.configureProject', () => ProjectConfigPanel.createOrShow(context)),
        vscode.commands.registerCommand('pymcu.serialMonitor',    () => openSerialMonitor()),
        vscode.commands.registerCommand('pymcu.selectBoard',      (boardId: string) => selectBoard(boardId)),
        vscode.commands.registerCommand('pymcu.refreshBoards',    () => boardsProvider.refresh()),
        vscode.commands.registerCommand('pymcu.startDebug',       () => startDebugSession()),
    );

    // ── pyproject.toml watcher ─────────────────────────────────────────────
    const watcher = vscode.workspace.createFileSystemWatcher('**/pyproject.toml');
    watcher.onDidChange(() => { clearConfigCache(); statusBar.update(); syncProject(); });
    watcher.onDidCreate(() => { clearConfigCache(); statusBar.update(); syncProject(); });
    watcher.onDidDelete(() => { clearConfigCache(); statusBar.update(); });
    context.subscriptions.push(watcher);

    // ── Branch-pruning decorator ───────────────────────────────────────────
    registerBranchPruningDecorator(context);

    // ── Diagnostics ────────────────────────────────────────────────────────
    diagnosticCollection = vscode.languages.createDiagnosticCollection('pymcu');
    context.subscriptions.push(
        diagnosticCollection,
        vscode.workspace.onDidChangeTextDocument(e => validatePyproject(e.document)),
        vscode.workspace.onDidOpenTextDocument(d => validatePyproject(d)),
    );
    if (vscode.window.activeTextEditor) {
        validatePyproject(vscode.window.activeTextEditor.document);
    }

    // ── IntelliSense ───────────────────────────────────────────────────────
    void configureIntellisense();

    // ── Initial sync ───────────────────────────────────────────────────────
    if (findPyprojectToml()) { syncProject(); }
}

// ── Helpers ──────────────────────────────────────────────────────────────────

function getExecutablePath(): string {
    return vscode.workspace.getConfiguration('pymcu').get<string>('executablePath') || 'pymcu';
}

function getSyncCommand(): string {
    const pm = vscode.workspace.getConfiguration('pymcu').get<string>('packageManager') || 'uv';
    switch (pm) {
        case 'uv':     return 'uv sync';
        case 'poetry': return 'poetry install';
        case 'pipenv': return 'pipenv install';
        case 'pip':    return 'pip install -e .';
        default:       return `${pm} sync`;
    }
}

function checkPymcuInstallation() {
    try {
        execSync(`${getExecutablePath()} --help`, { stdio: 'ignore' });
    } catch {
        void vscode.window.showWarningMessage(
            'PyMCU CLI not detected. Install it with: pipx install pymcu-compiler',
            'Install instructions'
        ).then(sel => {
            if (sel === 'Install instructions') {
                void vscode.env.openExternal(vscode.Uri.parse('https://pypa.github.io/pipx/'));
            }
        });
    }
}

function findPyprojectToml(): string | undefined {
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) { return undefined; }
    const p = path.join(folder.uri.fsPath, 'pyproject.toml');
    return fs.existsSync(p) ? p : undefined;
}

async function runPymcuCommand(command: string) {
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) {
        vscode.window.showErrorMessage('No workspace folder open. Open a PyMCU project first.');
        return;
    }
    const isClean = command === 'clean';
    if (!isClean && !fs.existsSync(path.join(folder.uri.fsPath, 'pyproject.toml'))) {
        vscode.window.showErrorMessage(
            'No pyproject.toml found. Run "PyMCU: New Project" to create a project.'
        );
        return;
    }
    const task = new vscode.Task(
        { type: 'pymcu', command },
        folder,
        `pymcu ${command}`,
        'pymcu',
        new vscode.ShellExecution(`${getExecutablePath()} ${command}`),
        ['$pymcuc'],
    );
    if (command === 'build') { task.group = vscode.TaskGroup.Build; }
    try {
        await vscode.tasks.executeTask(task);
    } catch (e) {
        vscode.window.showErrorMessage(`Failed to execute pymcu ${command}: ${e}`);
    }
}

async function startDebugSession() {
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) {
        vscode.window.showErrorMessage('No workspace folder open.');
        return;
    }
    const config = getConfig();
    await vscode.debug.startDebugging(folder, {
        type:          'pymcu',
        request:       'launch',
        name:          'PyMCU: Debug',
        workspaceRoot: folder.uri.fsPath,
        sourcesDir:    config?.sources?.[0] ? path.dirname(config.sources[0]) : 'src',
    });
}

async function configureIntellisense() {
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) { return; }

    const pythonConfig = vscode.workspace.getConfiguration('python', folder.uri);
    const extraPaths   = pythonConfig.get<string[]>('analysis.extraPaths') ?? [];
    let updated = false;

    const localLib = path.join(folder.uri.fsPath, 'lib', 'src');
    if (fs.existsSync(localLib) && !extraPaths.includes(localLib)) {
        extraPaths.push(localLib);
        updated = true;
    }

    const run = (cmd: string): string | undefined => {
        try { return execSync(cmd, { cwd: folder.uri.fsPath, encoding: 'utf-8', timeout: 5000, stdio: ['ignore', 'pipe', 'ignore'] }).trim(); }
        catch { return undefined; }
    };

    let stdlibPath =
        run('python3 -c "import pymcu; from pathlib import Path; print(Path(pymcu.__file__).parent)"') ??
        run('python -c "import pymcu; from pathlib import Path; print(Path(pymcu.__file__).parent)"');

    if (!stdlibPath && fs.existsSync(path.join(folder.uri.fsPath, 'pyproject.toml')) && run('uv --version')) {
        stdlibPath = run('uv run python -c "import pymcu; from pathlib import Path; print(Path(pymcu.__file__).parent)"');
        if (!stdlibPath) {
            try {
                execSync(getSyncCommand(), { cwd: folder.uri.fsPath, stdio: 'ignore', timeout: 60000 });
                stdlibPath = run('uv run python -c "import pymcu; from pathlib import Path; print(Path(pymcu.__file__).parent)"');
            } catch { /* ignore */ }
        }
    }

    if (stdlibPath && fs.existsSync(stdlibPath)) {
        const parent = path.dirname(stdlibPath);
        if (!extraPaths.includes(parent)) { extraPaths.push(parent); updated = true; }
    }

    if (updated) {
        await pythonConfig.update('analysis.extraPaths', extraPaths, vscode.ConfigurationTarget.Workspace);
    }
}

function validatePyproject(document: vscode.TextDocument) {
    if (!document.fileName.endsWith('pyproject.toml')) { return; }
    const lines = document.getText().split('\n');
    const diagnostics: vscode.Diagnostic[] = [];
    let inSection = false;
    let foundChip = false;
    let sectionLine = -1;

    for (let i = 0; i < lines.length; i++) {
        const line = lines[i].trim();
        if (line.startsWith('#')) { continue; }
        if (line === '[tool.pymcu]' || line === '[tool.whip]') {
            inSection = true; sectionLine = i; continue;
        }
        if (line.startsWith('[') && inSection) { inSection = false; }
        if (!inSection) { continue; }

        if (line.startsWith('chip') || line.startsWith('target')) {
            foundChip = true;
            const m = line.match(/^(?:chip|target)\s*=\s*"([^"]*)"/);
            if (!m) {
                diagnostics.push(new vscode.Diagnostic(
                    new vscode.Range(i, 0, i, lines[i].length),
                    'Invalid format. Expected: chip = "name"',
                    vscode.DiagnosticSeverity.Error,
                ));
            } else if (!m[1].trim()) {
                diagnostics.push(new vscode.Diagnostic(
                    new vscode.Range(i, 0, i, lines[i].length),
                    'Chip name cannot be empty',
                    vscode.DiagnosticSeverity.Error,
                ));
            }
        }
    }

    if (sectionLine !== -1 && !foundChip) {
        diagnostics.push(new vscode.Diagnostic(
            new vscode.Range(sectionLine, 0, sectionLine, lines[sectionLine].length),
            'Missing "chip" (or "target") in [tool.pymcu]',
            vscode.DiagnosticSeverity.Warning,
        ));
    }

    diagnosticCollection.set(document.uri, diagnostics);
}

function syncProject() {
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder || !fs.existsSync(path.join(folder.uri.fsPath, 'pyproject.toml'))) { return; }

    void vscode.window.withProgress(
        { location: vscode.ProgressLocation.Window, title: 'Syncing PyMCU project…', cancellable: false },
        () => new Promise<void>(resolve => {
            exec(getSyncCommand(), { cwd: folder.uri.fsPath }, async (error) => {
                if (!error) { await configureIntellisense(); }
                resolve();
            });
        }),
    );
}

export function deactivate() {
    diagnosticCollection?.dispose();
}
