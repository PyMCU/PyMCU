import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import { getConfig } from '../config/configReader.js';
import { findById, byManufacturer, formatFrequency, type BoardEntry } from '../boards/boardRegistry.js';

interface PyMCUConfig {
    board: string;
    chip: string;
    frequency: number;
    programmer: string;
    port: string;
    baud: number;
    fuseLow: string;
    fuseHigh: string;
    fuseExt: string;
    stdlib: string[];
    hasFfi: boolean;
}

function detectArch(chip: string): 'AVR' | 'PIC' | 'RISC-V' | 'Unknown' {
    const c = chip.toLowerCase();
    if (c.startsWith('at')) { return 'AVR'; }
    if (c.startsWith('pic') || /^(12|16|18)f/.test(c)) { return 'PIC'; }
    if (c.includes('riscv') || c.includes('risc-v')) { return 'RISC-V'; }
    return 'Unknown';
}

function readPyproject(p: string): PyMCUConfig {
    const content = fs.readFileSync(p, 'utf-8');
    const cfg = getConfig();

    const get = (key: string, def = ''): string => {
        const m = content.match(new RegExp(`^${key}\\s*=\\s*"([^"]*)"`, 'm'));
        return m ? m[1] : def;
    };
    const getNum = (key: string, def: number): number => {
        const m = content.match(new RegExp(`^${key}\\s*=\\s*(\\d+)`, 'm'));
        return m ? parseInt(m[1], 10) : def;
    };

    const boardId = cfg?.board ?? get('board', '');
    const boardEntry = findById(boardId);
    const chip = cfg?.chip ?? boardEntry?.chip ?? get('chip', get('target', ''));

    return {
        board:      boardId,
        chip,
        frequency:  boardEntry?.frequency ?? getNum('frequency', 16_000_000),
        programmer: get('programmer', ''),
        port:       get('port', ''),
        baud:       getNum('baud', 115200),
        fuseLow:    get('fuse_low', ''),
        fuseHigh:   get('fuse_high', ''),
        fuseExt:    get('fuse_ext', ''),
        stdlib:     cfg?.stdlib ?? [],
        hasFfi:     cfg?.hasFfi ?? false,
    };
}

function autoDetectPort(): string {
    try {
        if (process.platform === 'darwin' || process.platform === 'linux') {
            const prefixes = process.platform === 'darwin'
                ? ['cu.usbmodem', 'cu.usbserial']
                : ['ttyACM', 'ttyUSB'];
            for (const prefix of prefixes) {
                const match = fs.readdirSync('/dev').find(e => e.startsWith(prefix));
                if (match) { return path.join('/dev', match); }
            }
        }
    } catch { /* ignore */ }
    return '';
}

