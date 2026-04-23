import { ProjectConfigPanel } from './projectConfigPanel';
import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { execSync, exec } from 'child_process';

let chipStatusBarItem: vscode.StatusBarItem;
let diagnosticCollection: vscode.DiagnosticCollection;

export function activate(context: vscode.ExtensionContext) {
    console.log('PyMCU extension is now active!');

    // Set context for 'when' clauses in package.json
    vscode.commands.executeCommand('setContext', 'pymcu:active', true);

    checkPymcuInstallation();

    // Register commands
    context.subscriptions.push(
        vscode.commands.registerCommand('pymcu.build', () => runPymcuCommand('build')),
        vscode.commands.registerCommand('pymcu.flash', () => runPymcuCommand('flash')),
        vscode.commands.registerCommand('pymcu.clean', () => runPymcuCommand('clean')),
        vscode.commands.registerCommand('pymcu.new', () => runNewProject()),
        vscode.commands.registerCommand('pymcu.configureProject', () => ProjectConfigPanel.createOrShow(context))
    );

    // Status bar: show target chip from pyproject.toml
    chipStatusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
    chipStatusBarItem.command = 'pymcu.configureProject';
    chipStatusBarItem.tooltip = 'PyMCU target — click to configure';
    context.subscriptions.push(chipStatusBarItem);

    updateChipStatusBar();

    // Watch pyproject.toml for changes to update status bar and sync
    const watcher = vscode.workspace.createFileSystemWatcher('**/pyproject.toml');
    watcher.onDidChange(() => {
        updateChipStatusBar();
        syncProject();
    });
    watcher.onDidCreate(() => {
        updateChipStatusBar();
        syncProject();
    });
    watcher.onDidDelete(() => updateChipStatusBar());
    context.subscriptions.push(watcher);

    // Diagnostics
    diagnosticCollection = vscode.languages.createDiagnosticCollection('pymcu');
    context.subscriptions.push(diagnosticCollection);

    context.subscriptions.push(
        vscode.workspace.onDidChangeTextDocument(event => {
            validatePyproject(event.document);
        }),
        vscode.workspace.onDidOpenTextDocument(document => {
            validatePyproject(document);
        })
    );

    if (vscode.window.activeTextEditor) {
        validatePyproject(vscode.window.activeTextEditor.document);
    }

    configureIntellisense();

    // Sync on activation if a project is already open
    const pyproject = findPyprojectToml();
    if (pyproject) {
        syncProject();
    }
}

function getExecutablePath(): string {
    const config = vscode.workspace.getConfiguration('pymcu');
    return config.get<string>('executablePath') || 'pymcu';
}

function getSyncCommand(): string {
    const config = vscode.workspace.getConfiguration('pymcu');
    const pm = config.get<string>('packageManager') || 'uv';
    switch (pm) {
        case 'uv':      return 'uv sync';
        case 'poetry':  return 'poetry install';
        case 'pipenv':  return 'pipenv install';
        case 'pip':     return 'pip install -e .';
        default:        return `${pm} sync`;
    }
}

function checkPymcuInstallation() {
    try {
        const executable = getExecutablePath();
        execSync(`${executable} --help`, { stdio: 'ignore' });
    } catch {
        vscode.window.showWarningMessage(
            'PyMCU CLI not detected. Install it with: pipx install pymcu-compiler',
            'Install instructions'
        ).then(selection => {
            if (selection === 'Install instructions') {
                vscode.env.openExternal(vscode.Uri.parse('https://pypa.github.io/pipx/'));
            }
        });
    }
}

async function updateChipStatusBar() {
    const pyproject = findPyprojectToml();
    if (!pyproject) {
        chipStatusBarItem.hide();
        return;
    }

    try {
        const content = fs.readFileSync(pyproject, 'utf-8');
        // Prefer board over chip for display (matches PyMCUConfigReader logic)
        const boardMatch = content.match(/\[tool\.pymcu\][\s\S]*?board\s*=\s*"([^"]+)"/);
        const chipMatch  = content.match(/\[tool\.pymcu\][\s\S]*?chip\s*=\s*"([^"]+)"/);
        const target = boardMatch?.[1] ?? chipMatch?.[1];
        if (target) {
            chipStatusBarItem.text = `$(circuit-board) ${target}`;
            chipStatusBarItem.show();
        } else {
            chipStatusBarItem.hide();
        }
    } catch {
        chipStatusBarItem.hide();
    }
}

