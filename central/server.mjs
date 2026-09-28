import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { Store, AppError, defaultConfig, atomicJson, readJson, ticketId, attachmentName, prepare, verifyWorktree, writeSessionFiles, runGit, inside, redact, now, ticketConfig } from './core.mjs';
import { launchTerminal, focusTerminal, openLocal } from './desktop.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const publicRoot = path.join(here, 'public');
const mime = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.svg': 'image/svg+xml' };
function listRepositories(config) {
  const root = config.repositories_root || path.dirname(config.repository), found = [];
  try { for (const entry of fs.readdirSync(root, { withFileTypes: true })) { if (!entry.isDirectory()) continue; const candidate = path.join(root, entry.name); if (fs.existsSync(path.join(candidate, '.git'))) { const standardized = /(?:^|_)(?:pdv|forca_de_vendas_web)$/i.test(entry.name) || /forca.?de.?vendas/i.test(entry.name); found.push({ id: entry.name, name: entry.name, path: candidate, default: path.resolve(candidate).toLowerCase() === path.resolve(config.repository).toLowerCase(), standard: standardized ? { base: 'main', integration: 'pre_main' } : null }); } } } catch {}
  if (!found.some(x => x.default) && (fs.existsSync(path.join(config.repository, '.git')) || config.demo)) found.push({ id: path.basename(config.repository), name: path.basename(config.repository), path: config.repository, default: true, standard: null });
  return found.sort((a, b) => Number(b.default) - Number(a.default) || a.name.localeCompare(b.name));
}
function resolveRepository(config, value) {
  const selected = listRepositories(config).find(x => x.id === value || x.path === value);
  if (!selected) throw new AppError('Selecione um repositório Git válido dentro de C:\\git.');
  return selected;
}
function send(res, status, value) { res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' }); res.end(JSON.stringify(value)); }
async function jsonBody(req) {
  if (!String(req.headers['content-type'] || '').startsWith('application/json')) throw new AppError('Envie dados JSON.', 415);
  let buffer = '';
  for await (const part of req) { buffer += part; if (Buffer.byteLength(buffer) > 180_000) throw new AppError('Entrada muito grande.', 413); }
  try { return JSON.parse(buffer); } catch { throw new AppError('JSON inválido.'); }
}
async function multipartFiles(req) {
  const contentType = String(req.headers['content-type'] || '');
  const match = /boundary=(?:"([^"]+)"|([^;]+))/i.exec(contentType);
  if (!match) throw new AppError('Envie arquivos pelo formulário de anexos.', 415);
  const boundary = Buffer.from(`--${match[1] || match[2]}`);
  const chunks = []; let total = 0;
  for await (const part of req) { total += part.length; if (total > 52 * 1024 * 1024) throw new AppError('Os anexos ultrapassam o limite de 50 MB.', 413); chunks.push(part); }
  const body = Buffer.concat(chunks), files = [];
  let cursor = 0;
  while (cursor < body.length) {
    const start = body.indexOf(boundary, cursor); if (start < 0) break;
    const headerStart = start + boundary.length + 2; if (body.subarray(start + boundary.length, start + boundary.length + 2).toString() === '--') break;
    const headerEnd = body.indexOf(Buffer.from('\r\n\r\n'), headerStart); if (headerEnd < 0) break;
    const headers = body.subarray(headerStart, headerEnd).toString('utf8');
    const next = body.indexOf(boundary, headerEnd + 4); if (next < 0) break;
    const contentEnd = next - 2, content = body.subarray(headerEnd + 4, contentEnd);
    const disposition = /Content-Disposition:\s*form-data;[^\r\n]*name="[^"]*"[^\r\n]*filename="([^"]*)"/i.exec(headers);
    if (disposition && disposition[1]) files.push({ name: attachmentName(path.basename(disposition[1])), content });
    cursor = next;
  }
  if (!files.length) throw new AppError('Nenhum arquivo foi selecionado.', 400);
  if (files.length > 40) throw new AppError('Selecione no máximo 40 arquivos.', 400);
  return files;
}
export function createServer(config, adapters = {}) {
  const store = new Store(config);
  const token = crypto.randomBytes(32).toString('hex');
  const launch = adapters.launch || launchTerminal, focus = adapters.focus || focusTerminal, open = adapters.open || openLocal;
  const git = adapters.git || runGit;
  const server = http.createServer(async (req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('X-Frame-Options', 'DENY');
    res.setHeader('Referrer-Policy', 'no-referrer');
    res.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
    try {
      const port = server.address().port;
      const origin = `http://127.0.0.1:${port}`;
      if (req.headers.host !== `127.0.0.1:${port}`) throw new AppError('Host não permitido.', 403);
      const url = new URL(req.url, origin);
      if (req.method === 'GET' && url.pathname === '/health') return send(res, 200, { app: 'central-tickets-claude', version: '1.0.0', pid: process.pid });
      if (req.method === 'GET' && url.pathname === '/bootstrap') {
        const supplied = url.searchParams.get('token') || '';
        if (supplied.length !== token.length || !crypto.timingSafeEqual(Buffer.from(supplied), Buffer.from(token))) throw new AppError('Abra a Central pelo atalho da área de trabalho.', 401);
        res.setHeader('Set-Cookie', `central_session=${token}; HttpOnly; SameSite=Strict; Path=/`);
        res.writeHead(303, { Location: '/' }); return res.end();
      }
      const cookies = String(req.headers.cookie || '').split(';').map(x => x.trim());
      if (!cookies.includes(`central_session=${token}`)) throw new AppError('Abra a Central pelo atalho da área de trabalho para conectar esta janela.', 401);
      if (!['GET', 'POST'].includes(req.method)) throw new AppError('Método não permitido.', 405);
      if (req.method === 'POST' && (req.headers.origin !== origin || req.headers['x-central-request'] !== '1')) throw new AppError('Solicitação de outra página bloqueada.', 403);
      if (req.method === 'GET' && url.pathname === '/api/profile') {
        const checks = {
          claude: !!config.claude && fs.existsSync(config.claude), git: fs.existsSync(config.git_executable),
          repository: fs.existsSync(path.join(config.repository, '.git')), skill: fs.existsSync(path.join(config.skill, 'SKILL.md'))
        };
        let memory = false;
        if (!adapters.skipMemory) { try { const r = await fetch('http://127.0.0.1:49374/admin/status', { signal: AbortSignal.timeout(1000) }); memory = r.ok; } catch {} }
        return send(res, 200, { repository: config.repository, repositories: listRepositories(config), agents: [{ id: 'claude', name: 'Claude Code', available: !!config.claude && fs.existsSync(config.claude) }, { id: 'codex', name: 'Codex CLI', available: !!config.codex && fs.existsSync(config.codex) }], preferences: readJson(path.join(store.root, 'preferences.json'), {}), checks, memory, executionOwner: readJson(path.join(store.root, 'execution.lock'))?.id ?? null, demo: !!config.demo });
      }
      if (url.pathname === '/api/tickets') {
        if (req.method === 'GET') return send(res, 200, store.list());
        const input = await jsonBody(req), repository = resolveRepository(config, input.repository || path.basename(config.repository)), agent = String(input.agent || 'claude').toLowerCase();
        if (!['claude', 'codex'].includes(agent)) throw new AppError('Escolha Claude Code ou Codex CLI.');
        if (agent === 'claude' && !config.claude) throw new AppError('Claude Code não está instalado nesta máquina.', 409);
        if (agent === 'codex' && !config.codex) throw new AppError('Codex CLI não está instalado nesta máquina.', 409);
        const release = input.release || repository.standard?.base || 'main';
        return send(res, 201, store.create({ ...input, release, repository: repository.path, repositoryId: repository.id, integration_branch: input.integration_branch || repository.standard?.integration || null, agent }));
      }
      const match = /^\/api\/tickets\/([^/]+)(?:\/([^/]+))?$/.exec(url.pathname);
      if (match) {
        const id = ticketId(decodeURIComponent(match[1])), action = match[2];
        let record = store.get(id);
        if (req.method === 'GET' && !action) return send(res, 200, store.view(id));
        if (req.method === 'POST' && action === 'attachments') {
          const files = await multipartFiles(req);
          const dir = path.join(config.references_root, id.toLowerCase());
          if (!inside(config.references_root, dir)) throw new AppError('Pasta de anexos inválida.', 403);
          fs.mkdirSync(dir, { recursive: true });
          const written = [], skipped = [];
          for (const file of files) {
            const target = path.join(dir, file.name);
            if (!inside(dir, target)) throw new AppError('Arquivo de anexo fora da pasta permitida.', 403);
            if (fs.existsSync(target)) { skipped.push(file.name); continue; }
            fs.writeFileSync(target, file.content, { flag: 'wx', mode: 0o600 });
            written.push(file.name);
          }
          const attachments = [...new Set([...(record.attachments || []), ...written])];
          store.save({ ...record, attachments });
          store.event(id, 'anexos', `${written.length} anexo(s) copiado(s) para a pasta de referências${skipped.length ? `; ${skipped.length} já existente(s) preservado(s)` : ''}.`);
          return send(res, 201, { attachments: store.attachmentInfo(store.get(id)), message: `${written.length} anexo(s) adicionado(s)${skipped.length ? `; ${skipped.length} já existente(s) preservado(s)` : ''}.` });
        }
        if (req.method === 'GET' && action === 'content') {
          const kind = url.searchParams.get('kind');
          if (!['plan', 'input'].includes(kind)) throw new AppError('Conteúdo inválido.');
          const file = kind === 'plan' ? path.join(store.stateDir(id), 'escopo.md') : store.file(id, 'entrada.md');
          const root = kind === 'plan' ? ticketConfig(config, record).state_root : store.root;
          if (!inside(root, file)) throw new AppError('Arquivo fora da pasta do ticket.', 403);
          const text = fs.existsSync(file) ? fs.readFileSync(file, 'utf8').slice(0, 150_000) : kind === 'input' ? record.scope : null;
          return send(res, 200, { text });
        }
        if (req.method === 'GET' && action === 'diff') {
          await verifyWorktree(store, record, git);
          const status = await git(config, record.worktree, ['status', '--short']);
          const diff = await git(config, record.worktree, ['diff', '--no-ext-diff', '--no-textconv', record.baseSha, '--', '.']);
          return send(res, 200, { text: redact(`Branch: ${record.branch}\nBase: ${record.baseSha}\n\nArquivos (inclui não rastreados):\n${status || '(limpo)'}\n\nDiff dos arquivos rastreados:\n${diff || '(sem diferenças rastreadas)'}`) });
        }
        if (req.method === 'POST' && action === 'start') {
          await jsonBody(req);
          if (config.demo) throw new AppError('Simulação: a abertura de sessões reais está desativada.', 409);
          const agentExecutable = record.agent === 'codex' ? config.codex : config.claude;
          if (!agentExecutable || !fs.existsSync(agentExecutable)) throw new AppError(`${record.agent === 'codex' ? 'Codex CLI' : 'Claude Code'} não localizado. Repare a instalação antes de iniciar.`, 409);
          const attachments = store.attachmentInfo(record);
          if (attachments.missing.length) throw new AppError(`Anexos informados ausentes: ${attachments.missing.join(', ')}. Abra a pasta de anexos e confira.`, 409);
          const runtime = store.reserve(id);
          try {
            record = await prepare(store, record, git);
            writeSessionFiles(store, record);
            const documentDir = path.join(config.documents_root, id.toLowerCase());
            if (!inside(config.documents_root, documentDir)) throw new AppError('Pasta de documentos inválida.');
            fs.mkdirSync(documentDir, { recursive: true });
            await launch(config, record, runtime);
            store.event(id, 'abertura', record.sessionStarted ? 'Retomada solicitada para a conversa original.' : 'Janela do Claude solicitada para analisar este ticket.');
            return send(res, 200, { message: 'A janela do Claude está abrindo. A aprovação será feita na conversa.' });
          } catch (e) {
            atomicJson(store.file(id, 'runtime.json'), { ...runtime, phase: 'error', error: e.message, endedAt: now() });
            store.release(id, runtime.runId); store.event(id, 'erro', e.message); throw e;
          }
        }
        if (req.method === 'POST' && action === 'focus') {
          await jsonBody(req);
          if (!['running', 'uncertain'].includes(store.view(id).session)) throw new AppError('A conversa não está em execução. Use Retomar conversa.', 409);
          return send(res, 200, { message: await focus(id) ? 'Conversa aberta.' : `A sessão de ${id} está ativa. Localize a janela do Claude na barra de tarefas; nenhuma sessão duplicada foi criada.` });
        }
        if (req.method === 'POST' && action === 'open') {
          const input = await jsonBody(req); let target;
          if (input.kind === 'references') {
            target = path.join(config.references_root, id.toLowerCase());
            if (!inside(config.references_root, target)) throw new AppError('Pasta de anexos inválida.');
            fs.mkdirSync(target, { recursive: true });
          } else if (input.kind === 'worktree') {
            await verifyWorktree(store, record, git); target = record.worktree;
          } else if (input.kind === 'document') {
            const dir = path.join(config.documents_root, id.toLowerCase());
            if (!inside(config.documents_root, dir) || !fs.existsSync(dir)) throw new AppError('O documento ainda não foi gerado.', 404);
            const files = fs.readdirSync(dir).filter(x => x.toLowerCase().endsWith('.docx') && !x.startsWith('~$'));
            if (!files.length) throw new AppError('O documento ainda não foi gerado.', 404);
            // Multiple versions are opened as a folder so the app does not choose the wrong one.
            target = files.length === 1 ? path.join(dir, files[0]) : dir;
            if (!inside(dir, target)) throw new AppError('Documento fora da pasta do ticket.', 403);
          } else throw new AppError('Ação inválida.');
          await open(target); return send(res, 200, { message: 'Aberto no Windows.' });
        }
        throw new AppError('Ação não encontrada.', 404);
      }
      if (req.method === 'GET' && ['/', '/app.js', '/style.css', '/components.css', '/markdown.css', '/attachments.css', '/mark.svg'].includes(url.pathname)) {
        const file = path.join(publicRoot, url.pathname === '/' ? 'index.html' : url.pathname.slice(1));
        res.writeHead(200, { 'Content-Type': mime[path.extname(file)] }); fs.createReadStream(file).pipe(res); return;
      }
      throw new AppError('Página não encontrada.', 404);
    } catch (e) { if (!res.headersSent) send(res, e.status || 500, { error: redact(e.message || 'Erro inesperado.') }); else res.end(); }
  });
  server.requestTimeout = 180_000;
  return { server, store, token };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const appRoot = path.dirname(here);
  const configFile = process.argv[2];
  const config = configFile ? readJson(path.resolve(configFile)) : defaultConfig(appRoot);
  if (!config) throw new Error('Configuração ausente.');
  const { server, token } = createServer(config);
  const port = Number(process.env.CENTRAL_TICKETS_PORT || 17861);
  server.on('error', error => { console.error(error.message); process.exitCode = 1; });
  server.listen(port, '127.0.0.1', () => {
    atomicJson(path.join(config.dataRoot, 'config.json'), config);
    atomicJson(path.join(config.dataRoot, 'launch.json'), { pid: process.pid, port: server.address().port, token, app: 'central-tickets-claude' });
    console.log(`Central de Tickets disponível em 127.0.0.1:${server.address().port}`);
  });
  process.on('SIGTERM', () => server.close());
}
