import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';

interface PyMCUConfig {
    chip: string;
    frequency: number;
    sources: string;
    entry: string;
    programmer: string;
    port: string;
    baud: number;
    fuseLow: string;
    fuseHigh: string;
    fuseExt: string;
}

function detectArchitecture(chip: string): 'AVR' | 'PIC' | 'RISC-V' | 'Unknown' {
    const c = chip.toLowerCase();
    if (c.startsWith('at')) { return 'AVR'; }
    if (c.startsWith('pic') || /^(12|16|18)f/.test(c)) { return 'PIC'; }
    if (c.includes('riscv') || c.includes('risc-v')) { return 'RISC-V'; }
    return 'Unknown';
}

function readPyproject(pyprojectPath: string): PyMCUConfig {
    const content = fs.readFileSync(pyprojectPath, 'utf-8');
    const get = (key: string, def = ''): string => {
        const m = content.match(new RegExp(`^${key}\\s*=\\s*"([^"]*)"`, 'm'));
        return m ? m[1] : def;
    };
    const getNum = (key: string, def: number): number => {
        const m = content.match(new RegExp(`^${key}\\s*=\\s*(\\d+)`, 'm'));
        return m ? parseInt(m[1], 10) : def;
    };
    return {
        chip:       get('chip', ''),
        frequency:  getNum('frequency', 16000000),
        sources:    get('sources', 'src'),
        entry:      get('entry', 'main.py'),
        programmer: get('programmer', ''),
        port:       get('port', ''),
        baud:       getNum('baud', 115200),
        fuseLow:    get('fuse_low', ''),
        fuseHigh:   get('fuse_high', ''),
        fuseExt:    get('fuse_ext', ''),
    };
}