function findPyprojectToml(): string | undefined {
    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    if (!workspaceFolder) { return undefined; }

    const pyprojectPath = path.join(workspaceFolder.uri.fsPath, 'pyproject.toml');
    if (fs.existsSync(pyprojectPath)) {
        return pyprojectPath;
    }
    return undefined;
}

async function runPymcuCommand(command: string) {
    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    if (!workspaceFolder) {
        vscode.window.showErrorMessage('No workspace folder open. Open a PyMCU project first.');
        return;
    }

    // Verify pyproject.toml exists for build/flash (not clean)
    if (command !== 'clean') {
        const pyprojectPath = path.join(workspaceFolder.uri.fsPath, 'pyproject.toml');
        if (!fs.existsSync(pyprojectPath)) {
            vscode.window.showErrorMessage(
                'No pyproject.toml found. Run "PyMCU: New Project" to create a project, or open an existing one.'
            );
            return;
        }
    }

    const executable = getExecutablePath();

    const task = new vscode.Task(
        { type: 'pymcu', command: command },
        workspaceFolder,
        `pymcu ${command}`,
        'pymcu',
        new vscode.ShellExecution(`${executable} ${command}`),
        ['$pymcuc']
    );

    // Mark build as the default build task
    if (command === 'build') {
        task.group = vscode.TaskGroup.Build;
    }

    try {
        await vscode.tasks.executeTask(task);
    } catch (e) {
        vscode.window.showErrorMessage(`Failed to execute pymcu ${command}: ${e}`);
    }
}

async function runNewProject() {
    // Prompt for project name since `pymcu new` requires it
    const projectName = await vscode.window.showInputBox({
        prompt: 'Enter the new project name',
        placeHolder: 'my-mcu-project',
        validateInput: (value) => {
            if (!value || value.trim().length === 0) {
                return 'Project name is required';
            }
            if (/[^a-zA-Z0-9_\-.]/.test(value)) {
                return 'Project name can only contain letters, numbers, hyphens, underscores, and dots';
            }
            return null;
        }
    });

    if (!projectName) { return; } // User cancelled

    // Determine the parent directory
    let parentDir: vscode.Uri | undefined;

    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    if (workspaceFolder) {
        parentDir = workspaceFolder.uri;
    } else {
        const selected = await vscode.window.showOpenDialog({
            canSelectFolders: true,
            canSelectFiles: false,
            canSelectMany: false,
            openLabel: 'Select parent directory'
        });
        if (!selected || selected.length === 0) { return; }
        parentDir = selected[0];
    }

    const executable = getExecutablePath();

    // Run `pymcu new <name>` in a terminal (interactive — uses Rich prompts)
    const terminal = vscode.window.createTerminal({
        name: 'PyMCU: New Project',
        cwd: parentDir
    });
    terminal.show();
    terminal.sendText(`${executable} new ${projectName}`);
}

async function configureIntellisense() {
    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    if (!workspaceFolder) { return; }

    const pythonConfig = vscode.workspace.getConfiguration('python', workspaceFolder.uri);
    const extraPaths = pythonConfig.get<string[]>('analysis.extraPaths') || [];
    let updated = false;

    // 1. Check for local lib/src in workspace (development mode)
    const localLibPath = path.join(workspaceFolder.uri.fsPath, 'lib', 'src');
    if (fs.existsSync(localLibPath) && !extraPaths.includes(localLibPath)) {
        extraPaths.push(localLibPath);
        updated = true;
    }

    // 2. Try to resolve installed pymcu stdlib path
    let stdlibPath: string | undefined;

    const runCommand = (command: string): string | undefined => {
        try {
            return execSync(command, {
                cwd: workspaceFolder.uri.fsPath,
                encoding: 'utf-8',
                timeout: 5000,
                stdio: ['ignore', 'pipe', 'ignore'] // Ignore stderr to prevent traceback in console
            }).trim();
        } catch {
            return undefined;
        }
    };

    // Try with system python (python3 or python)
    stdlibPath = runCommand('python3 -c "import pymcu; from pathlib import Path; print(Path(pymcu.__file__).parent)"');
    if (!stdlibPath) {
        stdlibPath = runCommand('python -c "import pymcu; from pathlib import Path; print(Path(pymcu.__file__).parent)"');
    }

    // If not found, try with uv (if pyproject.toml exists)
    if (!stdlibPath && fs.existsSync(path.join(workspaceFolder.uri.fsPath, 'pyproject.toml'))) {
        // Check if uv is available
        if (runCommand('uv --version')) {
             // Try running with uv (which might use .venv)
            stdlibPath = runCommand('uv run python -c "import pymcu; from pathlib import Path; print(Path(pymcu.__file__).parent)"');
            
            // If still not found, try syncing first
            if (!stdlibPath) {
                try {
                    execSync(getSyncCommand(), {
                        cwd: workspaceFolder.uri.fsPath,
                        stdio: ['ignore', 'ignore', 'ignore'],
                        timeout: 60000
                    });
                    // Try again after sync
                    stdlibPath = runCommand('uv run python -c "import pymcu; from pathlib import Path; print(Path(pymcu.__file__).parent)"');
                } catch {
                    // sync failed
                }
            }
        }
    }

    if (stdlibPath && fs.existsSync(stdlibPath)) {
        // Add parent so `from pymcu.xxx import yyy` resolves
        const parentPath = path.dirname(stdlibPath);
        if (!extraPaths.includes(parentPath)) {
            extraPaths.push(parentPath);
            updated = true;
        }
    }

    if (updated) {
        await pythonConfig.update('analysis.extraPaths', extraPaths, vscode.ConfigurationTarget.Workspace);
    }
}

