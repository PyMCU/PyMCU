import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import { byManufacturer, findById, formatFrequency, type BoardEntry } from './boardRegistry.js';
import { findPyprojectToml } from '../config/configReader.js';

type BoardTreeItem = ManufacturerItem | BoardItem;

class ManufacturerItem extends vscode.TreeItem {
    constructor(
        public readonly manufacturer: string,
        public readonly boards: BoardEntry[]
    ) {
        super(manufacturer, vscode.TreeItemCollapsibleState.Collapsed);
        this.contextValue = 'pymcu.manufacturer';
        this.iconPath = new vscode.ThemeIcon('organization');
    }
}

class BoardItem extends vscode.TreeItem {
    constructor(public readonly board: BoardEntry) {
        super(board.name, vscode.TreeItemCollapsibleState.None);
        this.description = `${board.chip} · ${formatFrequency(board.frequency)}`;
        this.tooltip = new vscode.MarkdownString(
            `**${board.name}**\n\n` +
            `- Chip: \`${board.chip}\`\n` +
            `- Flash: ${board.flashKb} KB\n` +
            `- RAM: ${board.ramBytes} B\n` +
            `- Clock: ${formatFrequency(board.frequency)}\n` +
            `- Arch: ${board.arch.toUpperCase()}`
        );
        this.contextValue = 'pymcu.board';
        this.iconPath = new vscode.ThemeIcon('circuit-board');
        this.command = {
            title: 'Select Board',
            command: 'pymcu.selectBoard',
            arguments: [board.id],
        };
    }
}

export class BoardsTreeProvider implements vscode.TreeDataProvider<BoardTreeItem> {
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    refresh(): void { this._onDidChangeTreeData.fire(); }

    getTreeItem(element: BoardTreeItem): vscode.TreeItem { return element; }

    getChildren(element?: BoardTreeItem): BoardTreeItem[] {
        if (!element) {
            return [...byManufacturer.entries()].map(
                ([mfr, boards]) => new ManufacturerItem(mfr, boards)
            );
        }
        if (element instanceof ManufacturerItem) {
            return element.boards.map(b => new BoardItem(b));
        }
        return [];
    }
}

/** Patches pyproject.toml to set a new board (and its default chip + frequency). */
export async function selectBoard(boardId: string): Promise<void> {
    const pyprojectPath = findPyprojectToml();
    const board = findById(boardId);
    if (!board) { return; }

    if (!pyprojectPath) {
        vscode.window.showErrorMessage('No pyproject.toml found. Open a PyMCU project first.');
        return;
    }

    let content = fs.readFileSync(pyprojectPath, 'utf-8');
    content = patchOrAppend(content, 'board',     `"${board.id}"`,          'tool.pymcu');
    content = patchOrAppend(content, 'chip',      `"${board.chip}"`,        'tool.pymcu');
    content = patchOrAppend(content, 'frequency', String(board.frequency),  'tool.pymcu');
    fs.writeFileSync(pyprojectPath, content, 'utf-8');

    vscode.window.showInformationMessage(
        `PyMCU: target set to ${board.name} (${board.chip})`
    );

    // Open pyproject.toml so the user sees the change
    const doc = await vscode.workspace.openTextDocument(pyprojectPath);
    vscode.window.showTextDocument(doc, { preview: true });
}

function patchOrAppend(content: string, key: string, value: string, section: string): string {
    const sectionHeader = `[${section}]`;
    const keyRe = new RegExp(`(\\[${section.replace('.', '\\.')}\\][\\s\\S]*?)^${key}\\s*=\\s*.*$`, 'm');
    if (keyRe.test(content)) {
        return content.replace(keyRe, (_, pre) => `${pre}${key} = ${value}`);
    }
    if (content.includes(sectionHeader)) {
        const idx = content.indexOf(sectionHeader) + sectionHeader.length;
        const rest = content.slice(idx);
        const next = rest.match(/^\s*\n\[/m);
        const insertAt = next ? idx + (next.index ?? 0) + 1 : content.length;
        return content.slice(0, insertAt) + `${key} = ${value}\n` + content.slice(insertAt);
    }
    return content + `\n[${section}]\n${key} = ${value}\n`;
}

/** Quick-pick board selector used by the status-bar chip badge and the wizard. */
export async function quickPickBoard(): Promise<BoardEntry | undefined> {
    type BoardPickItem = vscode.QuickPickItem & { board?: BoardEntry };

    const items: BoardPickItem[] = [];
    for (const [mfr, boards] of byManufacturer) {
        items.push({ label: mfr, kind: vscode.QuickPickItemKind.Separator });
        for (const b of boards) {
            items.push({
                board: b,
                label: b.name,
                description: `${b.chip} · ${formatFrequency(b.frequency)} · ${b.flashKb} KB flash`,
            });
        }
    }

    const pick = await vscode.window.showQuickPick(items, {
        title: 'PyMCU: Select Target Board',
        placeHolder: 'Filter boards by name or chip…',
        matchOnDescription: true,
    });

    return pick?.board;
}

/** Opens a terminal to show the board pinout image using the `extra.pins` pattern. */
export function showBoardPinout(boardId: string): void {
    const board = findById(boardId);
    if (!board) { return; }
    vscode.env.openExternal(
        vscode.Uri.parse(`https://pymcu.dev/boards/${boardId}`)
    );
}

/** Returns the path to the workspace folder pyproject.toml. */
export function findPyproject(): string | undefined {
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) { return undefined; }
    const p = path.join(folder.uri.fsPath, 'pyproject.toml');
    return fs.existsSync(p) ? p : undefined;
}
