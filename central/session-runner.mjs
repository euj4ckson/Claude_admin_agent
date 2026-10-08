import path from 'node:path';
import fs from 'node:fs';
import { spawn } from 'node:child_process';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { Store, readJson, atomicJson, now, initialPrompt, verifyWorktree } from './core.mjs';
import { recordHookMetric, appendMemorySync } from './metrics.mjs';
import { learningFromTicket, syncLearning, flushMemoryOutbox } from './memory.mjs';

const [dataRoot, id, runId] = process.argv.slice(2);
let store, runtime, child, heartbeat;
const execFileAsync = promisify(execFile);
async function backgroundSession(config, ticket) {
  try {
    const result = await execFileAsync(config.claude, ['agents', '--json'], { windowsHide: true, timeout: 10_000, maxBuffer: 512_000 });
    const agents = JSON.parse(result.stdout || '[]');
    return agents.find(agent => agent.kind === 'background' && (agent.sessionId === ticket.sessionId || agent.id === ticket.sessionId || ticket.sessionIds?.includes(agent.sessionId)));
  } catch { return null; }
}
try {
  const config = readJson(path.join(dataRoot, 'config.json'));
  store = new Store(config);
  const ticket = store.get(id);
  runtime = store.runtime(id);
  if (runtime?.runId !== runId || readJson(store.file(id, 'active.lock'))?.runId !== runId) throw new Error('Reserva de sessão inválida.');
  await verifyWorktree(store, ticket);
  runtime = { ...runtime, pid: process.pid, phase: 'running', heartbeat: now() };
  atomicJson(store.file(id, 'active.lock'), { runId, pid: process.pid });
  atomicJson(store.file(id, 'runtime.json'), runtime);
  const executable = ticket.agent === 'codex' ? config.codex : config.claude;
  if (!executable) throw new Error(`${ticket.agent === 'codex' ? 'Codex CLI' : 'Claude Code'} não está configurado.`);
  const env = { ...process.env, CENTRAL_TICKETS_DATA: dataRoot, CENTRAL_TICKET_ID: ticket.id, CENTRAL_TICKET_RUN: runId, CENTRAL_TICKET_AGENT: ticket.agent || 'claude' };
  // Edições de arquivos são aceitas automaticamente para reduzir interrupções.
  // O hook da Central continua sendo a última barreira: caminhos fora do ticket,
  // comandos sensíveis, SQL e operações fora da política permanecem bloqueados
  // ou sujeitos à confirmação manual.
  const args = ticket.agent === 'codex' ? ['--ask-for-approval', 'on-request', '--sandbox', 'workspace-write', initialPrompt(store, ticket)] : ['--name', ticket.id, '--permission-mode', 'bypassPermissions', '--plugin-dir', path.join(config.appRoot, 'central'), '--settings', store.file(id, 'session-settings.json'), '--mcp-config', store.file(id, 'mcp-servers.json'), '--append-system-prompt', fs.readFileSync(store.file(id, 'session-context.txt'), 'utf8'), '--add-dir', store.stateDir(id), '--add-dir', path.join(config.documents_root, id.toLowerCase())];
  const references = path.join(config.references_root, id.toLowerCase());
  if (fs.existsSync(references)) args.push('--add-dir', references);
  if (ticket.agent !== 'codex' && ticket.sessionStarted) {
    const background = await backgroundSession(config, ticket);
    if (background) {
      // A background Claude session must be opened with attach; --resume
      // intentionally refuses to create a second interactive owner.
      args.length = 0;
      args.push('attach', background.id || background.sessionId.slice(0, 8));
    } else args.push('--resume', ticket.sessionId);
  } else if (ticket.agent !== 'codex') args.push('--session-id', ticket.sessionId, initialPrompt(store, ticket));
  console.log(`\n${ticket.id} — Central de Tickets\n${ticket.worktree}\nEntrega: ${ticket.delivery === 'no_commit' ? 'sem commit' : 'commit local'}, sem push.\n`);
  child = spawn(executable, args, { cwd: ticket.worktree, env, stdio: 'inherit', shell: process.platform === 'win32' && /\.(cmd|bat)$/i.test(executable) });
  if (ticket.agent === 'codex') recordHookMetric(store, ticket, { hook_event_name: 'SessionStart', session_id: ticket.sessionId });
  runtime.childPid = child.pid;
  atomicJson(store.file(id, 'runtime.json'), runtime);
  heartbeat = setInterval(() => { try { atomicJson(store.file(id, 'runtime.json'), { ...runtime, heartbeat: now() }); } catch {} }, 3000);
  const code = await new Promise((resolve, reject) => { child.on('error', reject); child.on('exit', value => resolve(value)); });
  clearInterval(heartbeat);
  atomicJson(store.file(id, 'runtime.json'), { ...runtime, phase: 'stopped', exitCode: code, endedAt: now(), heartbeat: now() });
  store.event(id, 'sessao', `Processo ${ticket.agent === 'codex' ? 'Codex' : 'Claude'} encerrado (${code ?? 'interrompido'}). A Central não marcou o ticket como concluído.`);
  if (ticket.agent === 'codex') {
    recordHookMetric(store, ticket, { hook_event_name: 'SessionEnd', session_id: ticket.sessionId });
    try { const view = store.view(id); await flushMemoryOutbox(config); const result = await syncLearning(config, learningFromTicket(ticket, view.state, view.quality, view.metrics)); appendMemorySync(store, id, result); } catch (error) { store.event(id, 'memory', `Falha ao sincronizar aprendizado do Codex; preservado localmente: ${error.message}`); }
  }
} catch (e) {
  clearInterval(heartbeat); console.error(`\nNão foi possível abrir a conversa: ${e.message}`);
  if (store && runtime) { atomicJson(store.file(id, 'runtime.json'), { ...runtime, phase: 'error', error: e.message, endedAt: now() }); store.event(id, 'erro', e.message); }
  process.exitCode = 1;
} finally { if (store && runtime) store.release(id, runId); }
