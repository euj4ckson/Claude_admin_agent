import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

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
  return { id, ticket: id, sprint, release, branch_type: branchType, scope, title, delivery: input.delivery, attachments: [...new Set(attachments)], repository: input.repository, repositoryId: input.repositoryId, repository_standard: input.repository_standard || null, integration_branch: input.integration_branch || null, agent: input.agent || 'claude' };
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
  return { ...profile, appRoot, skill, dataRoot: path.join(process.env.LOCALAPPDATA, 'CentralTicketsClaude'), claude: candidates.find(fs.existsSync) ?? null, codex: codexCandidates.find(fs.existsSync) ?? null, repositories_root: profile.repositories_root || path.dirname(profile.repository), state_base_root: profile.state_base_root || path.dirname(profile.state_root), worktrees_base_root: profile.worktrees_base_root || path.dirname(profile.new_worktrees_root), node: process.execPath };
}

export class Store {
  constructor(config) { this.config = config; this.root = config.dataRoot; fs.mkdirSync(path.join(this.root, 'tickets'), { recursive: true }); }
  dir(id) { return path.join(this.root, 'tickets', ticketId(id).toLowerCase()); }
  file(id, name) { return path.join(this.dir(id), name); }
  stateDir(id) { const record = readJson(this.file(id, 'ticket.json')); return path.join(ticketConfig(this.config, record || {}).state_root, ticketId(id).toLowerCase()); }
  stateFile(id) { return path.join(this.stateDir(id), 'estado.json'); }
  get(id) { const value = readJson(this.file(id, 'ticket.json')); if (!value) throw new AppError('Ticket não encontrado.', 404); return value; }
  save(record) { atomicJson(this.file(record.id, 'ticket.json'), { ...record, updatedAt: now() }); }
  runtime(id) { return readJson(this.file(id, 'runtime.json')); }
  event(id, type, message) {
    fs.mkdirSync(this.dir(id), { recursive: true });
    fs.appendFileSync(this.file(id, 'events.jsonl'), JSON.stringify({ at: now(), type, message: redact(message).slice(0, 1600) }) + '\n', { mode: 0o600 });
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
  view(id) {
    const ticket = this.get(id); let state, stateError = null;
    try { state = this.state(id); } catch (e) { stateError = e.message; }
    const runtime = this.runtime(id), running = !!runtime && (alive(runtime.pid) || alive(runtime.childPid)) && ['starting', 'running'].includes(runtime.phase);
    const session = running ? (Date.now() - Date.parse(runtime.heartbeat || runtime.startedAt) > 25_000 ? 'uncertain' : 'running') : runtime ? 'stopped' : 'none';
    const rawStage = state?.stage === 'entregue' ? 'concluido' : state?.stage;
    const stage = stateError ? 'bloqueado' : rawStage in STAGES ? rawStage : ticket.prepared ? 'analise' : 'cadastrado';
    let attachments; try { attachments = this.attachmentInfo(ticket); } catch (e) { attachments = { names: [], missing: [], error: e.message }; }
    return { ...ticket, stage, stageLabel: STAGES[stage], state: state ?? null, stateError, session, runtime, approval: this.currentApproval(id), attachments, events: this.events(id) };
  }
  list() { return fs.readdirSync(path.join(this.root, 'tickets'), { withFileTypes: true }).filter(x => x.isDirectory() && /^[a-z]{2,8}\d{2,8}$/i.test(x.name)).map(x => this.view(x.name)).sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)); }
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
    schema_version: 1, ticket: record.id, sprint: record.sprint, repository: config.repository,
    worktree, branch, base_ref: record.release, base_sha: base, stage: 'analise', scope_revision: 1,
    implementation_approval: null, approved_scope_sha256: null, active_session: record.sessionId,
    integration_branch: record.integration_branch || null,
    delivery: { commit_allowed: record.delivery !== 'no_commit', push_allowed: false, pr_allowed: false },
    build: [], tests: [], review: null, resources_created: { databases: [], backups: [], scratch_files: [] },
    scope_guard: config.scope_guard ?? { default_mode: 'minimal_patch', max_planned_files: 6, max_planned_added_lines: 180, max_planned_deleted_lines: 120, max_correction_rounds: 1, new_projects_or_tables_require_reapproval: true },
    change_budget: { planned_files: [], estimated_added_lines: null, estimated_deleted_lines: null, approved_expansion: false, correction_rounds: 0 },
    blockers: [], next_action: 'Provar a menor correcao, registrar o plano minimo e aguardar aprovacao.', updated_at: now()
  });
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
    permissions: { deny: ['Bash(git push *)', 'Bash(git -C * push *)', 'PowerShell(git push *)'] }
  };
  atomicJson(store.file(record.id, 'session-settings.json'), settings);
  const standardGuidance = record.repository_standard?.integration === 'pre_main' ? 'Use main como origem e pre_main como destino de homologação; push direto não faz parte deste fluxo. Atualize o clone antes de começar se necessário. ' + (record.repository_standard.restore_nuget ? 'No WCF do FVA, execute Restore NuGet Packages antes do primeiro build. ' : '') + 'Não versione bin/, obj/, .vs/ ou packages/.\n' : '';
  fs.writeFileSync(store.file(record.id, 'session-context.txt'), `${erp ? '' : 'Este ticket pertence a um repositório genérico; não aplique regras específicas do ERP, SQL ou da skill desenvolver-ticket sem que o escopo as exija.\n'}` + standardGuidance + `Esta sessao pertence ao ticket ${record.id} da Central de Tickets.\n` +
    `Leia e mantenha o estado do coordenador em ${store.stateFile(record.id)}. Nunca edite o cadastro, approval.json, active.lock ou os scripts da Central.\n` +
    `Antes de implementar, salve o plano minimo em ${path.join(store.stateDir(record.id), 'escopo.md')}: comportamento atual que ja funciona, causa confirmada, menor patch, arquivos/metodos, estimativa de linhas, testes diretamente afetados, limites negativos e uma secao Fora do escopo. Registre tambem change_budget no estado. Preserve tudo que ja satisfaz o aceite. Registre stage=aguardando_aprovacao e peca ao usuario a frase exata APROVAR ${record.id}. O hook registra essa decisao vinculada ao SHA-256 do plano. Nao aprove por memoria ou por conta propria.\n` +
    `Regra anti-delirio: nao refatore legado, nao crie mecanismo novo e nao corrija achado preexistente so porque parece melhor. Se precisar tocar arquivo/camada/tabela/projeto fora do plano, ou ultrapassar a estimativa/limite do scope_guard, PARE antes de editar, explique a expansao e solicite nova aprovacao. Nao use code review para autoautorizar expansao.\n` +
    `Faca primeiro uma prova de suficiencia: escreva qual regra existente ja atende, qual linha/condicao causa o defeito e por que a correcao minima resolve. Uma rodada de correcao e o padrao; nova rodada exige decisao do usuario.\n` +
    `Antes de aprovar o plano, faca um mapa de variantes e rotinas paralelas: pesquise todas as entradas que chegam ao mesmo comportamento, implementacoes equivalentes em outros forms/DAOs/projetos, chamadas de banco/migrations e caminhos de inclusao, alteracao, exclusao e repeticao. Para schema/migration, compare explicitamente os estados FK inexistente, existente confiavel, NOCHECK/desabilitada, indices/constraints conflitantes, dados orfaos, reaplicacao e rollback. Registre uma matriz por variante com esperado, obtido e evidencia; cada caso relevante deve ser executado ou marcado BLOQUEADO/NAO EXECUTADO, nunca presumido por um unico caminho feliz.\n` +
    `Revisao obrigatoria antes de declarar concluido: releia o escopo e cada criterio de aceite, confira o diff completo contra a base (git diff --check, arquivos alterados, linhas geradas e arquivos fora do plano), procure regressao nos caminhos sem alteracao, valide cenarios positivo/negativo e limites, confirme que build/testes realmente executaram e registre review com verdict, evidencias, achados confirmados e pendencias. Nao trate compilacao parcial, simulacao ou inspecao de uma unica funcao como revisao suficiente. Se houver qualquer duvida, resultado nao executado ou arquivo incidental, pare em revisao/validacao_manual e informe o usuario; nao marque concluido para encerrar a conversa.\n` +
    `Evidencias concretas sao obrigatorias no estado e no documento final: para cada criterio, registre comando completo (ou passos manuais), data/hora, worktree, branch, base_sha/commit, arquivos e linhas/metodos conferidos, ambiente e banco usado (servidor/instancia/base, sempre sem credenciais), resultado bruto resumido, esperado versus obtido e classificacao OK/FALHA/BLOQUEADO/NAO EXECUTADO. Para teste manual, registre pre-condicoes, dados de entrada, passos numerados, resultado observado e evidencia disponivel (log, screenshot ou consulta somente leitura). Nao escreva apenas “validado”, “testado”, “sem regressao” ou “funciona”: toda afirmacao deve apontar para uma evidencia verificavel.\n` +
    `Para qualquer teste em tela, capture prints dos estados relevantes (antes, acao e resultado), salve-os no scratch/evidence do ticket com nomes que identifiquem o cenario e inclua as imagens no DOCX final com legenda, data/hora, ambiente, dados usados e esperado versus obtido. O print nao substitui logs/consultas quando eles forem necessarios. Se a sessao nao tiver ferramenta de captura ou a tela nao puder ser executada, registre explicitamente SCREENSHOT_NAO_EXECUTADO e entregue um roteiro manual; nunca invente print ou trate ausencia de imagem como OK.\n` +
    `Antes de comandos de compilacao/testes/limpeza de banco, registre stage=compilacao ou testes. Execute esses comandos de forma sincrona. A central concede um recurso compartilhado de execucao a uma sessao por vez. Se ele estiver ocupado, informe o ticket dono, encerre o turno e aguarde o usuario pedir para continuar; nao faca tentativas repetidas nem contorne o controle.\n` +
    `Ao sair dessa etapa, atualize stage e devolva o recurso; SessionEnd tambem libera a reserva. Se o usuario determinar que o ticket deve ser entregue, registre stage=concluido (a Central tambem aceita o legado stage=entregue), delivered.at, commits e pendencias declaradas. Nao publique branch, PR nem mensagens. A entrega configurada e ${record.delivery}.\n` +
    `Apos compactacao, releia o estado e a skill desenvolver-ticket. Se o usuario pedir outra tarefa/ticket, oriente abrir outra sessao pela Central.\n`, 'utf8');
}
