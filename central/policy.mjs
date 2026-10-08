import fs from 'node:fs';
import path from 'node:path';
import { inside, readJson, atomicJson, alive, hash, now, AppError, qualityReport } from './core.mjs';

export const criticalStage = stage => ['compilacao', 'testes'].includes(stage);
const deny = reason => ({ hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'deny', permissionDecisionReason: reason } });
const ask = reason => ({ hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'ask', permissionDecisionReason: reason } });
const allow = reason => ({ hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'allow', permissionDecisionReason: reason } });

export function approvalPrompt(store, ticket, input) {
  // Only the exact user confirmation counts, not a quoted example inside a PO description.
  const prompt = String(input.prompt ?? '').trim();
  if (prompt.toUpperCase() !== `APROVAR ${ticket.id}`) return null;
  const sessions = new Set([ticket.sessionId, ...(Array.isArray(ticket.sessionIds) ? ticket.sessionIds : [])].filter(Boolean));
  if (!sessions.has(input.session_id)) throw new AppError('A aprovação pertence a outra sessão. Abra a conversa deste ticket.');
  const state = store.state(ticket.id);
  const file = path.join(store.stateDir(ticket.id), 'escopo.md');
  if (state?.stage !== 'aguardando_aprovacao' || !fs.existsSync(file)) throw new AppError('A análise precisa estar concluída e o plano salvo antes da aprovação.');
  const scope = fs.readFileSync(file);
  if (scope.length < 40) throw new AppError('O plano salvo está incompleto.');
  const approval = { at: now(), ticket: ticket.id, sessionId: input.session_id, scopeHash: hash(scope), prompt };
  atomicJson(store.file(ticket.id, 'approval.json'), approval);
  store.event(ticket.id, 'aprovacao', 'Implementação aprovada na conversa, vinculada ao conteúdo atual do plano.');
  return { hookSpecificOutput: { hookEventName: 'UserPromptSubmit', additionalContext: `Aprovacao humana registrada para ${ticket.id}, plano SHA256 ${approval.scopeHash}. Registre o OK no estado e prossiga dentro do escopo aprovado. A entrega continua sem push.` } };
}

export function acquireExecution(store, ticket, runtime) {
  const file = path.join(store.root, 'execution.lock');
  let owner = readJson(file);
  if (owner?.runId === runtime.runId) return null;
  if (owner && !alive(owner.pid) && !alive(store.runtime(owner.id)?.childPid)) { fs.unlinkSync(file); owner = null; }
  if (owner) return owner.id;
  try {
    fs.writeFileSync(file, JSON.stringify({ id: ticket.id, runId: runtime.runId, pid: runtime.pid, at: now() }), { flag: 'wx', mode: 0o600 });
    store.event(ticket.id, 'recurso', 'Recurso compartilhado de compilação/testes reservado para este ticket.');
    return null;
  } catch (e) { if (e.code === 'EEXIST') return readJson(file)?.id ?? 'outro ticket'; throw e; }
}
export function releaseExecution(store, runtime) {
  const file = path.join(store.root, 'execution.lock');
  if (readJson(file)?.runId === runtime.runId) fs.unlinkSync(file);
}

