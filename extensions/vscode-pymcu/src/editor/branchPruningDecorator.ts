import * as vscode from 'vscode';
import { getConfig } from '../config/configReader.js';

const PRUNED_DECORATION = vscode.window.createTextEditorDecorationType({
    opacity: '0.35',
    textDecoration: 'none',
});

function chipToArch(chip: string): string | undefined {
    const c = chip.toLowerCase();
    if (c.startsWith('at')) { return 'avr'; }
    if (c.startsWith('pic') || /^(12|16|18)f/.test(c)) { return 'pic'; }
    if (c.includes('riscv') || c.includes('risc-v')) { return 'risc-v'; }
    return undefined;
}

function patternCouldMatch(patternText: string, targetValue: string): boolean {
    const t = patternText.trim();
    if (t === '_') { return true; }   // wildcard
    if (!t.includes('.') && !t.startsWith('"') && !t.startsWith("'")) {
        return true;   // capture pattern — always matches
    }
    // String / dotted-name pattern: extract the leaf value
    const unquoted = t.replace(/^["']|["']$/g, '');
    const leaf = unquoted.includes('.') ? unquoted.split('.').pop()! : unquoted;
    return leaf.toLowerCase() === targetValue.toLowerCase();
}

/** Returns all case-clause ranges that are guaranteed unreachable for the current target. */
function computePrunedRanges(document: vscode.TextDocument, chip: string): vscode.Range[] {
    const text  = document.getText();
    const lines = text.split('\n');
    const ranges: vscode.Range[] = [];

    // We need to find `match __CHIP__.arch:`, `match __CHIP__.name:`, `match __CHIP__:`
    // then collect case blocks, then grey out the ones that cannot match.
    const MATCH_PATTERNS: { re: RegExp; attr: 'arch' | 'name' }[] = [
        { re: /^\s*match\s+__CHIP__\.arch\s*:/, attr: 'arch' },
        { re: /^\s*match\s+__CHIP__(?:\.name|\.chip)?\s*:/, attr: 'name' },
    ];

    for (let i = 0; i < lines.length; i++) {
        let matchAttr: 'arch' | 'name' | undefined;
        for (const mp of MATCH_PATTERNS) {
            if (mp.re.test(lines[i])) { matchAttr = mp.attr; break; }
        }
        if (!matchAttr) { continue; }

        const targetValue = matchAttr === 'arch'
            ? (chipToArch(chip) ?? chip)
            : chip;

        // Determine indent level of the match statement
        const matchIndent = lines[i].match(/^(\s*)/)?.[1].length ?? 0;

        // Collect case blocks starting after line i
        let j = i + 1;
        while (j < lines.length) {
            const line = lines[j];
            if (line.trim() === '') { j++; continue; }
            const lineIndent = line.match(/^(\s*)/)?.[1].length ?? 0;

            // A case at indent = matchIndent + <caseIndent>
            if (lineIndent <= matchIndent && line.trim() !== '') { break; } // out of match block

            const caseMatch = line.match(/^(\s*)case\s+(.*?)\s*:/);
            if (!caseMatch) { j++; continue; }

            const caseStartLine = j;
            const caseIndent    = caseMatch[1].length;
            const patternText   = caseMatch[2];

            // Find the end of this case block (next `case` at same indent or out)
            let k = j + 1;
            while (k < lines.length) {
                const nextLine = lines[k];
                if (nextLine.trim() === '') { k++; continue; }
                const nextIndent = nextLine.match(/^(\s*)/)?.[1].length ?? 0;
                if (nextIndent <= caseIndent && nextLine.trim() !== '') { break; }
                k++;
            }

            if (!patternCouldMatch(patternText, targetValue)) {
                const startPos = new vscode.Position(caseStartLine, 0);
                const endPos   = new vscode.Position(k - 1, lines[k - 1]?.length ?? 0);
                ranges.push(new vscode.Range(startPos, endPos));
            }

            j = k;
        }
    }

    return ranges;
}

function applyDecorations(editor: vscode.TextEditor): void {
    const config = getConfig();
    const chip   = config?.chip ?? (config?.board ? undefined : undefined);
    if (!chip) {
        editor.setDecorations(PRUNED_DECORATION, []);
        return;
    }
    if (editor.document.languageId !== 'python') {
        editor.setDecorations(PRUNED_DECORATION, []);
        return;
    }
    const ranges = computePrunedRanges(editor.document, chip);
    editor.setDecorations(PRUNED_DECORATION, ranges);
}

export function registerBranchPruningDecorator(context: vscode.ExtensionContext): void {
    // Apply on open editors
    for (const editor of vscode.window.visibleTextEditors) {
        applyDecorations(editor);
    }

    context.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor(editor => {
            if (editor) { applyDecorations(editor); }
        }),
        vscode.workspace.onDidChangeTextDocument(event => {
            for (const editor of vscode.window.visibleTextEditors) {
                if (editor.document === event.document) {
                    applyDecorations(editor);
                }
            }
        }),
        vscode.workspace.createFileSystemWatcher('**/pyproject.toml').onDidChange(() => {
            for (const editor of vscode.window.visibleTextEditors) {
                applyDecorations(editor);
            }
        }),
    );
}
