import * as vscode from 'vscode';

interface TaskDefinition {
    label: string;
    command: string;
    icon: string;
    description: string;
}

const TASKS: TaskDefinition[] = [
    { label: 'Build',          command: 'pymcu.build',         icon: 'tools',         description: 'Compile and assemble firmware' },
    { label: 'Flash',          command: 'pymcu.flash',         icon: 'zap',           description: 'Flash firmware to the target device' },
    { label: 'Clean',          command: 'pymcu.clean',         icon: 'trash',         description: 'Remove build artefacts' },
    { label: 'Build & Debug',  command: 'pymcu.debugBuild',    icon: 'bug',           description: 'Compile with debug info and launch AVR debugger' },
    { label: 'Serial Monitor', command: 'pymcu.serialMonitor', icon: 'plug',          description: 'Open serial port monitor' },
    { label: 'New Project',    command: 'pymcu.new',           icon: 'new-folder',    description: 'Scaffold a new PyMCU project' },
    { label: 'Configure…',     command: 'pymcu.configureProject', icon: 'settings-gear', description: 'Open project configuration panel' },
];

class TaskItem extends vscode.TreeItem {
    constructor(def: TaskDefinition) {
        super(def.label, vscode.TreeItemCollapsibleState.None);
        this.description = def.description;
        this.iconPath = new vscode.ThemeIcon(def.icon);
        this.contextValue = 'pymcu.task';
        this.command = {
            title: def.label,
            command: def.command,
        };
    }
}

export class ProjectTasksProvider implements vscode.TreeDataProvider<TaskItem> {
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    refresh(): void { this._onDidChangeTreeData.fire(); }

    getTreeItem(element: TaskItem): vscode.TreeItem { return element; }

    getChildren(): TaskItem[] {
        return TASKS.map(t => new TaskItem(t));
    }
}
