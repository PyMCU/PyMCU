import * as vscode from 'vscode';
import { getConfig } from '../config/configReader.js';

const PRIORITY_BASE = 50;

interface ButtonDef {
    id: string;
    text: string;
    tooltip: string;
    command: string;
    priority: number;
}

const BUTTON_DEFS: ButtonDef[] = [
    { id: 'build',   text: '$(tools) Build',   tooltip: 'PyMCU: Build',          command: 'pymcu.build',         priority: 4 },
    { id: 'flash',   text: '$(zap) Flash',     tooltip: 'PyMCU: Flash',          command: 'pymcu.flash',         priority: 3 },
    { id: 'clean',   text: '$(trash) Clean',   tooltip: 'PyMCU: Clean',          command: 'pymcu.clean',         priority: 2 },
    { id: 'monitor', text: '$(plug) Monitor',  tooltip: 'PyMCU: Serial Monitor', command: 'pymcu.serialMonitor', priority: 1 },
];

export class PyMcuStatusBar {
    private readonly buttons: vscode.StatusBarItem[] = [];
    private readonly chipBadge: vscode.StatusBarItem;
    private readonly disposables: vscode.Disposable[] = [];

    constructor() {
        for (const def of BUTTON_DEFS) {
            const item = vscode.window.createStatusBarItem(
                `pymcu-toolbar-${def.id}`,
                vscode.StatusBarAlignment.Left,
                PRIORITY_BASE + def.priority
            );
            item.name = def.tooltip;
            item.text = def.text;
            item.tooltip = def.tooltip;
            item.command = def.command;
            this.buttons.push(item);
        }

        this.chipBadge = vscode.window.createStatusBarItem(
            'pymcu-chip',
            vscode.StatusBarAlignment.Left,
            PRIORITY_BASE
        );
        this.chipBadge.name = 'PyMCU Target';
        this.chipBadge.command = 'pymcu.configureProject';

        this.update();
    }

    update(): void {
        const config = getConfig();
        if (!config) {
            this.hide();
            return;
        }

        for (const btn of this.buttons) { btn.show(); }

        this.chipBadge.text = `$(circuit-board) ${config.displayName}`;
        const parts: string[] = [`PyMCU: ${config.displayName}`];
        if (config.frequency) { parts.push(`@ ${Number(config.frequency).toLocaleString()} Hz`); }
        if (config.stdlib.length > 0) { parts.push(config.stdlib.join(', ')); }
        if (config.hasFfi) { parts.push('C/C++ FFI'); }
        this.chipBadge.tooltip = parts.join(' · ');
        this.chipBadge.show();
    }

    hide(): void {
        for (const btn of this.buttons) { btn.hide(); }
        this.chipBadge.hide();
    }

    dispose(): void {
        for (const btn of this.buttons) { btn.dispose(); }
        this.chipBadge.dispose();
        for (const d of this.disposables) { d.dispose(); }
    }

    getDisposables(): vscode.Disposable[] {
        return [...this.buttons, this.chipBadge];
    }
}
