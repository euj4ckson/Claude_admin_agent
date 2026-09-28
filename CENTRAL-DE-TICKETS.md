# Central de Tickets — Claude Code e Codex CLI

Aplicação local que organiza a entrada e a retomada dos tickets. No cadastro, escolha qualquer repositório Git direto em `C:\git` e selecione Claude Code ou Codex CLI. Cada ticket recebe worktree, branch e estado próprios.

## Uso

1. Abra **Central de Tickets Claude** na área de trabalho.
2. Clique em **Novo ticket** e informe ticket, sprint, origem (`release/...`, `main` ou `pre_main`), repositório, branch (`feature`/`hotfix`), agente e entrega.
3. Se houver anexos, liste seus nomes e use **Anexos** para abrir a pasta e copiar os arquivos. O início é bloqueado se um anexo listado estiver ausente.
4. Confira os dados e clique em **Iniciar análise**. A Central atualiza somente a referência de origem e prepara uma worktree/branch exclusiva. Não troca o checkout principal.
5. Uma janela interativa do agente escolhido é aberta para o ticket. O ERP usa `/desenvolver-ticket`; os demais repositórios recebem o contexto genérico seguro da Central.
6. Quando concordar, digite **APROVAR GER5800** na conversa, substituindo pelo ticket correto. A aprovação é vinculada ao conteúdo do plano; uma alteração nele exige nova aprovação.
7. Acompanhe o estado, abra o diff e confira o documento final. Se fechar a conversa, **Retomar conversa** usa o mesmo identificador de sessão. **Abrir conversa** tenta trazer a janela já existente sem duplicá-la.

Cadastrar não inicia o Claude. Fechar o navegador não encerra as conversas. Encerrar um processo não marca o ticket como entregue. O painel informa o estado escrito pelo coordenador, que deve ser conferido pelas evidências.

## Instalação e execução

Execute `instalar-central.cmd` nesta pasta. O instalador valida/instala Node.js 22+, npm e as dependências do projeto. Para tickets ERP, mantenha Git, Claude Code e a skill `desenvolver-ticket` configurados; para outros repositórios, instale o agente escolhido (Claude Code ou Codex CLI). Os launchers anteriores continuam intactos.

- Aplicação: `http://127.0.0.1:17861` (abra pelo atalho para autenticar o navegador).
- Configuração inicial: perfil da skill em `%USERPROFILE%\.claude\skills\desenvolver-ticket\perfil.json`.
- Cadastros, aprovações, eventos e logs: `%LOCALAPPDATA%\CentralTicketsClaude`.
- Repositórios: diretórios Git diretamente dentro de `C:\git` aparecem automaticamente no cadastro. O ERP preserva suas raízes atuais; os demais usam subpastas próprias de estado e worktree.
- Estado do coordenador: caminho `state_root` do perfil; um diretório por ticket e repositório.
- Código e atalhos de sessão: este diretório. Não mova a pasta sem executar novamente o instalador.
- Testes: `npm test`. Servidor sem navegador: `powershell -NoProfile -ExecutionPolicy Bypass -File .\abrir-central.ps1 -SomenteServidor`.

Não existe instalação de serviço, abertura de firewall ou inicialização automática no Windows. O servidor fica oculto após abrir o atalho e volta a ser iniciado quando necessário. Nenhum ticket empresarial foi iniciado pelos testes automatizados.

## Controles e limites

- Servidor vinculado exclusivamente ao loopback; cookie HttpOnly/SameSite e validação de origem para ações. Não publique essa porta na rede.
- Um lock por ticket, incluindo a verificação do processo filho do Claude. Preparações interrompidas só são recuperadas após verificar repositório, branch e commit.
- Plugin de hooks carregado apenas nas sessões da Central. Não substitui os hooks globais do AI Memory. O painel consulta somente a saúde do AI Memory; quem registra/consulta o contexto são as integrações já instaladas no Claude.
- Permissões interativas mantidas. Escritas pelas ferramentas conhecidas são limitadas à worktree e pastas do ticket; implementação depende da aprovação do plano.
- Depois da aprovação, se o coordenador registrar `change_budget.planned_files`, o hook também recusa edição de arquivo fora dessa lista. O script `verificar-diff-minimo.ps1` compara arquivos/linhas contra a base antes da revisão. Esses controles são uma trava adicional; o usuário ainda deve conferir o diff.
- Comandos diretos conhecidos de push/PR e Git destrutivo são bloqueados. A configuração “sem commit” é preservada. Nenhum push, PR, publicação ou mensagem externa é feito pela aplicação. No PDV/FVW, a origem é `main` e o destino de homologação documentado é `pre_main`; o PR continua sendo uma etapa manual.
- Compilação/testes possuem reserva compartilhada entre sessões gerenciadas. O coordenador deve registrar `compilacao` ou `testes` antes de executar comandos síncronos. Se ocupado, informa o dono e aguarda pedido para continuar; **não há fila automática**. Outras sessões abertas fora da Central não participam dessa reserva.
- Os hooks **não são um sandbox de sistema operacional**. Scripts indiretos, aliases e ferramentas não reconhecidas não podem ser classificados integralmente. Revise permissões do terminal, não use bypass de permissões e não trate os controles como barreira contra código malicioso.
- Evidências de build/teste/revisão são registros do coordenador, não uma certificação automática de sucesso. A UI e o diff não substituem revisão e validação manual.
- Dados do ticket e histórico são locais, mas o Claude continua enviando o contexto à Anthropic conforme a configuração/contrato da empresa. Não cole segredos. Proteja o perfil Windows; outro processo do mesmo usuário pode acessar os dados locais.

## Recuperação e manutenção

- Erros preservam arquivos, branches e worktrees; não há rollback destrutivo, remoção de banco ou limpeza automática pelo painel.
- Se a sessão estiver ativa, use a janela existente. Se fechar, retome pelo painel. Em caso de processo órfão, confira no Gerenciador de Tarefas antes de encerrar qualquer processo.
- A versão inicial não importa automaticamente sessões antigas, não edita tickets depois do cadastro e não exclui tickets. Confira ticket/sprint/release antes de cadastrar. Trabalho preexistente com o mesmo identificador é recusado para evitar sobreposição.
- Se a porta estiver ocupada por outro processo, a Central não o encerra. Logs ficam em `server-error.log`.
- Para backup, copie a pasta de dados e a pasta de estado com sessões fechadas. Worktrees e documentos precisam de backup separado. Não publique esses dados num repositório de código.
- Para desinstalar a interface, remova somente seu atalho. Os dados, documentos, worktrees e o Claude permanecem preservados.

## Arquitetura

`atalho → abrir-central.ps1 → servidor Node local → navegador → runner por ticket → Claude Code/Codex CLI interativo + contexto do repositório`

O frontend usa HTML/CSS/JavaScript, o backend apenas módulos nativos do Node. O projeto é versionado em `https://github.com/euj4ckson/Claude_admin_agent`.
