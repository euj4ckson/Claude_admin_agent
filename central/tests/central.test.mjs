import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync, spawnSync } from 'node:child_process';
import { Store, validateTicket, atomicJson, readJson, inside, prepare, verifyWorktree, writeSessionFiles } from '../core.mjs';
import { approvalPrompt, preTool, acquireExecution, releaseExecution } from '../policy.mjs';
import { createServer } from '../server.mjs';

const appRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const input = (id = 'GER5800') => ({ticket:id,sprint:'165',release:'release/106.4.3',title:'Teste isolado',scope:'Escopo de teste fictício para validar os controles locais.',delivery:'no_commit',attachments:[]});
function fixture(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'central-tickets-test-'));
  t.after(() => { assert.ok(path.dirname(dir) === fs.realpathSync(os.tmpdir()) || path.dirname(dir) === os.tmpdir()); assert.match(path.basename(dir), /^central-tickets-test-/); fs.rmSync(dir,{recursive:true,force:true}); });
  const config = {appRoot,dataRoot:path.join(dir,'data'),state_root:path.join(dir,'state'),new_worktrees_root:path.join(dir,'worktrees'),references_root:path.join(dir,'references'),documents_root:path.join(dir,'documents'),repository:path.join(dir,'repo'),git_executable:'C:/Program Files/Git/cmd/git.exe',claude:process.execPath,node:process.execPath,skill:path.join(dir,'skill'),demo:true};
  const store = new Store(config); const ticket = store.create(input());
  return {dir,config,store,ticket};
}
function plan(store,ticket) {
  atomicJson(store.stateFile(ticket.id),{stage:'aguardando_aprovacao',delivery:{commit_allowed:ticket.delivery !== 'no_commit'}});
  fs.writeFileSync(path.join(store.stateDir(ticket.id),'escopo.md'),'Plano completo fictício com análise, arquivos, riscos e critérios de validação.');
  return approvalPrompt(store,ticket,{prompt:`APROVAR ${ticket.id}`,session_id:ticket.sessionId});
}
const decision = output => output?.hookSpecificOutput?.permissionDecision;

