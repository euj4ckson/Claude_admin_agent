# Validação local da Central .NET

Data da verificação: 21/10/2026.

- `dotnet build CentralTickets.DotNet.sln --configuration Release -p:OutDir=<pasta temporária>`: concluído sem avisos ou erros. A saída isolada evitou interromper a instância aberta, que mantinha o assembly de produção carregado.
- `dotnet test CentralTickets.DotNet.sln --configuration Release -p:OutDir=<pasta temporária>`: 48 testes aprovados; inclui descoberta de clones aninhados, eventos de sessão Codex, fila de AI Memory e metadados reais de qualidade.
- Build isolado de `CentralTickets.Hook` e `CentralTickets.ComputerMcp`: concluídos sem avisos ou erros.
- Inicialização isolada da API: bootstrap autenticado, `/health`, página inicial, CSS, perfil e listagem vazia responderam corretamente em HTTP.
- O smoke test criou `config.json` e cadastros em diretório temporário; não criou ticket nem acessou Azure, e a pasta foi removida ao final.
- A rodada de paridade também testou contagem/filtro de atenção, contadores da navegação, filtros selecionados, conteúdo formatado/bruto e controles de impressão.
- Os testes cobrem importação do perfil local, preservação/idempotência de `config.json` e permissões MCP/negação de push da sessão.
- O runner Codex registra `SessionStart`/`SessionEnd` pelo hook .NET; o encerramento sincroniza o aprendizado e preserva a fila local se o AI Memory estiver indisponível.
- O hook Claude aponta para o assembly .NET; o manifesto do plugin não chama runtime Node.

O servidor de produção permaneceu sem reinicialização porque havia uma sessão CMX0223 ativa. O código e os testes estão atualizados, mas as alterações de descoberta de repositórios e ciclo de sessão Codex entram em vigor quando a Central for reiniciada após o encerramento dessa sessão. Ainda é necessário validar o fluxo interativo Claude/Codex com as instalações e autenticações de cada computador; não iniciar uma sessão de ticket real apenas como teste de instalação.