function validatePyproject(document: vscode.TextDocument) {
    if (!document.fileName.endsWith('pyproject.toml')) { return; }
    
    const text = document.getText();
    const diagnostics: vscode.Diagnostic[] = [];
    
    const lines = text.split('\n');
    let inPymcuSection = false;
    let foundTarget = false;
    let pymcuSectionLine = -1;

    for (let i = 0; i < lines.length; i++) {
        const line = lines[i].trim();
        if (line.startsWith('#')) { continue; }

        if (line === '[tool.pymcu]') {
            inPymcuSection = true;
            pymcuSectionLine = i;
            continue;
        }
        if (line.startsWith('[') && line !== '[tool.pymcu]') {
            inPymcuSection = false;
        }
        
        if (inPymcuSection) {
            if (line.startsWith('chip')) {
                foundTarget = true;
                const match = line.match(/^chip\s*=\s*"([^"]*)"/);
                if (!match) {
                     const range = new vscode.Range(i, 0, i, lines[i].length);
                     diagnostics.push(new vscode.Diagnostic(range, 'Invalid format. Expected: chip = "name"', vscode.DiagnosticSeverity.Error));
                } else if (match[1].trim() === '') {
                     const range = new vscode.Range(i, 0, i, lines[i].length);
                     diagnostics.push(new vscode.Diagnostic(range, 'Chip name cannot be empty', vscode.DiagnosticSeverity.Error));
                }
            }
            if (line.startsWith('board')) {
                foundTarget = true;
                const match = line.match(/^board\s*=\s*"([^"]*)"/);
                if (!match) {
                     const range = new vscode.Range(i, 0, i, lines[i].length);
                     diagnostics.push(new vscode.Diagnostic(range, 'Invalid format. Expected: board = "name"', vscode.DiagnosticSeverity.Error));
                } else if (match[1].trim() === '') {
                     const range = new vscode.Range(i, 0, i, lines[i].length);
                     diagnostics.push(new vscode.Diagnostic(range, 'Board name cannot be empty', vscode.DiagnosticSeverity.Error));
                }
            }
        }
    }
    
    if (pymcuSectionLine !== -1 && !foundTarget) {
         const range = new vscode.Range(pymcuSectionLine, 0, pymcuSectionLine, lines[pymcuSectionLine].length);
         diagnostics.push(new vscode.Diagnostic(range, 'Missing "chip" or "board" configuration in [tool.pymcu]', vscode.DiagnosticSeverity.Error));
    }
    
    diagnosticCollection.set(document.uri, diagnostics);
}

function syncProject() {
    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    if (!workspaceFolder) { return; }
    
    const pyprojectPath = path.join(workspaceFolder.uri.fsPath, 'pyproject.toml');
    if (!fs.existsSync(pyprojectPath)) { return; }

    vscode.window.withProgress({
        location: vscode.ProgressLocation.Window,
        title: "Syncing PyMCU project...",
        cancellable: false
    }, () => {
        return new Promise<void>((resolve) => {
            exec(getSyncCommand(), { cwd: workspaceFolder.uri.fsPath }, async (error, _stdout, _stderr) => {
                if (!error) {
                    await configureIntellisense();
                }
                resolve();
            });
        });
    });
}

export function deactivate() {
    chipStatusBarItem?.dispose();
    diagnosticCollection?.dispose();
}
