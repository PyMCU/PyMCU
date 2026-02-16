import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';

// This method is called when your extension is activated
// Your extension is activated the very first time the command is executed
export function activate(context: vscode.ExtensionContext) {
	console.log('PyMCU extension is now active!');

	// Register Commands
	context.subscriptions.push(
		vscode.commands.registerCommand('pymcu.build', () => runPymcuCommand('build')),
		vscode.commands.registerCommand('pymcu.flash', () => runPymcuCommand('flash')),
		vscode.commands.registerCommand('pymcu.clean', () => runPymcuCommand('clean')),
		vscode.commands.registerCommand('pymcu.new', () => runPymcuCommand('new'))
	);

	// Initial check for PyMCU project and Intellisense setup
	configureIntellisense();
}

// This method is called when your extension is deactivated
export function deactivate() { }

async function runPymcuCommand(command: string) {
	const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
	if (!workspaceFolder) {
		vscode.window.showErrorMessage('No workspace folder open');
		return;
	}

	const config = vscode.workspace.getConfiguration('pymcu');
	let executable = config.get<string>('executablePath');
	let args = [command];

	// Auto-detect package manager if executable is not explicitly set
	if (!executable || executable === 'pymcu') {
		const rootPath = workspaceFolder.uri.fsPath;
		if (fs.existsSync(path.join(rootPath, 'uv.lock'))) {
			executable = 'uv';
			args = ['run', 'pymcu', command];
		} else if (fs.existsSync(path.join(rootPath, 'poetry.lock'))) {
			executable = 'poetry';
			args = ['run', 'pymcu', command];
		} else if (fs.existsSync(path.join(rootPath, 'Pipfile'))) {
			executable = 'pipenv';
			args = ['run', 'pymcu', command];
		} else if (fs.existsSync(path.join(rootPath, '.venv'))) {
			// If .venv exists, try to use the binary directly
			const venvBin = process.platform === 'win32'
				? path.join(rootPath, '.venv', 'Scripts', 'pymcu.exe')
				: path.join(rootPath, '.venv', 'bin', 'pymcu');

			if (fs.existsSync(venvBin)) {
				executable = venvBin;
				args = [command];
			} else {
				executable = 'pymcu'; // Fallback
			}
		} else {
			executable = 'pymcu'; // Fallback to global path
		}
	}

	const commandString = `${executable} ${args.join(' ')}`;
	// If it's uv/poetry/pipenv, the command is 'exec args...', otherwise 'exec command'
	// For ShellExecution:
	// If executable is 'uv', commandLine is 'uv run pymcu build'

	// Create a Task for the command
	const task = new vscode.Task(
		{ type: 'pymcu', command: command },
		workspaceFolder,
		`pymcu ${command}`,
		'pymcu',
		new vscode.ShellExecution(commandString),
		['$pymcuc'] // Use the problem matcher defined in package.json
	);

	// Execute the task
	try {
		await vscode.tasks.executeTask(task);
	} catch (e) {
		vscode.window.showErrorMessage(`Failed to execute ${commandString}: ${e}`);
	}
}

async function configureIntellisense() {
	// Attempt to find the bundled lib/src in the development environment
	// OR assume standard installation.

	// If we are developing PyMCU itself, we might want to point to specific paths.
	// However, for end users, assuming 'pymcu' is installed in the python environment is standard.

	// We can check if 'pymcu' is resolvable in the current python interpreter
	// by using the Python extension API, but for now we'll keep it simple.

	// If the user has a local lib/src copy (e.g. they are working on the compiler repo),
	// we could try to add it to extraPaths.

	const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
	if (workspaceFolder) {
		const localLibPath = path.join(workspaceFolder.uri.fsPath, 'lib', 'src');
		if (fs.existsSync(localLibPath)) {
			const pythonConfig = vscode.workspace.getConfiguration('python', workspaceFolder.uri);
			const extraPaths = pythonConfig.get<string[]>('analysis.extraPaths') || [];

			if (!extraPaths.includes(localLibPath)) {
				extraPaths.push(localLibPath);
				await pythonConfig.update('analysis.extraPaths', extraPaths, vscode.ConfigurationTarget.Workspace);
				vscode.window.showInformationMessage('PyMCU: Added local library paths to Python analysis.');
			}
		}
	}
}
