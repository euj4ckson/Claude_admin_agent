import fs from 'node:fs';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const exec = promisify(execFile);
const powershell = path.join(process.env.SystemRoot || 'C:/Windows', 'System32/WindowsPowerShell/v1.0/powershell.exe');
const evidenceRoot = path.resolve(process.env.CENTRAL_TICKET_EVIDENCE || path.join(process.cwd(), 'scratch', 'evidence'));
const ticket = String(process.env.CENTRAL_TICKET_ID || 'ticket').replace(/[^A-Za-z0-9_-]/g, '_');

export const TOOL_DEFINITIONS = [
  { name: 'computer_screenshot', description: 'Captura a tela virtual inteira do Windows e salva uma evidência PNG no scratch/evidence do ticket. Use com label before, action ou result; mantenha a tela do sistema sob teste em primeiro plano e não capture credenciais.', inputSchema: { type: 'object', properties: { scenario: { type: 'string', description: 'Nome curto do cenário, sem caminho.' }, label: { type: 'string', enum: ['before', 'action', 'result', 'custom'] }, includeImage: { type: 'boolean', description: 'Retorna a imagem para inspeção visual; padrão true.' } }, required: ['scenario', 'label'], additionalProperties: false } },
  { name: 'computer_active_window', description: 'Informa o título e os limites da janela atualmente em primeiro plano, sem alterar a tela.', inputSchema: { type: 'object', properties: {}, additionalProperties: false } },
  { name: 'computer_click', description: 'Move o mouse e clica na posição indicada da tela virtual. Use coordenadas observadas no screenshot anterior; não interaja com janelas fora do cenário do ticket.', inputSchema: { type: 'object', properties: { x: { type: 'integer', minimum: 0 }, y: { type: 'integer', minimum: 0 }, button: { type: 'string', enum: ['left', 'right', 'double'] } }, required: ['x', 'y'], additionalProperties: false } },
  { name: 'computer_type', description: 'Digita texto na janela atualmente em primeiro plano. O texto é enviado por clipboard temporário e restaurado depois; não use para credenciais.', inputSchema: { type: 'object', properties: { text: { type: 'string', minLength: 1, maxLength: 10000 } }, required: ['text'], additionalProperties: false } },
  { name: 'computer_key', description: 'Envia uma tecla segura para a janela em primeiro plano.', inputSchema: { type: 'object', properties: { key: { type: 'string', enum: ['ENTER', 'TAB', 'SHIFT_TAB', 'ESC', 'BACKSPACE', 'DELETE', 'UP', 'DOWN', 'LEFT', 'RIGHT', 'HOME', 'END', 'CTRL_A', 'CTRL_C', 'CTRL_V', 'F5', 'ALT_F4'] } }, required: ['key'], additionalProperties: false } },
  { name: 'computer_wait', description: 'Aguarda uma transição visual terminar, por no máximo 10 segundos.', inputSchema: { type: 'object', properties: { milliseconds: { type: 'integer', minimum: 100, maximum: 10000 } }, required: ['milliseconds'], additionalProperties: false } }
];