export function preTool(store, ticket, runtime, input) {
  const tool = input.tool_name || '', args = input.tool_input || {};
  const approved = store.currentApproval(ticket.id);
  const state = store.state(ticket.id);
  if (['Write', 'Edit', 'NotebookEdit'].includes(tool)) {
    const target = args.file_path || args.notebook_path;
    if (!target || !path.isAbsolute(target)) return deny('A gravação precisa informar um caminho absoluto.');
    if (inside(store.stateDir(ticket.id), target)) return {};
    const worktree = ticket.worktree;
    const documentRoot = path.join(store.config.documents_root, ticket.id.toLowerCase());
    const withinWorktree = worktree && inside(worktree, target);
    if (!withinWorktree && !inside(documentRoot, target)) return deny('Este arquivo está fora da worktree e das pastas de entrega deste ticket.');
    if (withinWorktree && /(?:^|[\\/])\.git(?:[\\/]|$)|[\\/]\.claude[\\/]settings[^\\/]*\.json$/i.test(target)) return deny('Não altere os controles Git ou as permissões da sessão por edição de arquivo.');
    if (!approved) return deny(`O plano ainda não tem aprovação válida. Aguarde o usuário enviar APROVAR ${ticket.id} na conversa após ler a análise.`);
    const planned = Array.isArray(state?.change_budget?.planned_files) ? state.change_budget.planned_files.map(x => String(x).replaceAll('\\', '/').replace(/^\.\//, '')) : [];
    if (withinWorktree && planned.length) {
      const relative = path.relative(worktree, target).replaceAll('\\', '/');
      if (!planned.includes(relative)) return deny(`Arquivo fora do patch mínimo aprovado (${relative}). Pare e solicite nova aprovação antes de ampliar o escopo.`);
    }
    return {};
  }
  if (tool !== 'Bash' && tool !== 'PowerShell') return {};
  const command = String(args.command ?? '');
  if (/\bgit(?:\.exe)?\b[\s\S]*\bpush\b/i.test(command) || /\b(?:gh\s+pr\s+(?:create|merge)|az\s+repos\s+pr\s+(?:create|update))\b/i.test(command)) return deny('Publicação não faz parte desta sessão. A entrega da Central é local, sem push ou PR.');
  if (/\bgit(?:\.exe)?\b[\s\S]*\b(?:reset|clean)\b/i.test(command) && /(?:--hard|\s-[a-z]*[fd])/i.test(command)) return deny('Comando Git destrutivo bloqueado nesta sessão. Preserve alterações e worktrees.');
  if (/\bgit(?:\.exe)?\b[\s\S]*\bcommit\b/i.test(command)) {
    if (ticket.delivery === 'no_commit' || state?.delivery?.commit_allowed === false) return deny('Este ticket está configurado sem commit.');
    if (!approved) return deny('Não há aprovação válida do plano para commitar.');
    if (!Array.isArray(state?.build) || !state.build.length || !Array.isArray(state?.tests) || !state.tests.length || !state.review) return deny('Registre compilação, testes e revisão no estado antes do commit. Registros são evidências a conferir, não certificação automática.');
    if (!['entrega', 'aguardando_validacao_manual'].includes(state.stage)) return deny('Conclua a etapa de revisão e registre as pendências antes de commitar.');
  }
    const quality = /\bgit(?:\.exe)?\b[\s\S]*\bcommit\b/i.test(command) ? qualityReport(store, ticket, state) : { commitReady: true, structural: [] };
    if (!quality.commitReady) return deny(`Gate de qualidade bloqueou o commit: ${quality.structural.slice(0, 4).join('; ')}. Registre evidências concretas antes de commitar.`);
  const sharedCommand = /\b(?:msbuild(?:\.exe)?|sqlcmd(?:\.exe)?|vstest(?:\.console)?(?:\.exe)?|dotnet\s+(?:build|test))\b/i.test(command);
  if (sharedCommand || criticalStage(state?.stage)) {
    if (!approved) return deny('Compilação e testes aguardam aprovação do plano.');
    if (args.run_in_background) return deny('Execute compilação e testes de forma síncrona para manter a reserva compartilhada até a conclusão.');
    if (!criticalStage(state?.stage)) return deny('Antes de compilar ou testar, registre stage=compilacao ou stage=testes no estado do ticket.');
    if (/\bsqlcmd(?:\.exe)?\b/i.test(command)) {
      const targets = [...command.matchAll(/(?:^|\s)-S\s*["']?([^\s"';|]+)/g)].map(m => m[1]);
      if (!targets.length || targets.some(x => x.toLowerCase() !== 'localhost') || !/(?:^|\s)-E(?:\s|$)/.test(command)) return deny('SQLCMD deve usar explicitamente -S localhost -E.');
    }
    const owner = acquireExecution(store, ticket, runtime);
    if (owner) return deny(`O recurso de compilação/testes está reservado por ${owner}. Informe a espera e encerre o turno. Retome quando o recurso estiver livre; não execute em paralelo nem contorne a reserva.`);
    const safeBuild = /(?:MSBuild(?:\.exe)?|msbuild)\b[\s\S]*\/t\s*:\s*Build\b/i.test(command) && !/(?:\/t\s*:\s*(?:Clean|Rebuild|Restore)|-t\s*(?:Clean|Rebuild|Restore)|restore|clean|rebuild)/i.test(command);
    if (safeBuild && approved && criticalStage(state?.stage)) return allow('Build MSBuild aprovado automaticamente na etapa de compilação; nenhuma confirmação adicional é necessária.');
  }
  if (!approved) return ask('Fase de análise: autorize apenas consultas necessárias. Alteração de código depende da aprovação do plano.');
  return {};
}
