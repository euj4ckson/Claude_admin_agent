# Validação local da Central .NET

Data da verificação: 21/10/2026.

- `dotnet build CentralTickets.DotNet.sln --configuration Release -p:OutDir=<pasta temporária>`: concluído sem avisos ou erros. A saída isolada evitou interromper a instância aberta, que mantinha o assembly de produção carregado.
- `dotnet test central-dotnet/CentralTickets.Api.Tests/CentralTickets.Api.Tests.csproj --configuration Release -p:OutDir=<pasta temporária>`: 44 testes aprovados.
- Inicialização isolada da API: bootstrap autenticado, `/health`, página inicial, CSS, perfil e listagem vazia responderam corretamente em HTTP.
- O smoke test criou `config.json` e cadastros em diretório temporário; não criou ticket nem acessou Azure, e a pasta foi removida ao final.
- A rodada de paridade também testou contagem/filtro de atenção, contadores da navegação, filtros selecionados, conteúdo formatado/bruto e controles de impressão.
- Os testes cobrem importação do perfil local, preservação/idempotência de `config.json` e permissões MCP/negação de push da sessão.
- O hook Claude aponta para o assembly .NET; o manifesto do plugin não chama runtime Node.

Ainda é necessário validar o fluxo interativo Claude/Codex com as instalações e autenticações de cada computador após o cutover. Não iniciar uma sessão de ticket real apenas como teste de instalação.
