import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';

export interface PyMcuConfig {
    chip?: string;
    board?: string;
    frequency?: string;
    sources?: string;
    entry?: string;
    stdlib: string[];
    hasFfi: boolean;
    /** Friendly display name: board label if known, else chip, else "(unknown)" */
    displayName: string;
}

const SECTION_RE     = /^\s*\[tool\.(pymcu|whip)\]/;
const FFI_SECTION_RE = /^\s*\[tool\.(pymcu|whip)\.ffi\]/;
const NEW_SECTION_RE = /^\s*\[/;
const KV_RE          = /^\s*(\w+)\s*=\s*["']?([^"'\[\n\r]+?)["']?\s*$/;
const STDLIB_ARRAY_RE = /^\s*stdlib\s*=\s*\[([^\]]*)\]/;

const DISPLAY_NAMES: Record<string, string> = {
    arduino_uno:   'Arduino Uno (atmega328p)',
    arduino_nano:  'Arduino Nano (atmega328p)',
    arduino_mega:  'Arduino Mega (atmega2560)',
    arduino_micro: 'Arduino Micro (atmega32u4)',
    attiny85:      'ATtiny85',
    attiny84:      'ATtiny84',
    attiny2313:    'ATtiny2313',
};

export function parseConfig(content: string): PyMcuConfig | undefined {
    const lines = content.split('\n');
    const hasFfi = lines.some(l => FFI_SECTION_RE.test(l));

    let sectionStart = -1;
    for (let i = 0; i < lines.length; i++) {
        if (SECTION_RE.test(lines[i])) { sectionStart = i + 1; break; }
    }
    if (sectionStart < 0) { return undefined; }

    let chip: string | undefined;
    let board: string | undefined;
    let frequency: string | undefined;
    let sources: string | undefined;
    let entry: string | undefined;
    let stdlib: string[] = [];

    for (let i = sectionStart; i < lines.length; i++) {
        const line = lines[i];
        if (NEW_SECTION_RE.test(line)) { break; }

        const stdlibMatch = STDLIB_ARRAY_RE.exec(line);
        if (stdlibMatch) {
            stdlib = stdlibMatch[1].split(',')
                .map(s => s.trim().replace(/^["']|["']$/g, ''))
                .filter(Boolean);
            continue;
        }

        const kv = KV_RE.exec(line);
        if (!kv) { continue; }
        const [, key, value] = kv;
        switch (key) {
            case 'chip':
            case 'target': chip = value.trim(); break;
            case 'board':  board = value.trim(); break;
            case 'frequency': frequency = value.trim(); break;
            case 'sources':   sources   = value.trim(); break;
            case 'entry':     entry     = value.trim(); break;
        }
    }

    const displayName = board
        ? (DISPLAY_NAMES[board] ?? board)
        : (chip ?? '(unknown)');

    return { chip, board, frequency, sources, entry, stdlib, hasFfi, displayName };
}

export function readConfig(workspaceFolder: vscode.WorkspaceFolder): PyMcuConfig | undefined {
    const p = path.join(workspaceFolder.uri.fsPath, 'pyproject.toml');
    if (!fs.existsSync(p)) { return undefined; }
    try {
        return parseConfig(fs.readFileSync(p, 'utf-8'));
    } catch {
        return undefined;
    }
}

export function findPyprojectToml(): string | undefined {
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) { return undefined; }
    const p = path.join(folder.uri.fsPath, 'pyproject.toml');
    return fs.existsSync(p) ? p : undefined;
}

let _cache: PyMcuConfig | undefined | null = null;

export function clearConfigCache(): void {
    _cache = null;
}

export function getConfig(): PyMcuConfig | undefined {
    if (_cache !== null) { return _cache; }
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) { return undefined; }
    _cache = readConfig(folder) ?? undefined;
    return _cache;
}
