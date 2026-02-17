import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { execSync } from 'child_process';

let chipStatusBarItem: vscode.StatusBarItem;

export function activate(context: vscode.ExtensionContext) {
    console.log('PyMCU extension is now active!');

    checkPymcuInstallation();

    // Register commands
    context.subscriptions.push(
        vscode.commands.registerCommand('pymcu.build', () => runPymcuCommand('build')),
        vscode.commands.registerCommand('pymcu.flash', () => runPymcuCommand('flash')),
        vscode.commands.registerCommand('pymcu.clean', () => runPymcuCommand('clean')),
        vscode.commands.registerCommand('pymcu.new', () => runNewProject())
    );

    // Status bar: show target chip from pyproject.toml
    chipStatusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
    chipStatusBarItem.command = 'pymcu.build';
    chipStatusBarItem.tooltip = 'PyMCU target chip (click to build)';
    context.subscriptions.push(chipStatusBarItem);

    updateChipStatusBar();

    // Watch pyproject.toml for changes to update status bar
    const watcher = vscode.workspace.createFileSystemWatcher('**/pyproject.toml');
    watcher.onDidChange(() => updateChipStatusBar());
    watcher.onDidCreate(() => updateChipStatusBar());
    watcher.onDidDelete(() => updateChipStatusBar());
    context.subscriptions.push(watcher);

    configureIntellisense();
}

function getExecutablePath(): string {
    const config = vscode.workspace.getConfiguration('pymcu');
    return config.get<string>('executablePath') || 'pymcu';
}

function checkPymcuInstallation() {
    try {
        const executable = getExecutablePath();
        execSync(`${executable} --version`, { stdio: 'ignore' });
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
        // Simple TOML parsing for [tool.pymcu] chip field
        const chipMatch = content.match(/\[tool\.pymcu\][\s\S]*?chip\s*=\s*"([^"]+)"/);
        if (chipMatch) {
            chipStatusBarItem.text = `$(circuit-board) ${chipMatch[1]}`;
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
    try {
        const stdlibPath = execSync(
            'python3 -c "import pymcu; from pathlib import Path; print(Path(pymcu.__file__).parent)"',
            { cwd: workspaceFolder.uri.fsPath, encoding: 'utf-8', timeout: 5000 }
        ).trim();

        if (stdlibPath && fs.existsSync(stdlibPath)) {
            // Add parent so `from pymcu.xxx import yyy` resolves
            const parentPath = path.dirname(stdlibPath);
            if (!extraPaths.includes(parentPath)) {
                extraPaths.push(parentPath);
                updated = true;
            }
        }
    } catch {
        // pymcu not installed in current environment — skip
    }

    if (updated) {
        await pythonConfig.update('analysis.extraPaths', extraPaths, vscode.ConfigurationTarget.Workspace);
    }
}

export function deactivate() {
    chipStatusBarItem?.dispose();
}
