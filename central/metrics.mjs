import fs from 'node:fs';
import path from 'node:path';

const now = () => new Date().toISOString();
const atomicJson = (file, value) => {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = `${file}.${Date.now()}.${Math.random().toString(16).slice(2)}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(value, null, 2) + '\n', { mode: 0o600 });
  fs.renameSync(tmp, file);
};
const readJson = (file, fallback = null) => { try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch (e) { return e.code === 'ENOENT' ? fallback : fallback; } };
const phaseFor = stage => ({ analise: 'analise', aguardando_aprovacao: 'aprovacao', implementacao: 'implementacao', compilacao: 'compilacao', testes: 'testes', revisao: 'revisao', aguardando_validacao_manual: 'validacao_manual', entrega: 'entrega', concluido: 'entrega', entregue: 'entrega', bloqueado: 'bloqueado' }[stage] || 'outro');
const estimate = value => Math.ceil(Buffer.byteLength(typeof value === 'string' ? value : JSON.stringify(value ?? ''), 'utf8') / 4);

export function metricsFile(store, id) { return store.file(id, 'metrics.json'); }
export function emptyMetrics(ticket) {
  return { schema_version: 1, ticket, started_at: null, ended_at: null, last_stage: null, last_event_at: null, phases: {}, totals: { estimated_input_tokens: 0, estimated_output_tokens: 0, reported_input_tokens: 0, reported_output_tokens: 0, tool_calls: 0, prompts: 0, failures: 0, turns: 0 }, scope_expansions: [], reviews: [], blockers: [], escaped_findings: [], memory_sync: [] };
}
function ensurePhase(metrics, phase, at) {
  metrics.phases[phase] ??= { started_at: at, ended_at: null, seconds: 0, estimated_input_tokens: 0, estimated_output_tokens: 0, reported_input_tokens: 0, reported_output_tokens: 0, prompts: 0, tool_calls: 0, failures: 0, turns: 0 };
  return metrics.phases[phase];
}
function closePhase(metrics, phase, at) {
  const entry = metrics.phases[phase]; if (!entry?.started_at) return;
  entry.ended_at = at; entry.seconds = Math.max(0, Math.round((Date.parse(at) - Date.parse(entry.started_at)) / 1000));
}
export function recordHookMetric(store, ticket, input) {
  const at = now(), state = (() => { try { return store.state(ticket.id); } catch { return null; } })(), phase = phaseFor(state?.stage);
  const file = metricsFile(store, ticket.id), metrics = { ...emptyMetrics(ticket.id), ...(readJson(file) || {}) };
  metrics.phases ??= {}; metrics.totals ??= emptyMetrics(ticket.id).totals;
  const previous = metrics.last_stage ? phaseFor(metrics.last_stage) : null;
  if (!metrics.started_at) metrics.started_at = at;
  if (previous && previous !== phase) closePhase(metrics, previous, at);
  const current = ensurePhase(metrics, phase, at);
  const event = String(input.hook_event_name || 'unknown');
  const reported = input.usage || input.token_usage || {};
  const inputTokens = Number(reported.input_tokens ?? reported.prompt_tokens ?? 0) || 0;
  const outputTokens = Number(reported.output_tokens ?? reported.completion_tokens ?? 0) || 0;
  const estimatedIn = event === 'UserPromptSubmit' ? estimate(input.prompt) : event === 'PreToolUse' ? estimate(input.tool_input) : 0;
  const estimatedOut = event.startsWith('PostToolUse') ? estimate(input.tool_response ?? input.tool_result ?? input.tool_output) : 0;
  metrics.totals.estimated_input_tokens += estimatedIn; metrics.totals.estimated_output_tokens += estimatedOut;
  metrics.totals.reported_input_tokens += inputTokens; metrics.totals.reported_output_tokens += outputTokens;
  current.estimated_input_tokens += estimatedIn; current.estimated_output_tokens += estimatedOut;
  current.reported_input_tokens += inputTokens; current.reported_output_tokens += outputTokens;
  if (event === 'UserPromptSubmit') { metrics.totals.prompts++; current.prompts++; }
  if (event === 'PreToolUse') { metrics.totals.tool_calls++; current.tool_calls++; }
  if (event === 'PostToolUseFailure') { metrics.totals.failures++; current.failures++; }
  if (event === 'Stop') { metrics.totals.turns++; current.turns++; }
  if (phase === 'bloqueado' && (!metrics.blockers.length || metrics.blockers.at(-1)?.stage !== state?.stage)) metrics.blockers.push({ at, stage: state?.stage || 'bloqueado', reason: state?.next_action || state?.blockers?.[0] || 'bloqueio informado no estado' });
  if (state?.scope_revision > 1 || state?.change_budget?.approved_expansion || state?.change_budget?.correction_rounds > 0) {
    const key = `${state.scope_revision || 1}:${state.change_budget?.correction_rounds || 0}:${!!state.change_budget?.approved_expansion}`;
    if (!metrics.scope_expansions.some(x => x.key === key)) metrics.scope_expansions.push({ key, at, revision: state.scope_revision || 1, correction_rounds: state.change_budget?.correction_rounds || 0, approved: !!state.change_budget?.approved_expansion });
  }
  metrics.last_stage = state?.stage || metrics.last_stage; metrics.last_event_at = at;
  if (event === 'SessionEnd') { metrics.ended_at = at; closePhase(metrics, phase, at); }
  atomicJson(file, metrics); return metrics;
}

export function metricsSnapshot(store, record, state, quality) {
  const raw = readJson(metricsFile(store, record.id), emptyMetrics(record.id));
  const reviews = new Set((raw.reviews || []).map(x => JSON.stringify(x)));
  try { for (const event of store.events(record.id)) if (/(?:review|revis|correção|correcao)/i.test(`${event.type} ${event.message}`)) reviews.add(JSON.stringify({ at: event.at, message: event.message })); } catch {}
  if (state?.review) reviews.add(JSON.stringify({ at: state.review.at || state.updated_at || null, verdict: state.review.verdict || state.review.resultado || 'registrada' }));
  const tests = Array.isArray(state?.tests) ? state.tests : [];
  const notExecuted = tests.filter(x => /(?:não executado|nao executado|bloqueado|pendente)/i.test(String(x?.result ?? x?.status ?? ''))).length;
  const findings = ['suspected', 'findings', 'escaped_findings', 'out_of_scope'].flatMap(key => Array.isArray(state?.review?.[key]) ? state.review[key] : []).length;
  const blockedSeconds = Object.entries(raw.phases || {}).filter(([key]) => key === 'bloqueado').reduce((sum, [, value]) => sum + Number(value.seconds || 0), 0);
  return { tokens: { estimated_input: raw.totals?.estimated_input_tokens || 0, estimated_output: raw.totals?.estimated_output_tokens || 0, reported_input: raw.totals?.reported_input_tokens || 0, reported_output: raw.totals?.reported_output_tokens || 0, estimated_total: (raw.totals?.estimated_input_tokens || 0) + (raw.totals?.estimated_output_tokens || 0), by_phase: raw.phases || {} }, scope_expansions: Math.max(raw.scope_expansions?.length || 0, Math.max(0, Number(state?.scope_revision || 1) - 1)), reviews: reviews.size, blocked_seconds: blockedSeconds, tests_not_executed: notExecuted, test_count: tests.length, escaped_findings: findings, memory_sync: raw.memory_sync || [], quality: { commit_ready: !!quality?.commitReady, delivery_ready: !!quality?.deliveryReady } };
}

export function appendMemorySync(store, ticketId, result) {
  const file = metricsFile(store, ticketId), metrics = readJson(file, emptyMetrics(ticketId));
  metrics.memory_sync ??= []; metrics.memory_sync.push({ at: now(), status: result.remote, detail: result.detail });
  atomicJson(file, metrics); return metrics;
}
