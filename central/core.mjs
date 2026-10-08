import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { execFileSync } from 'node:child_process';
import { metricsSnapshot } from './metrics.mjs';

const exec = promisify(execFile);
export const now = () => new Date().toISOString();
export const hash = text => crypto.createHash('sha256').update(text).digest('hex');
export const STAGES = {
  cadastrado: 'Cadastrado', preparando: 'Preparando trabalho', analise: 'Em análise',
  aguardando_aprovacao: 'Aguardando aprovação', implementacao: 'Implementando',
  compilacao: 'Compilando', testes: 'Testando', revisao: 'Em revisão',
  aguardando_decisao_revisao: 'Decisão de revisão', aguardando_validacao_manual: 'Validação manual',
  bloqueado: 'Precisa de atenção', entrega: 'Preparando entrega', concluido: 'Entregue localmente'
};

const BAD_RESULTS = /^(?:falha|failed|erro|bloqueado|pendente|nao executado|não executado|inconclusivo|unknown)$/i;
const UI_TEST = /(?:tela|ui|manual|visual|interface|click|clique|screen|screenshot|print)/i;
const asList = value => Array.isArray(value) ? value : value == null || value === '' ? [] : [value];
const normalizedResult = item => String(item?.result ?? item?.status ?? '').trim();
const pathLike = value => typeof value === 'string' && /(?:[\\/]|\.(?:png|jpg|jpeg|gif|log|txt|json|sql|xml|html|docx|pdf))$/i.test(value);

function evidenceReferences(item) {
  const refs = [];
  for (const key of ['evidence', 'evidencias', 'evidence_files', 'evidence_images', 'screenshots', 'screenshot', 'log', 'artifact', 'output_file', 'raw_output']) {
    for (const value of asList(item?.[key])) if (typeof value === 'string' && value.trim()) refs.push(value.trim());
  }
  return [...new Set(refs)];
}

function resolveEvidencePath(store, record, value) {
  if (!pathLike(value)) return null;
  const candidate = path.isAbsolute(value) ? value : path.join(store.stateDir(record.id), value);
  const roots = [store.stateDir(record.id), path.join(store.config.documents_root, record.id.toLowerCase())];
  return roots.some(root => inside(root, candidate)) ? candidate : null;
}

/**
 * Quality gate shared by the UI and the hook. It deliberately distinguishes
 * structural readiness (safe to commit) from delivery readiness (safe to mark
 * delivered). State entries are evidence pointers, not a self-certification.
 */
