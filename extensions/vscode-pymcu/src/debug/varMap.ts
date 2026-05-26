import * as fs from 'fs';

export interface VarScope {
    function: string;
    file: string;
    startLine: number;
    /** register-allocated variables: varName → register name (e.g. "R4") */
    vars: Record<string, string>;
    /** source line of first assignment for each register var */
    varLines: Record<string, number>;
    /** stack-spilled variables: varName → absolute SRAM address */
    stackVars: Record<string, number>;
    /** source line of first assignment for each stack var */
    stackVarLines: Record<string, number>;
}

export class VarMap {
    constructor(public readonly scopes: VarScope[]) {}

    /** Returns the innermost scope that contains (file, line). */
    getScope(file: string, line: number): VarScope | undefined {
        return this.scopes
            .filter(s => s.file === file && s.startLine <= line)
            .sort((a, b) => b.startLine - a.startLine)[0];
    }

    static load(filePath: string): VarMap | undefined {
        try {
            const text   = fs.readFileSync(filePath, 'utf-8');
            const scopes = JSON.parse(text) as Array<{
                Function: string;
                File: string;
                StartLine: number;
                Vars?: Record<string, string>;
                VarLines?: Record<string, number>;
                StackVars?: Record<string, number>;
                StackVarLines?: Record<string, number>;
            }>;
            return new VarMap(scopes.map(s => ({
                function:      s.Function,
                file:          s.File,
                startLine:     s.StartLine,
                vars:          s.Vars       ?? {},
                varLines:      s.VarLines   ?? {},
                stackVars:     s.StackVars  ?? {},
                stackVarLines: s.StackVarLines ?? {},
            })));
        } catch {
            return undefined;
        }
    }
}

/** Returns the next register name (e.g. "R4" → "R5"), for INT16 high byte. */
export function regPlusOne(reg: string): string {
    const n = parseInt(reg.replace(/^R/i, ''), 10);
    return isNaN(n) ? reg : `R${n + 1}`;
}
