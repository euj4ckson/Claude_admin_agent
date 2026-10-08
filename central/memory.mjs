import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const now = () => new Date().toISOString();
const redact = text => String(text || '').replace(/(?:password|pwd|token|api[_-]?key|connectionstring)\s*[=:]\s*[^\s;]+/gi, '$1=[REDACTED]').slice(0, 4000);

export function memoryOutbox(config) { return path.join(config.dataRoot, 'ai-memory-outbox.jsonl'); }
export function learningFromTicket(record, state, quality, metrics) {
  const review = state?.review || {};
  return { id: crypto.randomUUID(), at: now(), source: 'central-tickets', ticket: record.id, repository: record.repositoryId || path.basename(record.repository || ''), title: redact(record.title), verdict: redact(review.verdict || review.resultado || ''), confirmed: (Array.isArray(review.confirmed) ? review.confirmed : []).map(redact).slice(0, 12), escaped_findings: (Array.isArray(review.suspected) ? review.suspected : Array.isArray(review.findings) ? review.findings : []).map(redact).slice(0, 12), scope_expansions: metrics?.scope_expansions || 0, tests_not_executed: metrics?.tests_not_executed || 0, quality: { commit_ready: !!quality?.commitReady, delivery_ready: !!quality?.deliveryReady } };
}
export async function syncLearning(config, learning, { fetchImpl = globalThis.fetch } = {}) {
  const outbox = memoryOutbox(config); fs.mkdirSync(path.dirname(outbox), { recursive: true });
  const base = String(config.ai_memory_url || 'http://127.0.0.1:49374').replace(/\/$/, '');
  const endpoint = `${base}/admin/memory`; let remote = 'queued', detail = 'registrado no outbox local';
  try {
    const response = await fetchImpl(endpoint, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(learning), signal: AbortSignal.timeout(1500) });
    if (response.ok) { remote = 'synced'; detail = `AI Memory respondeu ${response.status}`; }
    else { detail = `AI Memory respondeu ${response.status}; mantido no outbox`; fs.appendFileSync(outbox, JSON.stringify(learning) + '\n', { mode: 0o600 }); }
  } catch (e) { detail = `AI Memory indisponível; mantido no outbox (${e.message})`; fs.appendFileSync(outbox, JSON.stringify(learning) + '\n', { mode: 0o600 }); }
  return { remote, detail, outbox };
}

export async function flushMemoryOutbox(config, { fetchImpl = globalThis.fetch } = {}) {
  const outbox = memoryOutbox(config); if (!fs.existsSync(outbox)) return { sent: 0, pending: 0, outbox };
  const lines = fs.readFileSync(outbox, 'utf8').split(/\r?\n/).filter(Boolean), pending = [];
  const base = String(config.ai_memory_url || 'http://127.0.0.1:49374').replace(/\/$/, ''), endpoint = `${base}/admin/memory`;
  for (const line of lines) {
    try {
      const payload = JSON.parse(line), response = await fetchImpl(endpoint, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(payload), signal: AbortSignal.timeout(1500) });
      if (!response.ok) pending.push(line);
    } catch { pending.push(line); }
  }
  if (pending.length) fs.writeFileSync(outbox, pending.join('\n') + '\n', { mode: 0o600 }); else try { fs.unlinkSync(outbox); } catch {}
  return { sent: lines.length - pending.length, pending: pending.length, outbox };
}