function jsonArg(value) { return Buffer.from(JSON.stringify(value), 'utf8').toString('base64'); }
function psScript(body) { return Buffer.from(String(body), 'utf16le').toString('base64'); }
async function runPowerShell(body, timeout = 30_000) {
  try { return (await exec(powershell, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', psScript(body)], { windowsHide: true, timeout, maxBuffer: 2 * 1024 * 1024 })).stdout.trim(); }
  catch (error) { throw new Error(String(error.stderr || error.message || 'Falha no Windows PowerShell.').replace(/\r?\n/g, ' ').slice(0, 1000)); }
}

export function safeEvidencePart(value, fallback = 'cenario') {
  const clean = String(value || '').trim().replace(/[^A-Za-z0-9._-]+/g, '_').replace(/^[-_.]+|[-_.]+$/g, '').slice(0, 70);
  return clean || fallback;
}
export function validateCoordinates(x, y, bounds = { left: 0, top: 0, right: 10000, bottom: 10000 }) {
  const nx = Number(x), ny = Number(y);
  if (!Number.isInteger(nx) || !Number.isInteger(ny) || nx < bounds.left || ny < bounds.top || nx > bounds.right || ny > bounds.bottom) throw new Error('Coordenada fora da tela virtual informada pelo Windows.');
  return { x: nx, y: ny };
}
function evidenceFile(scenario, label) { fs.mkdirSync(evidenceRoot, { recursive: true }); return path.join(evidenceRoot, `${new Date().toISOString().replace(/[.:]/g, '-')}-${ticket}-${safeEvidencePart(scenario)}-${safeEvidencePart(label, 'custom')}.png`); }

async function screenshot(scenario, label, includeImage = true) {
  const target = evidenceFile(scenario, label), encoded = jsonArg(target);
  await runPowerShell(`$j=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${encoded}')); Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing; $b=[System.Windows.Forms.SystemInformation]::VirtualScreen; $bmp=[Drawing.Bitmap]::new($b.Width,$b.Height); $g=[Drawing.Graphics]::FromImage($bmp); try { $g.CopyFromScreen($b.Left,$b.Top,0,0,$bmp.Size); $bmp.Save($j,[Drawing.Imaging.ImageFormat]::Png) } finally { $g.Dispose(); $bmp.Dispose() }; Write-Output $j`);
  const image = includeImage ? fs.readFileSync(target).toString('base64') : null;
  return { path: target, image };
}

async function activeWindow() {
  const output = await runPowerShell(`Add-Type @'\nusing System; using System.Text; using System.Runtime.InteropServices; public static class CentralWindow { [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n); [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r); public struct RECT { public int Left,Top,Right,Bottom; } }\n'@; $h=[CentralWindow]::GetForegroundWindow(); $s=New-Object Text.StringBuilder 512; [void][CentralWindow]::GetWindowText($h,$s,$s.Capacity); $r=New-Object CentralWindow+RECT; [void][CentralWindow]::GetWindowRect($h,[ref]$r); [pscustomobject]@{title=$s.ToString();left=$r.Left;top=$r.Top;right=$r.Right;bottom=$r.Bottom}|ConvertTo-Json -Compress`);
  try { return JSON.parse(output); } catch { throw new Error('Não foi possível identificar a janela em primeiro plano.'); }
}

async function click(input) {
  const window = await activeWindow(), point = validateCoordinates(input.x, input.y, { left: window.left, top: window.top, right: window.right, bottom: window.bottom });
  const button = input.button === 'right' ? '0x0008,0x0010' : '0x0002,0x0004', repeated = input.button === 'double' ? '; Start-Sleep -Milliseconds 80; [NativeMouse]::mouse_event(0x0002,0,0,0,0); [NativeMouse]::mouse_event(0x0004,0,0,0,0)' : '';
  await runPowerShell(`Add-Type @'\nusing System; using System.Runtime.InteropServices; public static class NativeMouse { [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint data,UIntPtr extra); }\n'@; [NativeMouse]::SetCursorPos(${point.x},${point.y}); [NativeMouse]::mouse_event(${button.split(',')[0]},0,0,0,[UIntPtr]::Zero); [NativeMouse]::mouse_event(${button.split(',')[1]},0,0,0,[UIntPtr]::Zero)${repeated}`);
  return { clicked: point, window };
}

async function typeText(text) {
  const encoded = jsonArg(text);
  await runPowerShell(`Add-Type -AssemblyName System.Windows.Forms; $text=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${encoded}')); $old=$null; $had=[Windows.Forms.Clipboard]::ContainsText(); if($had){$old=[Windows.Forms.Clipboard]::GetText()}; try { [Windows.Forms.Clipboard]::SetText($text); [Windows.Forms.SendKeys]::SendWait('^v'); Start-Sleep -Milliseconds 120 } finally { if($had){[Windows.Forms.Clipboard]::SetText($old)} else {[Windows.Forms.Clipboard]::Clear()} }`);
  return { typed: text.length };
}

const keyMap = { ENTER: '~', TAB: '{TAB}', SHIFT_TAB: '+{TAB}', ESC: '{ESC}', BACKSPACE: '{BACKSPACE}', DELETE: '{DELETE}', UP: '{UP}', DOWN: '{DOWN}', LEFT: '{LEFT}', RIGHT: '{RIGHT}', HOME: '{HOME}', END: '{END}', CTRL_A: '^a', CTRL_C: '^c', CTRL_V: '^v', F5: '{F5}', ALT_F4: '%{F4}' };
async function keyPress(key) { const value = keyMap[String(key || '').toUpperCase()]; if (!value) throw new Error('Tecla não permitida pela ferramenta de Computer Use.'); await runPowerShell(`Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.SendKeys]::SendWait('${value.replaceAll("'", "''")}')`); return { key: String(key).toUpperCase() }; }

async function callTool(name, input = {}) {
  if (name === 'computer_screenshot') { const result = await screenshot(input.scenario, input.label, input.includeImage !== false); return { content: [{ type: 'text', text: `Evidência salva em ${result.path}` }, ...(result.image ? [{ type: 'image', data: result.image, mimeType: 'image/png' }] : [])] }; }
  if (name === 'computer_active_window') return { content: [{ type: 'text', text: JSON.stringify(await activeWindow()) }] };
  if (name === 'computer_click') return { content: [{ type: 'text', text: JSON.stringify(await click(input)) }] };
  if (name === 'computer_type') return { content: [{ type: 'text', text: JSON.stringify(await typeText(String(input.text))) }] };
  if (name === 'computer_key') return { content: [{ type: 'text', text: JSON.stringify(await keyPress(input.key)) }] };
  if (name === 'computer_wait') { await new Promise(resolve => setTimeout(resolve, Math.min(Math.max(Number(input.milliseconds) || 100, 100), 10000))); return { content: [{ type: 'text', text: 'Aguardado.' }] }; }
  throw new Error(`Ferramenta desconhecida: ${name}`);
}

function reply(id, result, error = null) { process.stdout.write(JSON.stringify(error ? { jsonrpc: '2.0', id, error } : { jsonrpc: '2.0', id, result }) + '\n'); }
async function handle(message) {
  if (message.method === 'notifications/initialized' || message.method === 'notifications/cancelled') return;
  if (message.method === 'initialize') return reply(message.id, { protocolVersion: '2025-06-18', capabilities: { tools: {} }, serverInfo: { name: 'central-computer', version: '1.0.0' } });
  if (message.method === 'ping') return reply(message.id, {});
  if (message.method === 'tools/list') return reply(message.id, { tools: TOOL_DEFINITIONS });
  if (message.method === 'tools/call') { try { return reply(message.id, await callTool(message.params?.name, message.params?.arguments || {})); } catch (error) { return reply(message.id, { content: [{ type: 'text', text: error.message }], isError: true }); } }
  if (message.id !== undefined) return reply(message.id, null, { code: -32601, message: `Método não suportado: ${message.method}` });
}

let buffer = '';
process.stdin.setEncoding('utf8'); process.stdin.on('data', chunk => { buffer += chunk; let index; while ((index = buffer.indexOf('\n')) >= 0) { const line = buffer.slice(0, index).trim(); buffer = buffer.slice(index + 1); if (!line) continue; try { const message = JSON.parse(line); void handle(message).catch(error => reply(message.id, null, { code: -32603, message: error.message })); } catch (error) { process.stderr.write(`central-computer: JSON inválido (${error.message})\n`); } } });
process.stderr.write(`central-computer MCP ativo para ${ticket}; evidências em ${evidenceRoot}\n`);
