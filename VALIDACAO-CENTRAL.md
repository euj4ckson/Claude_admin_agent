# Validação local da Central .NET

Data da verificação: 21/10/2026.

- `dotnet build CentralTickets.DotNet.sln --configuration Release -p:OutDir=<pasta temporária>`: concluído sem avisos ou erros. A saída isolada evitou interromper a instância aberta, que mantinha o assembly de produção carregado.
- `dotnet test CentralTickets.DotNet.sln --configuration Release -p:OutDir=<pasta temporária>`: 48 testes aprovados; inclui descoberta de clones aninhados, eventos de sessão Codex, fila de AI Memory e metadados reais de qualidade.
- Build isolado de `CentralTickets.Hook` e `CentralTickets.ComputerMcp`: concluídos sem avisos ou erros.
- Inicialização isolada da API: bootstrap autenticado, `/health`, página inicial, CSS e perfil responderam corretamente em HTTP.
- Instância local recompilada e reiniciada sem janela de terminal; `/health` e a página inicial responderam com HTTP 200, e `/api/profile` listou 15 repositórios, incluindo `ss_canhoto_frontend` e `ss_canhoto_backend` aninhados sob outro clone.
- Claude Code e Codex CLI locais responderam a `--version`; `--help` confirmou os argumentos de permissões/sandbox usados pelo runner. Nenhuma sessão de desenvolvimento foi iniciada durante essa verificação.
- Os 8 scripts PowerShell distribuídos passaram pela análise sintática; a árvore Git atual não contém `server.mjs`, `package.json`, módulos `.mjs`/`.cjs` nem `node_modules`.
- O smoke test criou `config.json` e cadastros em diretório temporário; não criou ticket nem acessou Azure, e a pasta foi removida ao final.
- A rodada de paridade também testou contagem/filtro de atenção, contadores da navegação, filtros selecionados, conteúdo formatado/bruto e controles de impressão.
- Os testes cobrem importação do perfil local, preservação/idempotência de `config.json` e permissões MCP/negação de push da sessão.
- O runner Codex registra `SessionStart`/`SessionEnd` pelo hook .NET; o encerramento sincroniza o aprendizado e preserva a fila local se o AI Memory estiver indisponível.
- O hook Claude aponta para o assembly .NET; o manifesto do plugin não chama runtime Node.

Ainda é necessário validar o fluxo interativo Claude/Codex com as instalações e autenticações de cada computador. Não iniciar uma sessão de ticket real apenas como teste de instalação.
