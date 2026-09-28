import fs from 'node:fs';
import path from 'node:path';
import { Store, readJson, atomicJson, now } from './core.mjs';
import { approvalPrompt, preTool, criticalStage, releaseExecution } from './policy.mjs';

let input = {};
try {
  let body = '';
  for await (const chunk of process.stdin) { body += chunk; if (body.length > 2_000_000) throw new Error('Evento muito grande.'); }
  input = JSON.parse(body || '{}');
  const dataRoot = process.env.CENTRAL_TICKETS_DATA;
  const id = process.env.CENTRAL_TICKET_ID;
  if (!dataRoot || !id) process.exit(0); // Plugin has no effect outside a managed session.
  const config = readJson(path.join(dataRoot, 'config.json'));
  const store = new Store(config); let ticket = store.get(id); const runtime = store.runtime(id);
  if (!runtime || runtime.runId !== process.env.CENTRAL_TICKET_RUN) throw new Error('A sessão não corresponde à reserva deste ticket.');
  // A reconnect/resume can rotate Claude's session id. Accept that rotation
  // only while this managed runner owns the active ticket lock, preserving
  // previous ids for audit and approval binding.
  const active = readJson(store.file(id, 'active.lock'));
  const managed = active?.runId === runtime.runId && ['starting', 'running'].includes(runtime.phase);
  if (input.session_id && input.session_id !== ticket.sessionId) {
    const known = Array.isArray(ticket.sessionIds) && ticket.sessionIds.includes(input.session_id);
    if (!known && !managed) throw new Error('Esta sessao nao corresponde a reserva ativa deste ticket.');
    if (!known) {
      ticket = { ...ticket, sessionId: input.session_id, sessionIds: [...new Set([...(ticket.sessionIds || [ticket.sessionId]), input.session_id])] };
      store.save(ticket);
      store.event(id, 'sessao', `Identificador de conversa atualizado durante a sessao gerenciada; historico preservado (${input.session_id}).`);
    }
  }
  const event = input.hook_event_name;
  let output = {};
  if (event === 'SessionStart') {
    if (input.session_id !== ticket.sessionId) throw new Error('Esta janela pertence a outro ticket. Abra uma nova sessão pela Central.');
    store.save({ ...ticket, sessionStarted: true });
    store.event(id, 'sessao', 'Claude conectado à sessão deste ticket.');
  } else if (event === 'UserPromptSubmit') {
    output = approvalPrompt(store, ticket, input) ?? {};
  } else if (event === 'PreToolUse') {
    output = preTool(store, ticket, runtime, input);
    const reason = output.hookSpecificOutput?.permissionDecisionReason;
    if (output.hookSpecificOutput?.permissionDecision === 'deny') store.event(id, 'controle', reason);
  } else if (['PostToolUse', 'PostToolUseFailure'].includes(event)) {
    if (!criticalStage(store.state(id)?.stage)) releaseExecution(store, runtime);
  } else if (event === 'Stop') {
    store.event(id, 'turno', 'Claude concluiu uma resposta. Confira a etapa e a conversa; isso não significa ticket concluído.');
    if (!criticalStage(store.state(id)?.stage)) releaseExecution(store, runtime);
  } else if (event === 'SessionEnd') {
    releaseExecution(store, runtime);
    store.event(id, 'sessao', 'Conversa encerrada. Trabalho e histórico foram preservados.');
  }
  atomicJson(store.file(id, 'hook-status.json'), { event, at: now(), sessionId: input.session_id });
  process.stdout.write(JSON.stringify(output));
} catch (e) {
  if (input.hook_event_name === 'PreToolUse') {
    process.stdout.write(JSON.stringify({ hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'deny', permissionDecisionReason: `Central: ${e.message}` } }));
  } else { process.stderr.write(`Central: ${e.message}\n`); process.exitCode = 2; }
}