test('validates ticket, sprint, release, scope and attachment paths', () => {
  assert.equal(validateTicket(input('ger5800')).id,'GER5800');
  for (const change of [{ticket:'../ERP'},{sprint:'1 & calc'},{release:'release/a/../b'},{release:'release/a.lock'},{scope:'curto'},{attachments:['../secret.txt']},{attachments:['a:bad']},{delivery:'push'}]) assert.throws(() => validateTicket({...input(),...change}));
});
test('duplicate registrations and preexisting coordinator state are preserved', t => {
  const {store} = fixture(t);
  assert.throws(() => store.create(input()), /já possui/);
  atomicJson(store.stateFile('GER5801'),{stage:'implementacao'});
  assert.throws(() => store.create(input('GER5801')), /já possui/);
  assert.equal(store.list().length,1);
});
test('approval requires correct session, stage and exact phrase, and invalidates on changed plan', t => {
  const {store,ticket} = fixture(t);
  assert.equal(approvalPrompt(store,ticket,{prompt:'Exemplo APROVAR GER5800'}),null);
  assert.throws(() => approvalPrompt(store,ticket,{prompt:'APROVAR GER5800',session_id:'other'}));
  assert.throws(() => approvalPrompt(store,ticket,{prompt:'APROVAR GER5800',session_id:ticket.sessionId}));
  plan(store,ticket); assert.ok(store.currentApproval(ticket.id));
  fs.appendFileSync(path.join(store.stateDir(ticket.id),'escopo.md'),' Alteração posterior.');
  assert.equal(store.currentApproval(ticket.id),null);
});
test('session lock rejects duplicate and live orphan child, accepts dead lock', t => {
  const {store,ticket} = fixture(t), run = store.reserve(ticket.id);
  assert.throws(() => store.reserve(ticket.id), /Já existe/);
  atomicJson(store.file(ticket.id,'active.lock'),{runId:run.runId,pid:2147483000});
  atomicJson(store.file(ticket.id,'runtime.json'),{...run,pid:2147483000,childPid:process.pid});
  assert.throws(() => store.reserve(ticket.id), /Já existe/);
  assert.equal(store.view(ticket.id).session,'running');
  atomicJson(store.file(ticket.id,'runtime.json'),{...run,pid:2147483000});
  const recovered = store.reserve(ticket.id); assert.notEqual(recovered.runId,run.runId);
  store.release(ticket.id,run.runId); assert.ok(fs.existsSync(store.file(ticket.id,'active.lock')));
  store.release(ticket.id,recovered.runId); assert.ok(!fs.existsSync(store.file(ticket.id,'active.lock')));
});
test('a new Central store recovers dead session and execution locks', t => {
  const {store,ticket,config} = fixture(t);
  const stale = { runId: 'stale-run', id: ticket.id, pid: 2147483000, phase: 'running', startedAt: '2026-01-01T00:00:00.000Z', heartbeat: '2026-01-01T00:00:01.000Z', childPid: 2147483001 };
  atomicJson(store.file(ticket.id,'active.lock'), stale);
  atomicJson(store.file(ticket.id,'runtime.json'), stale);
  atomicJson(path.join(config.dataRoot,'execution.lock'), { id: ticket.id, runId: stale.runId, pid: stale.pid, at: stale.startedAt });
  const recovered = new Store(config);
  assert.equal(fs.existsSync(recovered.file(ticket.id,'active.lock')), false);
  assert.equal(fs.existsSync(path.join(config.dataRoot,'execution.lock')), false);
  assert.equal(recovered.runtime(ticket.id).phase, 'stopped');
  assert.equal(recovered.runtime(ticket.id).recovery, 'stale_lock_recovered');
  assert.ok(recovered.events(ticket.id).some(x => x.type === 'recuperacao'));
});
test('write controls and no-commit policy preserve ticket boundaries', t => {
  const {store,ticket,config,dir} = fixture(t);
  ticket.worktree = path.join(config.new_worktrees_root,'165-ger5800');
  const runtime = store.reserve(ticket.id);
  const check = (tool_name,tool_input) => decision(preTool(store,ticket,runtime,{tool_name,tool_input}));
  assert.equal(check('Write',{file_path:path.join(ticket.worktree,'code.cs')}),'deny');
  assert.equal(check('Write',{file_path:path.join(store.stateDir(ticket.id),'escopo.md')}),undefined);
  assert.equal(check('Bash',{command:'git status --short'}),'ask');
  plan(store,ticket);
  assert.equal(check('Write',{file_path:path.join(ticket.worktree,'code.cs')}),undefined);
  assert.equal(check('Write',{file_path:path.join(dir,'outside.cs')}),'deny');
  assert.equal(check('Write',{file_path:path.join(ticket.worktree,'.git','config')}),'deny');
  atomicJson(store.stateFile(ticket.id),{stage:'implementacao',delivery:{commit_allowed:false},change_budget:{planned_files:['code.cs']}});
  assert.equal(check('Write',{file_path:path.join(ticket.worktree,'other.cs')}),'deny');
  assert.equal(check('Write',{file_path:path.join(ticket.worktree,'code.cs')}),undefined);
  for (const command of ['git push origin HEAD','git -C C:/temp push','gh pr create','az repos pr create','git reset --hard','git clean -fd','git commit -m test']) assert.equal(check('Bash',{command}),'deny',command);
});
test('shared build/test resource blocks concurrent tickets and remote SQL', t => {
  const {store,ticket} = fixture(t), second = store.create(input('GER5801'));
  const firstRun = store.reserve(ticket.id), secondRun = store.reserve(second.id);
  plan(store,ticket); plan(store,second);
  atomicJson(store.stateFile(ticket.id),{stage:'testes'}); atomicJson(store.stateFile(second.id),{stage:'testes'});
  const check = (item,run,command,extra={}) => decision(preTool(store,item,run,{tool_name:'Bash',tool_input:{command,...extra}}));
  assert.equal(check(ticket,firstRun,'sqlcmd -S remote -E -Q select'),'deny');
  assert.equal(check(ticket,firstRun,'dotnet test',{run_in_background:true}),'deny');
  assert.equal(check(ticket,firstRun,'dotnet test'),undefined);
  assert.equal(check(second,secondRun,'dotnet test'),'deny');
  releaseExecution(store,secondRun); assert.equal(readJson(path.join(store.root,'execution.lock')).id,ticket.id);
  releaseExecution(store,firstRun); assert.equal(acquireExecution(store,second,secondRun),null);
});
test('attachment presence and traversal checks, corrupted state remains untouched', t => {
  const {store,ticket,config} = fixture(t);
  ticket.attachments = ['extrato.ofx']; store.save(ticket);
  assert.deepEqual(store.attachmentInfo(ticket).missing,['extrato.ofx']);
  const dir = path.join(config.references_root,ticket.id.toLowerCase()); fs.mkdirSync(dir,{recursive:true}); fs.writeFileSync(path.join(dir,'EXTRATO.OFX'),'fixture');
  assert.equal(store.attachmentInfo(ticket).missing.length,0);
  assert.equal(inside(config.dataRoot,path.join(config.dataRoot,'..','escape')),false);
  fs.mkdirSync(store.stateDir(ticket.id),{recursive:true}); fs.writeFileSync(store.stateFile(ticket.id),'broken json');
  assert.equal(store.view(ticket.id).stage,'bloqueado');
  assert.equal(fs.readFileSync(store.stateFile(ticket.id),'utf8'),'broken json');
});
test('HTTP auth, CSRF, safe static assets, draft creation and demo execution block', async t => {
  const {config} = fixture(t), app = createServer(config,{skipMemory:true});
  await new Promise(resolve => app.server.listen(0,'127.0.0.1',resolve));
  t.after(() => new Promise(resolve => app.server.close(resolve)));
  const origin = `http://127.0.0.1:${app.server.address().port}`;
  assert.equal((await fetch(`${origin}/api/tickets`)).status,401);
  assert.equal((await fetch(`${origin}/bootstrap?token=wrong`,{redirect:'manual'})).status,401);
  const boot = await fetch(`${origin}/bootstrap?token=${app.token}`,{redirect:'manual'});
  assert.equal(boot.status,303); assert.match(boot.headers.get('set-cookie'),/HttpOnly; SameSite=Strict/);
  const headers = {Cookie:`central_session=${app.token}`,'Content-Type':'application/json','X-Central-Request':'1',Origin:origin};
  const wrongHost = await new Promise((resolve,reject) => { const req = http.get(`${origin}/api/tickets`,{headers:{...headers,Host:'evil.test'}}, res => { res.resume(); resolve(res.statusCode); }); req.on('error',reject); });
  assert.equal(wrongHost,403);
  assert.equal((await fetch(`${origin}/api/tickets`,{method:'POST',headers:{...headers,Origin:'https://evil.test'},body:JSON.stringify(input('GER5801'))})).status,403);
  assert.equal((await fetch(`${origin}/api/tickets`,{method:'POST',headers,body:JSON.stringify(input('GER5801'))})).status,201);
  const upload = new FormData(); upload.append('files', new Blob(['anexo fictício'], {type:'text/plain'}), 'regra.txt');
  const uploaded = await fetch(`${origin}/api/tickets/GER5801/attachments`,{method:'POST',headers:{Cookie:`central_session=${app.token}`,Origin:origin,'X-Central-Request':'1'},body:upload});
  assert.equal(uploaded.status,201); assert.deepEqual((await uploaded.json()).attachments.missing,[]);
  assert.equal((await fetch(`${origin}/api/tickets/GER5801/start`,{method:'POST',headers,body:'{}'})).status,409);
  for (const route of ['/','/app.js','/style.css','/components.css']) assert.equal((await fetch(origin+route,{headers})).status,200);
  assert.equal((await fetch(`${origin}/api/tickets/GER5800/content?kind=../../secret`,{headers})).status,400);
  const list = await (await fetch(`${origin}/api/tickets`,{headers})).json(); assert.equal(list.length,2);
});
test('real isolated Git worktree preparation, reuse and interrupted preparation recovery', async t => {
  const {store,ticket,config,dir} = fixture(t);
  fs.mkdirSync(config.repository);
  const git = (cwd,...args) => execFileSync(config.git_executable,['-C',cwd,...args],{encoding:'utf8',windowsHide:true,stdio:['ignore','pipe','pipe']}).trim();
  git(config.repository,'init','-b','release/106.4.3');
  git(config.repository,'config','user.name','Central Fixture'); git(config.repository,'config','user.email','fixture@example.invalid');
  fs.writeFileSync(path.join(config.repository,'readme.txt'),'isolated fixture');
  git(config.repository,'add','readme.txt'); git(config.repository,'commit','-m','fixture');
  const remote = path.join(dir,'remote.git'); git(dir,'clone','--bare',config.repository,remote); git(config.repository,'remote','add','origin',remote);
  const mainHead = git(config.repository,'rev-parse','HEAD');
  const prepared = await prepare(store,ticket);
  assert.equal(prepared.branch,'feature/165/ger5800'); assert.equal(prepared.baseSha,mainHead);
  assert.equal(git(config.repository,'branch','--show-current'),'release/106.4.3'); assert.equal(git(config.repository,'status','--porcelain'),'');
  assert.equal((await prepare(store,prepared)).worktree,prepared.worktree);
  writeSessionFiles(store,prepared); assert.equal(readJson(store.file(ticket.id,'session-settings.json')).hooks,undefined);
  store.save({...ticket,preparation:{branch:prepared.branch,worktree:prepared.worktree,base:mainHead}});
  assert.equal((await prepare(store,store.get(ticket.id))).prepared,true);
  await assert.rejects(verifyWorktree(store,{...prepared,branch:'wrong'}));
  const second = store.create(input('GER5801'));
  const planned = path.join(config.new_worktrees_root,'165-ger5801'); fs.mkdirSync(planned); fs.writeFileSync(path.join(planned,'keep.txt'),'keep');
  await assert.rejects(prepare(store,second)); assert.equal(fs.readFileSync(path.join(planned,'keep.txt'),'utf8'),'keep');
});
test('hook subprocess registers session and fails closed for incorrect runtime', t => {
  const {store,ticket,config} = fixture(t), runtime = store.reserve(ticket.id);
  atomicJson(path.join(store.root,'config.json'),config);
  const env = {...process.env,CENTRAL_TICKETS_DATA:store.root,CENTRAL_TICKET_ID:ticket.id,CENTRAL_TICKET_RUN:runtime.runId};
  const run = (event,overrides={}) => spawnSync(process.execPath,[path.join(appRoot,'central/hook.mjs')],{env:{...env,...overrides},input:JSON.stringify(event),encoding:'utf8',windowsHide:true});
  const start = run({hook_event_name:'SessionStart',session_id:ticket.sessionId}); assert.equal(start.status,0,start.stderr); assert.equal(store.get(ticket.id).sessionStarted,true);
  const bad = run({hook_event_name:'PreToolUse',tool_name:'Bash',tool_input:{command:'git status'}},{CENTRAL_TICKET_RUN:'wrong'});
  assert.equal(decision(JSON.parse(bad.stdout)),'deny');
});
test('managed reconnect may rotate Claude session id without allowing another ticket', t => {
  const {store,ticket,config} = fixture(t), runtime = store.reserve(ticket.id);
  atomicJson(path.join(store.root,'config.json'),config);
  const env = {...process.env,CENTRAL_TICKETS_DATA:store.root,CENTRAL_TICKET_ID:ticket.id,CENTRAL_TICKET_RUN:runtime.runId};
  const rotated = 'b6c962ed-2427-4d4f-ba11-d0c54abdf637';
  const result = spawnSync(process.execPath,[path.join(appRoot,'central/hook.mjs')],{env,input:JSON.stringify({hook_event_name:'UserPromptSubmit',session_id:rotated,prompt:'APROVAR GER5800'}),encoding:'utf8',windowsHide:true});
  assert.equal(result.status,2);
  assert.match(result.stderr,/Central:/);
  assert.doesNotMatch(result.stderr,/sess[aã]o nao corresponde/i);
  assert.ok(store.get(ticket.id).sessionIds.includes(rotated));
});
test('HTTP launch failure preserves worktree and retry reuses session without duplicate launch', async t => {
  const {config,ticket} = fixture(t); config.demo = false;
  const common = path.join(config.repository,'.git'); fs.mkdirSync(common,{recursive:true});
  let adds = 0, launches = 0, failed = true;
  const base = 'a'.repeat(40);
  const git = async (_config,_cwd,args) => {
    if (args[0] === 'remote') return 'origin';
    if (args[0] === 'branch') return 'feature/165/ger5800';
    if (args.includes('--git-common-dir')) return common;
    if (args[0] === 'rev-parse') return base;
    if (args[0] === 'worktree') { adds++; fs.mkdirSync(args[5],{recursive:true}); }
    return '';
  };
  const app = createServer(config,{skipMemory:true,git,launch:async (_config,record) => { launches++; assert.equal(record.sessionId,ticket.sessionId); if (failed) throw new Error('falha simulada de abertura'); }});
  await new Promise(resolve => app.server.listen(0,'127.0.0.1',resolve));
  t.after(() => new Promise(resolve => app.server.close(resolve)));
  const origin = `http://127.0.0.1:${app.server.address().port}`;
  const start = () => fetch(`${origin}/api/tickets/${ticket.id}/start`,{method:'POST',headers:{Cookie:`central_session=${app.token}`,Origin:origin,'Content-Type':'application/json','X-Central-Request':'1'},body:'{}'});
  assert.equal((await start()).status,500); assert.equal(app.store.get(ticket.id).prepared,true);
  assert.equal(fs.existsSync(app.store.file(ticket.id,'active.lock')),false);
  failed = false; assert.equal((await start()).status,200);
  assert.equal((await start()).status,409); assert.equal(launches,2); assert.equal(adds,1);
});