function patchToml(content: string, key: string, value: string, section: string): string {
    const sectionRe = new RegExp(`(\\[${section.replace('.', '\\.')}\\][\\s\\S]*?)^${key}\\s*=\\s*.*$`, 'm');
    if (sectionRe.test(content)) {
        return content.replace(sectionRe, (_, pre) => `${pre}${key} = ${value}`);
    }
    const header = `[${section}]`;
    if (content.includes(header)) {
        const idx = content.indexOf(header) + header.length;
        const rest = content.slice(idx);
        const next = rest.match(/^\s*\n\[/m);
        const insertAt = next ? idx + (next.index ?? 0) + 1 : content.length;
        return content.slice(0, insertAt) + `${key} = ${value}\n` + content.slice(insertAt);
    }
    return content + `\n[${section}]\n${key} = ${value}\n`;
}

function writePyproject(p: string, config: PyMCUConfig): void {
    let content = fs.readFileSync(p, 'utf-8');
    const set    = (k: string, v: string, s: string) => { if (v) { content = patchToml(content, k, `"${v}"`, s); } };
    const setNum = (k: string, v: number, s: string) => { content = patchToml(content, k, String(v), s); };

    if (config.board) { set('board', config.board, 'tool.pymcu'); }
    if (config.chip)  { set('chip',  config.chip,  'tool.pymcu'); }
    setNum('frequency', config.frequency, 'tool.pymcu');

    if (config.programmer) { set('programmer', config.programmer, 'tool.pymcu.flash'); }
    if (config.port)       { set('port',       config.port,       'tool.pymcu.flash'); }
    if (config.baud && config.baud !== 115200) { setNum('baud', config.baud, 'tool.pymcu.flash'); }

    const arch = detectArch(config.chip);
    if (arch === 'AVR') {
        if (config.fuseLow)  { set('fuse_low',  config.fuseLow,  'tool.pymcu.flash'); }
        if (config.fuseHigh) { set('fuse_high', config.fuseHigh, 'tool.pymcu.flash'); }
        if (config.fuseExt)  { set('fuse_ext',  config.fuseExt,  'tool.pymcu.flash'); }
    }

    // stdlib
    if (config.stdlib.length > 0) {
        const arr = `[${config.stdlib.map(s => `"${s}"`).join(', ')}]`;
        content = patchToml(content, 'stdlib', arr, 'tool.pymcu');
    }

    // ffi section
    const ffiHeader = '[tool.pymcu.ffi]';
    if (config.hasFfi && !content.includes(ffiHeader)) {
        content += `\n${ffiHeader}\n`;
    } else if (!config.hasFfi && content.includes(ffiHeader)) {
        content = content.replace(`\n${ffiHeader}\n`, '\n').replace(`${ffiHeader}\n`, '');
    }

    fs.writeFileSync(p, content, 'utf-8');
}

function buildBoardOptionsHtml(): string {
    const lines: string[] = [];
    for (const [mfr, boards] of byManufacturer) {
        lines.push(`<optgroup label="${mfr}">`);
        for (const b of boards) {
            lines.push(`<option value="${b.id}" data-chip="${b.chip}" data-freq="${b.frequency}" data-flash="${b.flashKb}" data-ram="${b.ramBytes}">${b.name}</option>`);
        }
        lines.push('</optgroup>');
    }
    lines.push('<option value="__custom__">Custom chip…</option>');
    return lines.join('\n');
}

export class ProjectConfigPanel {
    private static current: ProjectConfigPanel | undefined;
    private readonly panel: vscode.WebviewPanel;
    private readonly pyprojectPath: string;
    private disposables: vscode.Disposable[] = [];

    static createOrShow(context: vscode.ExtensionContext): void {
        const folder = vscode.workspace.workspaceFolders?.[0];
        if (!folder) {
            vscode.window.showErrorMessage('No workspace folder open.');
            return;
        }
        const pyprojectPath = path.join(folder.uri.fsPath, 'pyproject.toml');
        if (!fs.existsSync(pyprojectPath)) {
            vscode.window.showErrorMessage('No pyproject.toml found in the current workspace.');
            return;
        }

        if (ProjectConfigPanel.current) {
            ProjectConfigPanel.current.panel.reveal(vscode.ViewColumn.One);
            return;
        }

        const panel = vscode.window.createWebviewPanel(
            'pymcuConfig',
            'PyMCU Project Configuration',
            vscode.ViewColumn.One,
            { enableScripts: true, retainContextWhenHidden: true }
        );

        ProjectConfigPanel.current = new ProjectConfigPanel(panel, pyprojectPath, context);
    }

    private constructor(
        panel: vscode.WebviewPanel,
        pyprojectPath: string,
        _context: vscode.ExtensionContext
    ) {
        this.panel = panel;
        this.pyprojectPath = pyprojectPath;
        this.panel.webview.html = this.buildHtml(readPyproject(pyprojectPath));

        this.panel.webview.onDidReceiveMessage(msg => {
            switch (msg.command) {
                case 'detect_port': {
                    const port = autoDetectPort();
                    this.panel.webview.postMessage({ command: 'port_detected', port });
                    break;
                }
                case 'save': {
                    try {
                        writePyproject(this.pyprojectPath, msg.config as PyMCUConfig);
                        vscode.window.showInformationMessage('PyMCU project configuration saved.');
                        this.panel.dispose();
                    } catch (e) {
                        vscode.window.showErrorMessage(`Failed to save configuration: ${e}`);
                    }
                    break;
                }
                case 'cancel':
                    this.panel.dispose();
                    break;
            }
        }, null, this.disposables);

        this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    }

    private dispose(): void {
        ProjectConfigPanel.current = undefined;
        this.panel.dispose();
        for (const d of this.disposables) { d.dispose(); }
        this.disposables = [];
    }

    private buildHtml(cfg: PyMCUConfig): string {
        const arch = detectArch(cfg.chip);
        const archColors: Record<string, string> = {
            AVR: '#4c8eda', PIC: '#e07b39', 'RISC-V': '#6abf69', Unknown: '#888',
        };
        const avrVisible = arch === 'AVR' ? '' : 'display:none';

        const boardEntry = findById(cfg.board);
        const boardOptions = buildBoardOptionsHtml();

        const STDLIBS = ['circuitpython', 'pio', 'riscv'];
        const stdlibCheckboxes = STDLIBS.map(s =>
            `<label class="checkbox-label">
              <input type="checkbox" name="stdlib" value="${s}" ${cfg.stdlib.includes(s) ? 'checked' : ''}> ${s}
            </label>`
        ).join('\n');

        return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>PyMCU Project Configuration</title>
<style>
  :root { --gap: 14px; }
  body { font-family: var(--vscode-font-family); color: var(--vscode-foreground);
         background: var(--vscode-editor-background); padding: 24px; max-width: 600px; }
  h2 { margin-top: 0; display: flex; align-items: center; gap: 8px; }
  .badge { display: inline-block; padding: 2px 10px; border-radius: 12px;
           font-size: 11px; font-weight: 700; letter-spacing: 0.5px;
           color: #fff; background: ${archColors[arch]}; }
  .board-specs { font-size: 11px; opacity: 0.7; margin-top: 4px; min-height: 16px; }
  label { display: block; margin-top: var(--gap); font-size: 12px; opacity: 0.8; }
  input[type=text], input[type=number], select {
    width: 100%; box-sizing: border-box; padding: 5px 8px; margin-top: 4px;
    background: var(--vscode-input-background); color: var(--vscode-input-foreground);
    border: 1px solid var(--vscode-input-border, #555); border-radius: 3px; font-size: 13px; }
  .row { display: flex; gap: 8px; align-items: flex-end; }
  .row input { flex: 1; }
  button { margin-top: 6px; padding: 5px 14px; border: none; border-radius: 3px;
           cursor: pointer; font-size: 13px;
           background: var(--vscode-button-background);
           color: var(--vscode-button-foreground); }
  button:hover { background: var(--vscode-button-hoverBackground); }
  .section { margin-top: 16px; padding: 12px;
             border: 1px solid var(--vscode-input-border, #555); border-radius: 4px; }
  .section-title { font-size: 11px; opacity: 0.7; text-transform: uppercase; font-weight: 700;
                   margin: 0 0 8px; }
  .fuse-row { display: flex; gap: 10px; }
  .fuse-row label { flex: 1; }
  .checkbox-group { display: flex; flex-wrap: wrap; gap: 12px; margin-top: 6px; }
  .checkbox-label { display: flex; align-items: center; gap: 6px; font-size: 13px; opacity: 1;
                    margin: 0; cursor: pointer; }
  hr { margin: 20px 0; border: none; border-top: 1px solid var(--vscode-input-border, #555); }
  .actions { margin-top: 20px; display: flex; gap: 10px; }
  #cancel-btn { background: var(--vscode-button-secondaryBackground, #444);
                color: var(--vscode-button-secondaryForeground, #ddd); }
</style>
</head>
<body>
<h2>
  PyMCU Configuration
  <span class="badge" id="arch-badge">${arch}</span>
</h2>

<label>Target Board
  <select id="board" onchange="onBoardChange(this)">
    ${boardOptions}
  </select>
</label>
<div class="board-specs" id="board-specs">${boardEntry ? `${boardEntry.chip} · ${formatFrequency(boardEntry.frequency)} · ${boardEntry.flashKb} KB flash · ${boardEntry.ramBytes} B RAM` : ''}</div>

<div id="custom-chip-row" style="display:none; margin-top:6px">
  <label>Custom Chip ID
    <input id="custom-chip" type="text" placeholder="e.g. atmega1284p" value="${cfg.chip}" />
  </label>
</div>

<label>Clock Frequency (Hz)
  <input id="frequency" type="number" min="1000" max="240000000" value="${cfg.frequency}" />
</label>

<hr>
<label>Flash Programmer
  <select id="programmer">
    <option value="" ${cfg.programmer === '' ? 'selected' : ''}>Auto (based on chip)</option>
    <option value="avrdude" ${cfg.programmer === 'avrdude' ? 'selected' : ''}>avrdude (AVR)</option>
    <option value="pk2cmd"  ${cfg.programmer === 'pk2cmd'  ? 'selected' : ''}>pk2cmd (PIC)</option>
  </select>
</label>

<label>Serial Port
  <div class="row">
    <input id="port" type="text" placeholder="/dev/cu.usbmodem… or COM3" value="${cfg.port}" />
    <button onclick="detectPort()">Detect</button>
  </div>
</label>

<label>Baud Rate
  <input id="baud" type="number" min="300" max="4000000" value="${cfg.baud}" />
</label>

<div id="fuses-section" class="section" style="${avrVisible}">
  <p class="section-title">AVR Fuse Bits (hex, optional)</p>
  <div class="fuse-row">
    <label>Low Fuse  <input id="fuse-low"  type="text" placeholder="0xFF" value="${cfg.fuseLow}"  maxlength="4" /></label>
    <label>High Fuse <input id="fuse-high" type="text" placeholder="0xDE" value="${cfg.fuseHigh}" maxlength="4" /></label>
    <label>Ext Fuse  <input id="fuse-ext"  type="text" placeholder="0xFF" value="${cfg.fuseExt}"  maxlength="4" /></label>
  </div>
</div>

<div class="section" style="margin-top:16px">
  <p class="section-title">Standard Libraries</p>
  <div class="checkbox-group">
    ${stdlibCheckboxes}
  </div>
</div>

<div class="section" style="margin-top:16px">
  <p class="section-title">C/C++ FFI</p>
  <label class="checkbox-label" style="margin-top:4px">
    <input type="checkbox" id="ffi" ${cfg.hasFfi ? 'checked' : ''}> Enable <code>[tool.pymcu.ffi]</code> section
  </label>
</div>

<div class="actions">
  <button onclick="save()">Save</button>
  <button id="cancel-btn" onclick="cancel()">Cancel</button>
</div>

<script>
  const vscode = acquireVsCodeApi();
  const archColors = { AVR:'#4c8eda', PIC:'#e07b39', 'RISC-V':'#6abf69', Unknown:'#888' };

  function detectArch(chip) {
    const c = chip.toLowerCase();
    if (c.startsWith('at')) return 'AVR';
    if (c.startsWith('pic') || /^(12|16|18)f/.test(c)) return 'PIC';
    if (c.includes('riscv') || c.includes('risc-v')) return 'RISC-V';
    return 'Unknown';
  }

  function getChip() {
    const sel = document.getElementById('board').value;
    if (sel === '__custom__') return document.getElementById('custom-chip').value.trim();
    const opt = document.querySelector('#board option[value="' + sel + '"]');
    return opt ? opt.dataset.chip : sel;
  }

  function onBoardChange(sel) {
    const val = sel.value;
    const isCustom = val === '__custom__';
    document.getElementById('custom-chip-row').style.display = isCustom ? '' : 'none';
    if (isCustom) { updateArch(document.getElementById('custom-chip').value); return; }
    const opt = sel.options[sel.selectedIndex];
    if (!opt) return;
    const chip = opt.dataset.chip || '';
    const freq = opt.dataset.freq || '';
    const flash = opt.dataset.flash || '';
    const ram = opt.dataset.ram || '';
    document.getElementById('board-specs').textContent =
      chip + (freq ? ' · ' + (freq / 1e6) + ' MHz' : '') +
      (flash ? ' · ' + flash + ' KB flash' : '') +
      (ram ? ' · ' + ram + ' B RAM' : '');
    if (freq) document.getElementById('frequency').value = freq;
    updateArch(chip);
  }

  function updateArch(chip) {
    const arch = detectArch(chip);
    const badge = document.getElementById('arch-badge');
    badge.textContent = arch;
    badge.style.background = archColors[arch] || '#888';
    document.getElementById('fuses-section').style.display = arch === 'AVR' ? '' : 'none';
  }

  function detectPort() { vscode.postMessage({ command: 'detect_port' }); }

  function save() {
    const stdlib = [...document.querySelectorAll('input[name=stdlib]:checked')].map(e => e.value);
    vscode.postMessage({
      command: 'save',
      config: {
        board:      document.getElementById('board').value === '__custom__' ? '' : document.getElementById('board').value,
        chip:       getChip(),
        frequency:  parseInt(document.getElementById('frequency').value, 10) || 16000000,
        programmer: document.getElementById('programmer').value,
        port:       document.getElementById('port').value.trim(),
        baud:       parseInt(document.getElementById('baud').value, 10) || 115200,
        fuseLow:    document.getElementById('fuse-low').value.trim(),
        fuseHigh:   document.getElementById('fuse-high').value.trim(),
        fuseExt:    document.getElementById('fuse-ext').value.trim(),
        stdlib,
        hasFfi:     document.getElementById('ffi').checked,
      }
    });
  }

  function cancel() { vscode.postMessage({ command: 'cancel' }); }

  window.addEventListener('message', event => {
    if (event.data.command === 'port_detected') {
      if (event.data.port) document.getElementById('port').value = event.data.port;
      else alert('No serial port detected. Connect your device and try again.');
    }
  });

  // Init: set board selector to current board or custom
  (function init() {
    const boardSel = document.getElementById('board');
    const currentBoard = ${JSON.stringify(cfg.board)};
    const knownIds = [...boardSel.options].map(o => o.value).filter(v => v !== '__custom__');
    if (currentBoard && knownIds.includes(currentBoard)) {
      boardSel.value = currentBoard;
      onBoardChange(boardSel);
    } else if (currentBoard) {
      boardSel.value = '__custom__';
      document.getElementById('custom-chip-row').style.display = '';
      document.getElementById('custom-chip').value = ${JSON.stringify(cfg.chip)};
      updateArch(${JSON.stringify(cfg.chip)});
    }
  })();
</script>
</body>
</html>`;
    }
}
