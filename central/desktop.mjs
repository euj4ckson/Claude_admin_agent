import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
const exec = promisify(execFile);
const powershell = path.join(process.env.SystemRoot || 'C:/Windows', 'System32/WindowsPowerShell/v1.0/powershell.exe');
// Encode data as UTF-16LE, never interpolate user PO text in executable shell code.
export async function ps(code) {
  return exec(powershell, ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(code, 'utf16le').toString('base64')], { windowsHide: true, timeout: 15_000 });
}
const literal = value => `'${String(value).replaceAll("'", "''")}'`;
export async function launchTerminal(config, record, runtime) {
  const args = ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(config.appRoot, 'central/abrir-sessao.ps1'), '-AppRoot', config.appRoot, '-DataRoot', config.dataRoot, '-Ticket', record.id, '-RunId', runtime.runId];
  // All arguments are local controlled paths or validated identifiers, not the ticket scope.
  if (args.some(x => /["\r\n]/.test(x))) throw new Error('Um caminho da instalação contém caracteres não suportados.');
  const commandLine = args.map(x => /\s/.test(x) ? `"${x}"` : x).join(' ');
  await ps(`Start-Process -FilePath ${literal(powershell)} -ArgumentList ${literal(commandLine)} -WorkingDirectory ${literal(record.worktree)} -WindowStyle Normal`);
}
export async function focusTerminal(id) {
  const { stdout } = await ps(`$taskShell = New-Object -ComObject WScript.Shell; $taskShell.AppActivate(${literal(`Claude - ${id} - Central de Tickets`)})`);
  return stdout.trim() === 'True';
}
export async function openLocal(target) { await ps(`Start-Process -FilePath ${literal(target)}`); }
