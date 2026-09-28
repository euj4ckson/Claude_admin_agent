// Isolated visual QA. Never launches Claude or prepares ERP worktrees.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Store, atomicJson, now } from './core.mjs';
import { createServer } from './server.mjs';
const appRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const root = fs.mkdtempSync(path.join(os.tmpdir(),'central-tickets-qa-'));
const config = {appRoot,dataRoot:path.join(root,'data'),state_root:path.join(root,'state'),references_root:path.join(root,'references'),documents_root:path.join(root,'documents'),new_worktrees_root:path.join(root,'worktrees'),repository:path.join(root,'erp-ficticio'),skill:path.join(root,'skill'),git_executable:process.execPath,claude:process.execPath,node:process.execPath,demo:true};
fs.mkdirSync(path.join(config.repository,'.git'),{recursive:true});
fs.mkdirSync(config.skill,{recursive:true}); fs.writeFileSync(path.join(config.skill,'SKILL.md'),'Somente demonstração.');
const store = new Store(config);
for (const item of [
  {id:'GER5800',title:'Conciliação bancária com importação OFX',stage:'aguardando_aprovacao'},
  {id:'GER5801',title:'Ajuste no relatório de movimentações',stage:'analise'},
  {id:'GER5792',title:'Validação de campos no cadastro de clientes',stage:'concluido'}
]) {
  const ticket = store.create({ticket:item.id,title:item.title,sprint:'165',release:'release/106.4.3',scope:'Escopo fictício para demonstração visual. Nenhum ticket real será executado.',delivery:'no_commit'});
  atomicJson(store.stateFile(ticket.id),{stage:item.stage,blockers:[],updated_at:now()});
  fs.writeFileSync(path.join(store.stateDir(ticket.id),'escopo.md'),'# Plano de demonstração\n\nConferir importação OFX, regras de conciliação e cenários de validação.\n\nEste conteúdo é fictício; a execução está desativada.');
}
const {server,token} = createServer(config,{skipMemory:true});
server.listen(17862,'127.0.0.1',() => {
  atomicJson(path.join(appRoot,'central/.qa-launch.json'),{pid:process.pid,url:`http://127.0.0.1:17862/bootstrap?token=${token}`,root});
  console.log('Demonstração isolada em 127.0.0.1:17862');
});