function actualDiff(store, record, state) {
  if (!record.prepared || !record.worktree || !state?.base_sha) return { files: [], added: 0, deleted: 0, outside: [], inspected: false };
  try {
    const git = (args) => execFileSync(store.config.git_executable, ['-C', record.worktree, ...args], { encoding: 'utf8', windowsHide: true, timeout: 20_000, maxBuffer: 4 * 1024 * 1024 }).trim();
    const tracked = git(['diff', '--name-only', state.base_sha, '--']).split(/\r?\n/).filter(Boolean);
    const untracked = git(['ls-files', '--others', '--exclude-standard']).split(/\r?\n/).filter(Boolean);
    const files = [...new Set([...tracked, ...untracked].map(x => x.replaceAll('\\', '/')))];
    const numstat = git(['diff', '--numstat', state.base_sha, '--']).split(/\r?\n/).filter(Boolean).reduce((sum, line) => { const [a, d] = line.split('\t'); return { added: sum.added + (Number(a) || 0), deleted: sum.deleted + (Number(d) || 0) }; }, { added: 0, deleted: 0 });
    for (const file of untracked.slice(0, 200)) { try { const stat = fs.statSync(path.join(record.worktree, file)); if (stat.size <= 2_000_000) { const text = fs.readFileSync(path.join(record.worktree, file), 'utf8'); numstat.added += text.split(/\r?\n/).length; } } catch {} }
    const planned = Array.isArray(state.change_budget?.planned_files) ? state.change_budget.planned_files.map(x => String(x).replaceAll('\\', '/').replace(/^\.\//, '')) : [];
    const outside = planned.length ? files.filter(file => !planned.includes(file)) : [];
    return { files, added: numstat.added, deleted: numstat.deleted, outside, inspected: true };
  } catch (e) { return { files: [], added: 0, deleted: 0, outside: [], inspected: false, error: e.message }; }
}

export function qualityReport(store, record, state = null, options = {}) {
  const current = state ?? (() => { try { return store.state(record.id); } catch { return null; } })();
  const structural = [], delivery = [], evidence = [], entries = [];
  if (!current || typeof current !== 'object') structural.push('estado.json ausente ou inválido');
  const build = Array.isArray(current?.build) ? current.build : [];
  const tests = Array.isArray(current?.tests) ? current.tests : [];
  const review = current?.review;
  let manifest = null;
  try { manifest = readJson(path.join(store.stateDir(record.id), 'evidence-manifest.json')); } catch { structural.push('manifesto de evidencias invalido'); }
  const manifestRefs = new Set(asList(manifest?.entries).flatMap(entry => typeof entry === 'string' ? [entry] : [entry?.path, entry?.file, entry?.evidence].filter(Boolean)).map(String));
  if (Number(current?.schema_version || 1) >= 2 && (!manifest || !Array.isArray(manifest.entries))) structural.push('manifesto de evidências ausente ou inválido');
  if (!build.length) structural.push('compilação não registrada');
  if (!tests.length) structural.push('testes não registrados');
  if (!review || typeof review !== 'object') structural.push('revisão final não registrada');
  const all = [...build.map((x, i) => ({ ...x, _kind: 'build', _index: i })), ...tests.map((x, i) => ({ ...x, _kind: 'test', _index: i }))];
  for (const item of all) {
    const label = `${item._kind}[${item._index}]`;
    const result = normalizedResult(item);
    const refs = evidenceReferences(item);
    if (!result) structural.push(`${label} sem resultado`);
    if (!item.expected && !item.criterio && !item.criteria) structural.push(`${label} sem esperado/critério`);
    if (!item.obtained && !item.detail && !item.details && !item.resultado && !item.output) structural.push(`${label} sem obtido/detalhe`);
    if (!refs.length) evidence.push(`${label} sem referência de evidência`);
    if (Number(current?.schema_version || 1) >= 2) for (const ref of refs) if (!manifestRefs.has(ref)) evidence.push(`${label}: evidência não declarada no manifesto (${ref})`);
    const existing = [];
    for (const ref of refs) {
      const resolved = resolveEvidencePath(store, record, ref);
      if (resolved) {
        if (fs.existsSync(resolved)) existing.push(ref);
        else evidence.push(`${label}: evidência ausente (${ref})`);
      } else if (pathLike(ref)) evidence.push(`${label}: caminho de evidência fora das pastas permitidas (${ref})`);
    }
    if (UI_TEST.test(`${item.type || ''} ${item.name || ''} ${item.command || ''}`) && !existing.some(x => /\.(?:png|jpe?g|gif)$/i.test(x))) {
      delivery.push(`${label}: teste de tela sem screenshot existente`);
    }
    entries.push({ kind: item._kind, index: item._index, name: item.name || item.type || item.project || `${item._kind} ${item._index + 1}`, result, references: refs, existing });
    if (BAD_RESULTS.test(result)) delivery.push(`${label}: resultado ${result}`);
  }
  const verdict = String(review?.verdict ?? review?.resultado ?? '').trim();
  if (!verdict) structural.push('revisão sem veredicto');
  if (BAD_RESULTS.test(verdict) || /(?:bloque|pend|falh|fora do escopo não resolvido)/i.test(verdict)) delivery.push(`revisão com veredicto não aprovável: ${verdict}`);
  const budget = current?.change_budget, guard = current?.scope_guard;
  if (budget && guard) {
    if (Array.isArray(budget.planned_files) && budget.planned_files.length > Number(guard.max_planned_files || Infinity)) structural.push('quantidade de arquivos acima do scope_guard');
    if (Number.isFinite(Number(budget.estimated_added_lines)) && Number(budget.estimated_added_lines) > Number(guard.max_planned_added_lines || Infinity)) structural.push('linhas adicionadas acima do scope_guard');
    if (Number.isFinite(Number(budget.estimated_deleted_lines)) && Number(budget.estimated_deleted_lines) > Number(guard.max_planned_deleted_lines || Infinity)) structural.push('linhas removidas acima do scope_guard');
  }
  if (options.checkDiff) {
    const diff = actualDiff(store, record, current);
    if (!diff.inspected) structural.push(`diff real não pôde ser conferido${diff.error ? `: ${diff.error}` : ''}`);
    else {
      if (diff.outside.length) structural.push(`arquivos fora do plano: ${diff.outside.slice(0, 5).join(', ')}`);
      if (guard && diff.files.length > Number(guard.max_planned_files || Infinity)) structural.push(`diff real com ${diff.files.length} arquivos; limite ${guard.max_planned_files}`);
      if (guard && diff.added > Number(guard.max_planned_added_lines || Infinity)) structural.push(`diff real com ${diff.added} linhas adicionadas; limite ${guard.max_planned_added_lines}`);
      if (guard && diff.deleted > Number(guard.max_planned_deleted_lines || Infinity)) structural.push(`diff real com ${diff.deleted} linhas removidas; limite ${guard.max_planned_deleted_lines}`);
    }
  }
  const document = current?.document;
  if (current?.stage === 'concluido' || current?.stage === 'entregue') {
    if (!document?.path) delivery.push('documento final não registrado');
    else if (pathLike(document.path) && !fs.existsSync(document.path)) delivery.push('documento final não encontrado');
    const images = asList(document?.evidence_images);
    for (const image of images) if (!fs.existsSync(image)) delivery.push(`evidência do documento ausente (${image})`);
    if (!images.length && all.some(x => UI_TEST.test(`${x.type || ''} ${x.name || ''} ${x.command || ''}`))) delivery.push('documento final sem imagens de evidência para teste de tela');
  }
  const commitReady = !structural.length && !evidence.length;
  const deliveryReady = commitReady && !delivery.length;
  const metrics = metricsSnapshot(store, record, current, { commitReady, deliveryReady });
  return { commitReady, deliveryReady, structural, evidence, delivery, entries, metrics };
}

export function riskReport(record, state, quality, runtime = null) {
  const reasons = [], metrics = quality?.metrics || {}, stage = state?.stage || 'cadastrado';
  let score = 0;
  if (quality?.structural?.length) { score += 4; reasons.push(`${quality.structural.length} pendência(s) estrutural(is)`); }
  if (quality?.evidence?.length) { score += 3; reasons.push(`${quality.evidence.length} evidência(s) inconsistente(s)`); }
  if (quality?.delivery?.length) { score += 4; reasons.push(`${quality.delivery.length} bloqueio(s) de entrega`); }
  if (stage === 'bloqueado' || runtime?.phase === 'error') { score += 4; reasons.push('ticket bloqueado ou com erro de sessão'); }
  if (runtime?.phase === 'running' && runtime?.heartbeat && Date.now() - Date.parse(runtime.heartbeat) > 25_000) { score += 2; reasons.push('sessão sem heartbeat recente'); }
  if (Number(metrics.tests_not_executed) > 0) { score += 3; reasons.push(`${metrics.tests_not_executed} teste(s) não executado(s)`); }
  if (Number(metrics.scope_expansions) > 0) { score += 2; reasons.push(`${metrics.scope_expansions} expansão(ões) de escopo`); }
  if (Number(metrics.escaped_findings) > 0) { score += 2; reasons.push(`${metrics.escaped_findings} achado(s) identificado(s) em revisão`); }
  if (Number(metrics.tokens?.estimated_total) > 150_000) { score += 1; reasons.push('sessão com contexto estimado alto'); }
  const level = score >= 7 ? 'red' : score >= 3 ? 'yellow' : 'green';
  return { level, label: level === 'red' ? 'Alto risco' : level === 'yellow' ? 'Atenção' : 'Controlado', score, reasons: reasons.slice(0, 8) };
}

export function preflightReport(store, record) {
  const blocking = [], warnings = [];
  if (!record.repository || !fs.existsSync(path.join(record.repository, '.git'))) blocking.push('repositório Git não encontrado');
  if (!['claude', 'codex'].includes(record.agent)) blocking.push('agente inválido');
  const executable = record.agent === 'codex' ? store.config.codex : store.config.claude;
  if (!executable || !fs.existsSync(executable)) blocking.push(`${record.agent} não localizado`);
  try { const attachments = store.attachmentInfo(record); if (attachments.missing.length) blocking.push(`anexos ausentes: ${attachments.missing.join(', ')}`); } catch (e) { blocking.push(e.message); }
  if (record.prepared && (!record.worktree || !inside(store.config.new_worktrees_root, record.worktree) || !fs.existsSync(record.worktree))) blocking.push('worktree ausente ou fora do diretório gerenciado');
  try { const state = store.state(record.id); if (!state || !fs.existsSync(path.join(store.stateDir(record.id), 'test-matrix.json')) || !fs.existsSync(path.join(store.stateDir(record.id), 'evidence-manifest.json'))) blocking.push('arquivos de controle de qualidade ausentes'); } catch (e) { blocking.push(e.message); }
  if (!store.config.repository) blocking.push('perfil sem repositório padrão');
  if (!store.config.ai_memory_url) warnings.push('AI Memory usa o endereço local padrão; falhas serão preservadas no outbox.');
  return { ok: blocking.length === 0, blocking, warnings, checked_at: now() };
}

function ensureQualityFiles(store, record) {
  const root = store.stateDir(record.id);
  fs.mkdirSync(path.join(root, 'scratch', 'evidence'), { recursive: true });
  const matrix = path.join(root, 'test-matrix.json');
  if (!fs.existsSync(matrix)) atomicJson(matrix, { schema_version: 1, ticket: record.id, criteria: [], instruction: 'Uma entrada por critério/variante; registre esperado, obtido, resultado e evidência.', updated_at: now() });
  const manifest = path.join(root, 'evidence-manifest.json');
  if (!fs.existsSync(manifest)) atomicJson(manifest, { schema_version: 1, ticket: record.id, entries: [], instruction: 'Liste somente evidências existentes, com caminho relativo ao estado ou documento.', updated_at: now() });
}
export class AppError extends Error {
  constructor(message, status = 400) { super(message); this.status = status; }
}
export function readJson(file, fallback = null) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '')); }
  catch (e) { if (e.code === 'ENOENT') return fallback; throw new AppError(`Não foi possível ler ${path.basename(file)}. O arquivo foi preservado.`, 409); }
}
export function atomicJson(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const temporary = `${file}.${crypto.randomUUID()}.tmp`;
  try { fs.writeFileSync(temporary, JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 }); fs.renameSync(temporary, file); }
  finally { if (fs.existsSync(temporary)) fs.unlinkSync(temporary); }
}
export function ticketId(value) {
  if (typeof value !== 'string' || !/^[a-z]{2,8}\d{2,8}$/i.test(value.trim())) throw new AppError('Informe o ticket com prefixo e número, por exemplo GER5800.');
  return value.trim().toUpperCase();
}
export function attachmentName(value) {
  if (typeof value !== 'string' || !value || value.length > 200 || /[<>:"/\\|?*\x00-\x1f]/.test(value) || value === '.' || value === '..' || /[. ]$/.test(value)) throw new AppError('Nome de anexo inválido. Escolha um arquivo com nome simples, sem caminho.');
  return value;
}
export function validateTicket(input) {
  const id = ticketId(input.ticket);
  const sprint = String(input.sprint ?? '').trim();
  if (!/^[1-9]\d{0,5}$/.test(sprint)) throw new AppError('Informe uma sprint numérica válida.');
  const release = String(input.release ?? '').trim();
  if (!/^(?:main|pre_main|release\/[a-zA-Z0-9][a-zA-Z0-9._/-]{0,90})$/.test(release) || /\.\.|\/\/|\/$|\.$|\.lock(?:\/|$)|\/\./.test(release)) throw new AppError('Informe uma origem válida: release/106.4.3, main ou pre_main.');
  const scope = String(input.scope ?? '').trim();
  if (scope.length < 20 || scope.length > 100_000) throw new AppError('O escopo deve ter entre 20 e 100.000 caracteres.');
  const title = String(input.title ?? '').trim().slice(0, 160) || scope.split(/\r?\n/)[0].slice(0, 100);
  if (!['local_commit_only', 'no_commit'].includes(input.delivery)) throw new AppError('Escolha uma opção de entrega.');
  const attachments = Array.isArray(input.attachments) ? input.attachments : [];
  if (attachments.length > 40) throw new AppError('Liste no máximo 40 anexos.');
  attachments.forEach(attachmentName);
  const branchType = String(input.branchType || input.branch_type || 'feature').toLowerCase();
  if (!['feature', 'hotfix'].includes(branchType)) throw new AppError('Escolha branch feature ou hotfix.');
  const azureSource = input.azure_source && typeof input.azure_source === 'object' ? { id: Number(input.azure_source.id) || null, url: String(input.azure_source.url || '').slice(0, 500), importedAt: String(input.azure_source.importedAt || now()) } : null;
  return { id, ticket: id, sprint, release, branch_type: branchType, scope, title, delivery: input.delivery, attachments: [...new Set(attachments)], repository: input.repository, repositoryId: input.repositoryId, repository_standard: input.repository_standard || null, integration_branch: input.integration_branch || null, agent: input.agent || 'claude', azure_source: azureSource };
}
// Resolve existing ancestors too: a junction inside an allowed folder must not escape it.
export function realTarget(value) {
  let current = path.resolve(value); const rest = [];
  while (!fs.existsSync(current)) { const parent = path.dirname(current); if (parent === current) break; rest.unshift(path.basename(current)); current = parent; }
  return path.resolve(fs.realpathSync.native(current), ...rest);
}
export function inside(root, candidate) {
  const relative = path.relative(realTarget(root), realTarget(candidate));
  return relative === '' || (!path.isAbsolute(relative) && relative !== '..' && !relative.startsWith(`..${path.sep}`));
}
export function alive(pid) {
  if (!Number.isSafeInteger(pid) || pid <= 0) return false;
  try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; }
}
export function redact(text) {
  return String(text).replace(/(https?:\/\/)[^\s/@]+:[^\s/@]+@/gi, '$1[credencial]@')
    .replace(/^.*(?:password\s*[=:]|pwd\s*=|api[_-]?key\s*[=:]|access[_-]?token\s*[=:]|connectionString\s*=).*$/gim, '[linha com possível credencial omitida]');
}
export function ticketConfig(config, record = {}) {
  const repository = path.resolve(record.repository || config.repository);
  const configured = path.resolve(config.repository);
  if (repository.toLowerCase() === configured.toLowerCase()) return config;
  const repositoriesRoot = config.repositories_root || path.dirname(configured);
  if (!inside(repositoriesRoot, repository)) throw new AppError('Repositório fora da pasta permitida.');
  const slug = path.basename(repository).toLowerCase().replace(/[^a-z0-9._-]+/g, '-');
  const stateBase = config.state_base_root || path.dirname(config.state_root);
  const worktreeBase = config.worktrees_base_root || path.dirname(config.new_worktrees_root);
  return { ...config, repository, state_root: path.join(stateBase, slug), new_worktrees_root: path.join(worktreeBase, slug) };
}
export async function runGit(config, cwd, args) {
  try {
    return (await exec(config.git_executable, ['-C', cwd, ...args], { windowsHide: true, timeout: 120_000, maxBuffer: 2 * 1024 * 1024, env: { ...process.env, GIT_OPTIONAL_LOCKS: '0', GIT_TERMINAL_PROMPT: '0' } })).stdout.trim();
  } catch (e) { throw new AppError(`Git: ${redact(e.stderr || e.message).slice(0, 1800)}`, 409); }
}
export function defaultConfig(appRoot) {
  const home = process.env.USERPROFILE;
  const skill = path.join(home, '.claude', 'skills', 'desenvolver-ticket');
  const profile = readJson(path.join(skill, 'perfil.json'));
  if (!profile) throw new AppError('O comando desenvolver-ticket não está instalado neste usuário.');
  const candidates = [path.join(home, 'AppData/Roaming/npm/node_modules/@anthropic-ai/claude-code/bin/claude.exe'), path.join(home, '.local/bin/claude.exe')];
  const codexCandidates = [path.join(home, 'AppData/Roaming/npm/codex.cmd'), path.join(home, 'AppData/Roaming/npm/codex.exe'), path.join(home, '.local/bin/codex.exe')];
  return { ...profile, appRoot, skill, dataRoot: path.join(process.env.LOCALAPPDATA, 'CentralTicketsClaude'), claude: candidates.find(fs.existsSync) ?? null, codex: codexCandidates.find(fs.existsSync) ?? null, repositories_root: profile.repositories_root || path.dirname(profile.repository), state_base_root: profile.state_base_root || path.dirname(profile.state_root), worktrees_base_root: profile.worktrees_base_root || path.dirname(profile.new_worktrees_root), node: process.execPath, azure: profile.azure || { organization: 'sistemasunion', project: 'SSUnion' } };
}

export class Store {
  constructor(config) {
    this.config = config;
    this.listeners = new Set();
    this.root = config.dataRoot;
    fs.mkdirSync(path.join(this.root, 'tickets'), { recursive: true });
    // A terminal/agent can be closed abruptly, leaving a lock behind without
    // giving SessionEnd a chance to release it. Reconcile those locks whenever
    // a Central process or hook touches the store.
    this.reapStaleLocks();
  }
  dir(id) { return path.join(this.root, 'tickets', ticketId(id).toLowerCase()); }
  file(id, name) { return path.join(this.dir(id), name); }
  stateDir(id) { const record = readJson(this.file(id, 'ticket.json')); return path.join(ticketConfig(this.config, record || {}).state_root, ticketId(id).toLowerCase()); }
  stateFile(id) { return path.join(this.stateDir(id), 'estado.json'); }
  get(id) { const value = readJson(this.file(id, 'ticket.json')); if (!value) throw new AppError('Ticket não encontrado.', 404); return value; }
  save(record) { atomicJson(this.file(record.id, 'ticket.json'), { ...record, updatedAt: now() }); }
  subscribe(listener) { this.listeners.add(listener); return () => this.listeners.delete(listener); }
  notify(payload = {}) { for (const listener of this.listeners) { try { listener(payload); } catch {} } }
  runtime(id) { return readJson(this.file(id, 'runtime.json')); }
  reapStaleLocks() {
    const cleaned = [];
    const optionalJson = file => {
      try { return { value: readJson(file), valid: true }; }
      catch { return { value: null, valid: false }; }
    };
    const markStopped = (id, runtime, reason) => {
      if (!runtime || !['starting', 'running'].includes(runtime.phase)) return;
      const endedAt = now();
      atomicJson(this.file(id, 'runtime.json'), { ...runtime, phase: 'stopped', endedAt, heartbeat: runtime.heartbeat ?? null, recovery: reason });
    };
    const ticketRoot = path.join(this.root, 'tickets');
    let entries = [];
    try { entries = fs.readdirSync(ticketRoot, { withFileTypes: true }); } catch { return cleaned; }
    for (const entry of entries) {
      if (!entry.isDirectory() || !/^[a-z]{2,8}\d{2,8}$/i.test(entry.name)) continue;
      const id = entry.name.toUpperCase(), lockFile = this.file(id, 'active.lock'), lockResult = optionalJson(lockFile), runtimeResult = optionalJson(this.file(id, 'runtime.json'));
      // Never delete a malformed lock/state file automatically: preserve it
      // for diagnosis and let the normal corrupted-state guard report it.
      if (!lockResult.valid || !runtimeResult.valid) continue;
      const lock = lockResult.value, runtime = runtimeResult.value;
      const pids = [lock?.pid, runtime?.pid, runtime?.childPid].filter(Number.isSafeInteger);
      if (lock && !pids.some(alive)) {
        try { fs.unlinkSync(lockFile); } catch (e) { if (e.code !== 'ENOENT') continue; }
        markStopped(id, runtime, 'stale_lock_recovered');
        try { this.event(id, 'recuperacao', 'Reserva de sessão órfã liberada automaticamente: nenhum processo do ticket estava vivo.'); } catch {}
        cleaned.push(id);
      } else if (!lock && runtime && ['starting', 'running'].includes(runtime.phase) && ![runtime.pid, runtime.childPid].some(alive)) {
        markStopped(id, runtime, 'stale_runtime_recovered');
      }
    }
    const resourceFile = path.join(this.root, 'execution.lock'), ownerResult = optionalJson(resourceFile);
    if (!ownerResult.valid) return cleaned;
    const owner = ownerResult.value;
    if (owner) {
      const runtimeResult = optionalJson(this.file(owner.id, 'runtime.json'));
      if (!runtimeResult.valid) return cleaned;
      const runtime = runtimeResult.value;
      const pids = [owner.pid, runtime?.pid, runtime?.childPid].filter(Number.isSafeInteger);
      if (!pids.some(alive)) {
        try { fs.unlinkSync(resourceFile); } catch (e) { if (e.code !== 'ENOENT') return cleaned; }
        markStopped(owner.id, runtime, 'stale_execution_lock_recovered');
        try { this.event(owner.id, 'recuperacao', 'Reserva compartilhada de compilação/testes órfã liberada automaticamente.'); } catch {}
        cleaned.push(`${owner.id}:execution`);
      }
    }
    return cleaned;
  }
  event(id, type, message) {
    fs.mkdirSync(this.dir(id), { recursive: true });
    const event = { at: now(), type, message: redact(message).slice(0, 1600) };
    fs.appendFileSync(this.file(id, 'events.jsonl'), JSON.stringify(event) + '\n', { mode: 0o600 });
    this.notify({ id: ticketId(id), type });
  }
  events(id) {
    try { return fs.readFileSync(this.file(id, 'events.jsonl'), 'utf8').trim().split('\n').slice(-60).flatMap(x => { try { return [JSON.parse(x)]; } catch { return []; } }).reverse(); }
    catch (e) { if (e.code === 'ENOENT') return []; throw e; }
  }
  create(input) {
    const ticket = validateTicket(input), dir = this.dir(ticket.id);
    if (fs.existsSync(dir) || fs.existsSync(this.stateFile(ticket.id))) throw new AppError('Este ticket já possui cadastro ou estado do coordenador. Preserve o trabalho existente.', 409);
    fs.mkdirSync(dir); // Atomic duplicate prevention across application processes.
    const sessionId = crypto.randomUUID();
    const record = { ...ticket, repository: ticket.repository || this.config.repository, repositoryId: ticket.repositoryId || path.basename(ticket.repository || this.config.repository), agent: ticket.agent || 'claude', createdAt: now(), updatedAt: now(), prepared: false, sessionId, sessionIds: [sessionId] };
    this.save(record); this.event(ticket.id, 'cadastro', 'Ticket cadastrado. Nenhuma sessão ou alteração de código foi iniciada.');
    atomicJson(path.join(this.root, 'preferences.json'), { sprint: ticket.sprint, release: ticket.release });
    return record;
  }
  state(id) { return readJson(this.stateFile(id)); }
  currentApproval(id) {
    const approval = readJson(this.file(id, 'approval.json'));
    const file = path.join(this.stateDir(id), 'escopo.md');
    if (!approval || !fs.existsSync(file)) return null;
    return hash(fs.readFileSync(file)) === approval.scopeHash ? approval : null;
  }
  attachmentInfo(record) {
    const dir = path.join(this.config.references_root, record.id.toLowerCase());
    if (!inside(this.config.references_root, dir)) throw new AppError('Pasta de anexos fora do local configurado.');
    const names = fs.existsSync(dir) ? fs.readdirSync(dir, { withFileTypes: true }).filter(x => x.isFile()).map(x => x.name) : [];
    const missing = record.attachments.filter(x => !names.some(n => n.toLowerCase() === x.toLowerCase()));
    return { directory: dir, exists: fs.existsSync(dir), names, missing };
  }
  summary(id) {
    const ticket = this.get(id); let state = null, stateError = null;
    try { state = this.state(id); } catch (e) { stateError = e.message; }
    const runtime = this.runtime(id);
    const running = !!runtime && (alive(runtime.pid) || alive(runtime.childPid)) && ['starting', 'running'].includes(runtime.phase);
    const session = running ? (Date.now() - Date.parse(runtime.heartbeat || runtime.startedAt) > 25_000 ? 'uncertain' : 'running') : runtime ? 'stopped' : 'none';
    const rawStage = state?.stage === 'entregue' ? 'concluido' : state?.stage;
    const quality = stateError ? { commitReady: false, deliveryReady: false, structural: [stateError], evidence: [], delivery: [], entries: [], metrics: null } : qualityReport(this, ticket, state);
    const stage = stateError ? 'bloqueado' : (['concluido'].includes(rawStage) && !quality.deliveryReady) ? 'bloqueado' : rawStage in STAGES ? rawStage : ticket.prepared ? 'analise' : 'cadastrado';
    const risk = riskReport(ticket, state, quality, runtime);
    let attachments = { names: [], missing: [], exists: false };
    try { const info = this.attachmentInfo(ticket); attachments = { names: info.names, missing: info.missing, exists: info.exists }; } catch (e) { attachments.error = e.message; }
    return { id: ticket.id, ticket: ticket.ticket, sprint: ticket.sprint, release: ticket.release, title: ticket.title, delivery: ticket.delivery, agent: ticket.agent, repositoryId: ticket.repositoryId, prepared: !!ticket.prepared, branch: ticket.branch, worktree: ticket.worktree, updatedAt: ticket.updatedAt, stage, stageLabel: STAGES[stage], stateError, session, runtime: runtime ? { phase: runtime.phase, error: runtime.error, heartbeat: runtime.heartbeat } : null, risk, quality: { commitReady: quality.commitReady, deliveryReady: quality.deliveryReady, issues: [...quality.structural, ...quality.evidence, ...quality.delivery].slice(0, 12), counts: { structural: quality.structural.length, evidence: quality.evidence.length, delivery: quality.delivery.length } }, metrics: quality.metrics, attachments: { missing: attachments.missing, count: attachments.names.length, exists: attachments.exists, error: attachments.error } };
  }
  view(id) {
    const ticket = this.get(id); let state, stateError = null;
    try { state = this.state(id); } catch (e) { stateError = e.message; }
    const runtime = this.runtime(id), running = !!runtime && (alive(runtime.pid) || alive(runtime.childPid)) && ['starting', 'running'].includes(runtime.phase);
    const session = running ? (Date.now() - Date.parse(runtime.heartbeat || runtime.startedAt) > 25_000 ? 'uncertain' : 'running') : runtime ? 'stopped' : 'none';
    const rawStage = state?.stage === 'entregue' ? 'concluido' : state?.stage;
    const checkDiff = ['revisao', 'entrega', 'aguardando_validacao_manual', 'concluido'].includes(state?.stage);
    const quality = stateError ? { commitReady: false, deliveryReady: false, structural: [stateError], evidence: [], delivery: [], entries: [], metrics: null } : qualityReport(this, ticket, state, { checkDiff });
    const stage = stateError ? 'bloqueado' : (['concluido'].includes(rawStage) && !quality.deliveryReady) ? 'bloqueado' : rawStage in STAGES ? rawStage : ticket.prepared ? 'analise' : 'cadastrado';
    const risk = riskReport(ticket, state, quality, runtime);
    let attachments; try { attachments = this.attachmentInfo(ticket); } catch (e) { attachments = { names: [], missing: [], error: e.message }; }
    return { ...ticket, stage, stageLabel: STAGES[stage], state: state ?? null, stateError, session, runtime, approval: this.currentApproval(id), risk, quality, metrics: quality.metrics, attachments, events: this.events(id) };
  }
  list(options = {}) {
    const stageFilter = options.stage || 'active', query = String(options.q || '').trim().toLowerCase(), repository = String(options.repository || '').toLowerCase(), agent = String(options.agent || '').toLowerCase(), limit = Math.min(Math.max(Number(options.limit) || 250, 1), 500);
    let entries = [];
    try { entries = fs.readdirSync(path.join(this.root, 'tickets'), { withFileTypes: true }); } catch { return []; }
    return entries.filter(x => x.isDirectory() && /^[a-z]{2,8}\d{2,8}$/i.test(x.name)).map(x => this.summary(x.name)).filter(ticket => {
      if (stageFilter === 'active' && ticket.stage === 'concluido') return false;
      if (stageFilter === 'done' && ticket.stage !== 'concluido') return false;
      if (repository && String(ticket.repositoryId || '').toLowerCase() !== repository) return false;
      if (agent && String(ticket.agent || '').toLowerCase() !== agent) return false;
      return !query || `${ticket.id} ${ticket.title} ${ticket.release}`.toLowerCase().includes(query);
    }).sort((a, b) => String(b.updatedAt).localeCompare(String(a.updatedAt))).slice(0, limit);
  }
  stats() {
    const all = this.list({ stage: 'all', limit: 500 });
    const tokens = all.reduce((sum, x) => sum + Number(x.metrics?.tokens?.estimated_total || 0), 0);
    const notExecuted = all.reduce((sum, x) => sum + Number(x.metrics?.tests_not_executed || 0), 0), testCount = all.reduce((sum, x) => sum + Number(x.metrics?.test_count || 0), 0);
    return { total: all.length, active: all.filter(x => x.stage !== 'concluido').length, running: all.filter(x => ['running', 'uncertain'].includes(x.session)).length, attention: all.filter(x => /aguardando|bloqueado/.test(x.stage) || x.runtime?.phase === 'error' || x.stateError).length, done: all.filter(x => x.stage === 'concluido').length, risks: { red: all.filter(x => x.risk?.level === 'red').length, yellow: all.filter(x => x.risk?.level === 'yellow').length, green: all.filter(x => x.risk?.level === 'green').length }, estimated_tokens: tokens, tests_not_executed: notExecuted, tests_not_executed_rate: testCount ? notExecuted / testCount : 0, blocked_seconds: all.reduce((sum, x) => sum + Number(x.metrics?.blocked_seconds || 0), 0) };
  }
  reserve(id) {
    this.get(id);
    const lock = this.file(id, 'active.lock'), previous = readJson(lock);
    if (previous) {
      const runtime = this.runtime(id);
      if (alive(runtime?.pid) || alive(runtime?.childPid) || alive(previous.pid)) throw new AppError('Já existe uma sessão ou abertura em andamento para este ticket. Use Abrir conversa.', 409);
      fs.unlinkSync(lock);
    }
    const run = { runId: crypto.randomUUID(), id: ticketId(id), pid: process.pid, phase: 'starting', startedAt: now(), heartbeat: now() };
    try { fs.writeFileSync(lock, JSON.stringify(run), { flag: 'wx', mode: 0o600 }); }
    catch (e) { if (e.code === 'EEXIST') throw new AppError('Outra sessão está abrindo este ticket.', 409); throw e; }
    atomicJson(this.file(id, 'runtime.json'), run); return run;
  }
  release(id, runId) {
    const lock = this.file(id, 'active.lock');
    if (readJson(lock)?.runId === runId) fs.unlinkSync(lock);
    const resource = path.join(this.root, 'execution.lock');
    if (readJson(resource)?.runId === runId) fs.unlinkSync(resource);
  }
}

export async function verifyWorktree(store, record, git = runGit) {
  const config = ticketConfig(store.config, record), worktree = record.worktree;
  if (!worktree || !inside(config.new_worktrees_root, worktree) || !fs.existsSync(worktree)) throw new AppError('Worktree ausente ou fora do local gerenciado. Nada foi sobrescrito.', 409);
  const [common, mainCommon, branch, head] = await Promise.all([
    git(config, worktree, ['rev-parse', '--path-format=absolute', '--git-common-dir']),
    git(config, config.repository, ['rev-parse', '--path-format=absolute', '--git-common-dir']),
    git(config, worktree, ['branch', '--show-current']), git(config, worktree, ['rev-parse', 'HEAD'])
  ]);
  if (realTarget(common).toLowerCase() !== realTarget(mainCommon).toLowerCase() || branch !== record.branch) throw new AppError('A worktree não corresponde ao repositório e à branch deste ticket.', 409);
  return { worktree, branch, head };
}
export async function prepare(store, record, git = runGit) {
  const config = ticketConfig(store.config, record);
  if (record.prepared) { await verifyWorktree(store, record, git); ensureState(store, record); return record; }
  if (record.preparation && fs.existsSync(record.preparation.worktree)) {
    const p = record.preparation;
    const recovered = { ...record, prepared: true, branch: p.branch, worktree: p.worktree, baseSha: p.base };
    const verified = await verifyWorktree(store, recovered, git);
    if (verified.head !== p.base) throw new AppError('A preparação interrompida contém commits posteriores. Confira o trabalho existente antes de recuperar.', 409);
    store.save(recovered); ensureState(store, recovered);
    store.event(record.id, 'recuperacao', 'Preparação interrompida recuperada após conferir worktree, branch e commit de origem.');
    return recovered;
  }
  await git(config, config.repository, ['check-ref-format', '--branch', record.release]);
  const branch = `${record.branch_type || config.branch_type || 'feature'}/${record.sprint}/${record.id.toLowerCase()}`;
  const worktree = path.join(config.new_worktrees_root, `${record.sprint}-${record.id.toLowerCase()}`);
  fs.mkdirSync(config.new_worktrees_root, { recursive: true });
  if (!inside(config.new_worktrees_root, worktree) || fs.existsSync(worktree)) throw new AppError('A pasta prevista para este ticket já existe. Nada foi substituído.', 409);
  const branches = await git(config, config.repository, ['for-each-ref', '--format=%(refname:short)', `refs/heads/${branch}`]);
  if (branches.split(/\r?\n/).includes(branch)) throw new AppError('A branch já existe fora deste cadastro. Retome o trabalho original antes de iniciar outro.', 409);
  const remotes = (await git(config, config.repository, ['remote'])).split(/\r?\n/).filter(Boolean);
  const remote = remotes.includes('origin') ? 'origin' : remotes.length === 1 ? remotes[0] : null;
  if (!remote || !/^[a-zA-Z0-9_-]+$/.test(remote)) throw new AppError('Não foi possível identificar um único remoto para a release.', 409);
  store.event(record.id, 'preparacao', `Atualizando ${record.release} e preparando worktree exclusiva.`);
  await git(config, config.repository, ['fetch', '--no-tags', remote, `${record.release}:refs/remotes/${remote}/${record.release}`]);
  const base = await git(config, config.repository, ['rev-parse', '--verify', `refs/remotes/${remote}/${record.release}^{commit}`]);
  if (!/^[a-f0-9]{40,64}$/.test(base)) throw new AppError('O Git não retornou um commit válido para a release.', 409);
  // Journal before mutation lets a failed launch be diagnosed without destructive rollback.
  store.save({ ...record, preparation: { branch, worktree, base, at: now() } });
  await git(config, config.repository, ['worktree', 'add', '--no-track', '-b', branch, worktree, base]);
  record = { ...record, prepared: true, branch, worktree, baseSha: base };
  store.save(record);
  ensureState(store, record);
  store.event(record.id, 'preparacao', 'Worktree criada. O checkout principal foi preservado.');
  return record;
}
function ensureState(store, record) {
  const config = store.config, { worktree, branch, baseSha: base } = record;
  fs.mkdirSync(store.stateDir(record.id), { recursive: true });
  if (!fs.existsSync(store.stateFile(record.id))) atomicJson(store.stateFile(record.id), {
    schema_version: 2, ticket: record.id, sprint: record.sprint, repository: config.repository,
    worktree, branch, base_ref: record.release, base_sha: base, stage: 'analise', scope_revision: 1,
    implementation_approval: null, approved_scope_sha256: null, active_session: record.sessionId,
    integration_branch: record.integration_branch || null,
    delivery: { commit_allowed: record.delivery !== 'no_commit', push_allowed: false, pr_allowed: false },
    build: [], tests: [], review: null, resources_created: { databases: [], backups: [], scratch_files: [] },
    scope_guard: config.scope_guard ?? { default_mode: 'minimal_patch', max_planned_files: 6, max_planned_added_lines: 180, max_planned_deleted_lines: 120, max_correction_rounds: 1, new_projects_or_tables_require_reapproval: true },
    change_budget: { planned_files: [], estimated_added_lines: null, estimated_deleted_lines: null, approved_expansion: false, correction_rounds: 0 },
    blockers: [], next_action: 'Provar a menor correcao, registrar o plano minimo e aguardar aprovacao.', updated_at: now()
  });
  ensureQualityFiles(store, record);
}

export function initialPrompt(store, record) {
  const erp = path.resolve(record.repository || '') === path.resolve(store.config.repository || '');
  const prefix = erp && record.agent !== 'codex' ? '/desenvolver-ticket\n' : '';
  const standard = record.repository_standard?.integration === 'pre_main' ? ` Regras deste repositório: origem main, destino de homologação pre_main, PR obrigatório e sem push direto. Atualize o clone antes de começar quando necessário. ${record.repository_standard.restore_nuget ? 'No WCF do FVA, execute Restore NuGet Packages antes do primeiro build.' : ''} Não versione bin/, obj/, .vs/ ou packages/.` : '';
  return `${prefix}Leia a entrada completa em ${store.file(record.id, 'entrada.md')}. ` +
    `Este ticket foi aberto pela Central para o repositório ${record.repository}. A worktree ${record.worktree} e a branch ${record.branch} já estão prontas na base ${record.baseSha}. ` +
    `Use o estado em ${store.stateFile(record.id)}; não crie outra worktree. A branch usa o tipo ${record.branch_type || 'feature'} e a origem ${record.release}; o destino de homologação é ${record.integration_branch || 'manual/definido pelo repositório'}.${standard} Primeiro faça uma análise mínima, registre o plano e aguarde APROVAR ${record.id} antes de alterar o código. Preserve o que já funciona e não expanda o escopo sem nova aprovação.`;
}
export function writeSessionFiles(store, record) {
  const erp = path.resolve(record.repository || '') === path.resolve(store.config.repository || '');
  fs.writeFileSync(store.file(record.id, 'entrada.md'), `# ${record.id} — ${record.title}\n\nTicket: ${record.id}\nSprint: ${record.sprint}\nRelease: ${record.release}\nEntrega: ${record.delivery === 'no_commit' ? 'sem commit e sem push' : 'commit local, sem push'}\n\n## Escopo funcional\n\n${record.scope}\n`, 'utf8');
  // Hooks come from an ephemeral --plugin-dir, alongside existing AI Memory hooks.
  // This settings file adds only session-scoped deny rules, never replaces global hooks.
  const settings = {
    permissions: { allow: ['mcp__central-computer__computer_screenshot', 'mcp__central-computer__computer_active_window', 'mcp__central-computer__computer_click', 'mcp__central-computer__computer_type', 'mcp__central-computer__computer_key', 'mcp__central-computer__computer_wait'], deny: ['Bash(git push *)', 'Bash(git -C * push *)', 'PowerShell(git push *)'] }
  };
  atomicJson(store.file(record.id, 'session-settings.json'), settings);
  const evidenceRoot = path.join(store.stateDir(record.id), 'scratch', 'evidence');
  atomicJson(store.file(record.id, 'mcp-servers.json'), { mcpServers: { 'central-computer': { command: store.config.node || process.execPath, args: [path.join(store.config.appRoot, 'central', 'computer-use.mjs')], env: { CENTRAL_TICKET_ID: record.id, CENTRAL_TICKET_EVIDENCE: evidenceRoot } } } });
  const standardGuidance = record.repository_standard?.integration === 'pre_main' ? 'Use main como origem e pre_main como destino de homologação; push direto não faz parte deste fluxo. Atualize o clone antes de começar se necessário. ' + (record.repository_standard.restore_nuget ? 'No WCF do FVA, execute Restore NuGet Packages antes do primeiro build. ' : '') + 'Não versione bin/, obj/, .vs/ ou packages/.\n' : '';
  fs.writeFileSync(store.file(record.id, 'session-context.txt'), `${erp ? '' : 'Este ticket pertence a um repositório genérico; não aplique regras específicas do ERP, SQL ou da skill desenvolver-ticket sem que o escopo as exija.\n'}` + standardGuidance + `Esta sessao pertence ao ticket ${record.id} da Central de Tickets.\n` +
    `Leia e mantenha o estado do coordenador em ${store.stateFile(record.id)}. Nunca edite o cadastro, approval.json, active.lock ou os scripts da Central.\n` +
    `Antes de implementar, salve o plano minimo em ${path.join(store.stateDir(record.id), 'escopo.md')}: comportamento atual que ja funciona, causa confirmada, menor patch, arquivos/metodos, estimativa de linhas, testes diretamente afetados, limites negativos e uma secao Fora do escopo. Registre tambem change_budget no estado. Preserve tudo que ja satisfaz o aceite. Registre stage=aguardando_aprovacao e peca ao usuario a frase exata APROVAR ${record.id}. O hook registra essa decisao vinculada ao SHA-256 do plano. Nao aprove por memoria ou por conta propria.\n` +
    `Regra anti-delirio: nao refatore legado, nao crie mecanismo novo e nao corrija achado preexistente so porque parece melhor. Se precisar tocar arquivo/camada/tabela/projeto fora do plano, ou ultrapassar a estimativa/limite do scope_guard, PARE antes de editar, explique a expansao e solicite nova aprovacao. Nao use code review para autoautorizar expansao.\n` +
    `A Central criou ${path.join(store.stateDir(record.id), 'test-matrix.json')} e ${path.join(store.stateDir(record.id), 'evidence-manifest.json')}. Mantenha ambos sincronizados: uma entrada por criterio/variante e uma referencia para cada log, consulta, screenshot ou artefato realmente existente. Caminho inexistente, texto generico ou evidencia apenas presumida nao conta.\n` +
    `A Central registra automaticamente eventos, estimativa de tokens por fase, tempo bloqueado, chamadas de ferramenta, expansoes de escopo e encerramento da sessao em metrics.json. Nao invente numeros; quando houver uso/token informado pelo agente, registre a origem. O encerramento gera um aprendizado sanitizado para o AI Memory; nunca inclua credenciais, dumps ou dados de cliente.\n` +
    `Faca primeiro uma prova de suficiencia: escreva qual regra existente ja atende, qual linha/condicao causa o defeito e por que a correcao minima resolve. Uma rodada de correcao e o padrao; nova rodada exige decisao do usuario.\n` +
     `Antes de aprovar o plano, faca um mapa de variantes e rotinas paralelas: pesquise todas as entradas que chegam ao mesmo comportamento, implementacoes equivalentes em outros forms/DAOs/projetos, chamadas de banco/migrations e caminhos de inclusao, alteracao, exclusao e repeticao. Para schema/migration, compare explicitamente os estados FK inexistente, existente confiavel, NOCHECK/desabilitada, indices/constraints conflitantes, dados orfaos, reaplicacao e rollback. Registre uma matriz por variante com esperado, obtido e evidencia; cada caso relevante deve ser executado ou marcado BLOQUEADO/NAO EXECUTADO, nunca presumido por um unico caminho feliz. Para telas e eventos com estado, teste tambem sequencias de transicao (0->1->2, 2->1, desselecionar, reordenar, cancelar, confirmar e reabrir), diferenciando preferencia persistente de estado derivado da operacao; nunca altere ou grave uma preferencia apenas para limitar uma operacao momentanea.\n` +
     `Revisao obrigatoria antes de declarar concluido: releia o escopo e cada criterio de aceite, confira o diff completo contra a base (git diff --check, arquivos alterados, linhas geradas e arquivos fora do plano), procure regressao nos caminhos sem alteracao, valide cenarios positivo/negativo, limites e transicoes de estado, confirme que build/testes realmente executaram e registre review com verdict, evidencias, achados confirmados e pendencias. Nao trate compilacao parcial, simulacao, estado final isolado ou inspecao de uma unica funcao como revisao suficiente. Se houver qualquer duvida, resultado nao executado ou arquivo incidental, pare em revisao/validacao_manual e informe o usuario; nao marque concluido para encerrar a conversa.\n` +
    `Evidencias concretas sao obrigatorias no estado e no documento final: para cada criterio, registre comando completo (ou passos manuais), data/hora, worktree, branch, base_sha/commit, arquivos e linhas/metodos conferidos, ambiente e banco usado (servidor/instancia/base, sempre sem credenciais), resultado bruto resumido, esperado versus obtido e classificacao OK/FALHA/BLOQUEADO/NAO EXECUTADO. Para teste manual, registre pre-condicoes, dados de entrada, passos numerados, resultado observado e evidencia disponivel (log, screenshot ou consulta somente leitura). Nao escreva apenas “validado”, “testado”, “sem regressao” ou “funciona”: toda afirmacao deve apontar para uma evidencia verificavel.\n` +
    `A sessao possui a ferramenta MCP central-computer para operar a tela local: use computer_active_window, computer_screenshot (labels before/action/result), computer_click, computer_type, computer_key e computer_wait. Antes de cada cenario, confirme a janela em primeiro plano; capture os estados antes, acao e resultado, salve-os em ${evidenceRoot} e inclua as imagens no DOCX final com legenda, data/hora, ambiente, dados usados e esperado versus obtido. Nao capture credenciais. O print nao substitui logs/consultas quando eles forem necessarios. Se a ferramenta estiver indisponivel ou a tela nao puder ser executada, registre explicitamente SCREENSHOT_NAO_EXECUTADO e entregue um roteiro manual; nunca invente print ou trate ausencia de imagem como OK.\n` +
    `Antes de comandos de compilacao/testes/limpeza de banco, registre stage=compilacao ou testes. Execute esses comandos de forma sincrona. A central concede um recurso compartilhado de execucao a uma sessao por vez. Se ele estiver ocupado, informe o ticket dono, encerre o turno e aguarde o usuario pedir para continuar; nao faca tentativas repetidas nem contorne o controle. Antes de commitar, a Central exige compilacao, testes, revisao, esperado/obtido e referencias de evidencias existentes; antes de marcar concluido, tambem exige documento final e nenhum teste falho, bloqueado ou nao executado.\n` +
    `Ao sair dessa etapa, atualize stage e devolva o recurso; SessionEnd tambem libera a reserva. Se o usuario determinar que o ticket deve ser entregue, registre stage=concluido (a Central tambem aceita o legado stage=entregue), delivered.at, commits e pendencias declaradas. Nao publique branch, PR nem mensagens. A entrega configurada e ${record.delivery}.\n` +
    `Apos compactacao, releia o estado e a skill desenvolver-ticket. Se o usuario pedir outra tarefa/ticket, oriente abrir outra sessao pela Central.\n`, 'utf8');
}