function patchToml(content: string, key: string, value: string, section: string): string {
    // Try to update existing key inside the given section
    const sectionRe = new RegExp(`(\\[${section.replace('.', '\\.')}\\][\\s\\S]*?)^${key}\\s*=\\s*.*$`, 'm');
    if (sectionRe.test(content)) {
        return content.replace(sectionRe, (_, pre) => `${pre}${key} = ${value}`);
    }
    // Section exists but key is missing — append before next section or EOF
    const sectionHeader = `[${section}]`;
    if (content.includes(sectionHeader)) {
        const idx = content.indexOf(sectionHeader) + sectionHeader.length;
        const rest = content.slice(idx);
        const nextSection = rest.match(/^\s*\n\[/m);
        const insertAt = nextSection ? idx + nextSection.index! + 1 : content.length;
        return content.slice(0, insertAt) + `${key} = ${value}\n` + content.slice(insertAt);
    }
    // Section missing — append at end
    return content + `\n[${section}]\n${key} = ${value}\n`;
}

function writePyproject(pyprojectPath: string, config: PyMCUConfig): void {
    let content = fs.readFileSync(pyprojectPath, 'utf-8');

    const set = (key: string, val: string, section: string) => {
        if (val !== '') {
            content = patchToml(content, key, `"${val}"`, section);
        }
    };
    const setNum = (key: string, val: number, section: string) => {
        content = patchToml(content, key, String(val), section);
    };

    set('chip', config.chip, 'tool.pymcu');
    setNum('frequency', config.frequency, 'tool.pymcu');

    if (config.programmer) { set('programmer', config.programmer, 'tool.pymcu.flash'); }
    if (config.port)       { set('port', config.port, 'tool.pymcu.flash'); }
    if (config.baud && config.baud !== 115200) { setNum('baud', config.baud, 'tool.pymcu.flash'); }

    const arch = detectArchitecture(config.chip);
    if (arch === 'AVR') {
        if (config.fuseLow)  { set('fuse_low',  config.fuseLow,  'tool.pymcu.flash'); }
        if (config.fuseHigh) { set('fuse_high', config.fuseHigh, 'tool.pymcu.flash'); }
        if (config.fuseExt)  { set('fuse_ext',  config.fuseExt,  'tool.pymcu.flash'); }
    }

    fs.writeFileSync(pyprojectPath, content, 'utf-8');
}

function autoDetectPort(): string {
    try {
        if (process.platform === 'darwin' || process.platform === 'linux') {
            const prefixes = process.platform === 'darwin'
                ? ['cu.usbmodem', 'cu.usbserial']
                : ['ttyACM', 'ttyUSB'];
            const devDir = '/dev';
            const entries = fs.readdirSync(devDir);
            for (const prefix of prefixes) {
                const match = entries.find(e => e.startsWith(prefix));
                if (match) { return path.join(devDir, match); }
            }
        }
    } catch { /* ignore */ }
    return '';
}

export class ProjectConfigPanel {
    private static current: ProjectConfigPanel | undefined;
    private readonly panel: vscode.WebviewPanel;
    private readonly pyprojectPath: string;
    private disposables: vscode.Disposable[] = [];

    static createOrShow(context: vscode.ExtensionContext): void {
        const pyprojectPath = ProjectConfigPanel.findPyproject();
        if (!pyprojectPath) {
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

    private static findPyproject(): string | undefined {
        const folder = vscode.workspace.workspaceFolders?.[0];
        if (!folder) { return undefined; }
        const p = path.join(folder.uri.fsPath, 'pyproject.toml');
        return fs.existsSync(p) ? p : undefined;
    }

    private buildHtml(cfg: PyMCUConfig): string {
        const arch = detectArchitecture(cfg.chip);
        const archColor: Record<string, string> = {
            AVR: '#4c8eda', PIC: '#e07b39', 'RISC-V': '#6abf69', Unknown: '#888'
        };
        const avrVisible = arch === 'AVR' ? '' : 'display:none';

        return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>PyMCU Project Configuration</title>
<style>
  body { font-family: var(--vscode-font-family); color: var(--vscode-foreground);
         background: var(--vscode-editor-background); padding: 24px; max-width: 560px; }
  h2 { margin-top: 0; }
  .badge { display: inline-block; padding: 2px 10px; border-radius: 12px;
           font-size: 11px; font-weight: 700; letter-spacing: 0.5px;
           color: #fff; background: ${archColor[arch]}; margin-left: 8px; vertical-align: middle; }
  label { display: block; margin-top: 14px; font-size: 12px; opacity: 0.8; }
  input, select { width: 100%; box-sizing: border-box; padding: 5px 8px; margin-top: 4px;
                  background: var(--vscode-input-background); color: var(--vscode-input-foreground);
                  border: 1px solid var(--vscode-input-border, #555); border-radius: 3px; font-size: 13px; }
  .row { display: flex; gap: 8px; align-items: flex-end; }
  .row input { flex: 1; }
  button { margin-top: 6px; padding: 5px 14px; border: none; border-radius: 3px; cursor: pointer;
           font-size: 13px; background: var(--vscode-button-background);
           color: var(--vscode-button-foreground); }
  button:hover { background: var(--vscode-button-hoverBackground); }
  #detect-btn { flex-shrink: 0; margin-top: 10px; }
  .fuses { margin-top: 16px; padding: 12px; border: 1px solid var(--vscode-input-border, #555);
           border-radius: 4px; }
  .fuses h4 { margin: 0 0 8px; font-size: 12px; opacity: 0.7; text-transform: uppercase; }
  .fuse-row { display: flex; gap: 10px; }
  .fuse-row label { flex: 1; }
  hr { margin: 20px 0; border: none; border-top: 1px solid var(--vscode-input-border, #555); }
  .actions { margin-top: 20px; display: flex; gap: 10px; }
  #save-btn { background: var(--vscode-button-background); }
  #cancel-btn { background: var(--vscode-button-secondaryBackground, #444);
                color: var(--vscode-button-secondaryForeground, #ddd); }
</style>
</head>
<body>
<h2>PyMCU Configuration <span class="badge" id="arch-badge">${arch}</span></h2>

<label>Target Chip
  <select id="chip" onchange="onChipChange(this.value)">
    <optgroup label="AVR (ATmega)">
      <option value="atmega328p" ${cfg.chip==='atmega328p'?'selected':''}>ATmega328P (Arduino Uno)</option>
      <option value="atmega2560" ${cfg.chip==='atmega2560'?'selected':''}>ATmega2560 (Arduino Mega)</option>
      <option value="atmega32u4" ${cfg.chip==='atmega32u4'?'selected':''}>ATmega32U4 (Leonardo)</option>
    </optgroup>
    <optgroup label="AVR (ATtiny)">
      <option value="attiny85" ${cfg.chip==='attiny85'?'selected':''}>ATtiny85</option>
      <option value="attiny45" ${cfg.chip==='attiny45'?'selected':''}>ATtiny45</option>
      <option value="attiny13" ${cfg.chip==='attiny13'?'selected':''}>ATtiny13</option>
    </optgroup>
    <optgroup label="PIC">
      <option value="pic16f877a" ${cfg.chip==='pic16f877a'?'selected':''}>PIC16F877A</option>
      <option value="pic18f4550" ${cfg.chip==='pic18f4550'?'selected':''}>PIC18F4550</option>
    </optgroup>
    <option value="__custom__">Custom…</option>
  </select>
</label>
<div id="custom-chip-row" style="display:none;margin-top:4px">
  <input id="custom-chip" placeholder="e.g. atmega1284p" value="${cfg.chip}" />
</div>

<label>Clock Frequency (Hz)
  <input id="frequency" type="number" min="1000" max="240000000" value="${cfg.frequency}" />
</label>

<hr>
<label>Flash Programmer
  <select id="programmer">
    <option value="" ${cfg.programmer===''?'selected':''}>Auto (based on chip)</option>
    <option value="avrdude" ${cfg.programmer==='avrdude'?'selected':''}>avrdude (AVR)</option>
    <option value="pk2cmd" ${cfg.programmer==='pk2cmd'?'selected':''}>pk2cmd (PIC)</option>
  </select>
</label>

<label>Serial Port
  <div class="row">
    <input id="port" placeholder="/dev/cu.usbmodem… or COM3" value="${cfg.port}" />
    <button id="detect-btn" onclick="detectPort()">Detect</button>
  </div>
</label>

<label>Baud Rate
  <input id="baud" type="number" min="300" max="4000000" value="${cfg.baud || 115200}" />
</label>

<div id="fuses-section" class="fuses" style="${avrVisible}">
  <h4>AVR Fuse Bits (hex, optional)</h4>
  <div class="fuse-row">
    <label>Low Fuse  <input id="fuse-low"  placeholder="0xFF" value="${cfg.fuseLow}"  maxlength="4" /></label>
    <label>High Fuse <input id="fuse-high" placeholder="0xDE" value="${cfg.fuseHigh}" maxlength="4" /></label>
    <label>Ext Fuse  <input id="fuse-ext"  placeholder="0xFF" value="${cfg.fuseExt}"  maxlength="4" /></label>
  </div>
</div>

<div class="actions">
  <button id="save-btn" onclick="save()">Save</button>
  <button id="cancel-btn" onclick="cancel()">Cancel</button>
</div>

<script>
  const vscode = acquireVsCodeApi();

  const AVR_CHIPS = ['at'];
  const arcColors = { AVR:'#4c8eda', PIC:'#e07b39', 'RISC-V':'#6abf69', Unknown:'#888' };

  function getChip() {
    const sel = document.getElementById('chip').value;
    return sel === '__custom__'
      ? document.getElementById('custom-chip').value.trim()
      : sel;
  }

  function detectArch(chip) {
    const c = chip.toLowerCase();
    if (c.startsWith('at')) return 'AVR';
    if (c.startsWith('pic') || /^(12|16|18)f/.test(c)) return 'PIC';
    if (c.includes('riscv') || c.includes('risc-v')) return 'RISC-V';
    return 'Unknown';
  }

  function onChipChange(val) {
    const custom = document.getElementById('custom-chip-row');
    custom.style.display = val === '__custom__' ? '' : 'none';
    if (val !== '__custom__') updateArch(val);
  }

  function updateArch(chip) {
    const arch = detectArch(chip);
    const badge = document.getElementById('arch-badge');
    badge.textContent = arch;
    badge.style.background = arcColors[arch] || '#888';
    document.getElementById('fuses-section').style.display = arch === 'AVR' ? '' : 'none';
  }

  function detectPort() {
    vscode.postMessage({ command: 'detect_port' });
  }

  function save() {
    vscode.postMessage({
      command: 'save',
      config: {
        chip:       getChip(),
        frequency:  parseInt(document.getElementById('frequency').value, 10) || 16000000,
        sources:    '',
        entry:      '',
        programmer: document.getElementById('programmer').value,
        port:       document.getElementById('port').value.trim(),
        baud:       parseInt(document.getElementById('baud').value, 10) || 115200,
        fuseLow:    document.getElementById('fuse-low').value.trim(),
        fuseHigh:   document.getElementById('fuse-high').value.trim(),
        fuseExt:    document.getElementById('fuse-ext').value.trim(),
      }
    });
  }

  function cancel() {
    vscode.postMessage({ command: 'cancel' });
  }

  window.addEventListener('message', event => {
    const msg = event.data;
    if (msg.command === 'port_detected') {
      if (msg.port) {
        document.getElementById('port').value = msg.port;
      } else {
        alert('No serial port detected. Connect your device and try again.');
      }
    }
  });

  // Init custom chip if current value not in select
  const sel = document.getElementById('chip');
  const knownValues = [...sel.options].map(o => o.value).filter(v => v !== '__custom__');
  if (!knownValues.includes('${cfg.chip}') && '${cfg.chip}' !== '') {
    sel.value = '__custom__';
    document.getElementById('custom-chip-row').style.display = '';
    document.getElementById('custom-chip').value = '${cfg.chip}';
  }
</script>
</body>
</html>`;
    }
}
