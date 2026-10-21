# Central de Tickets

Aplicação local para organizar o desenvolvimento de tickets em vários repositórios. O fluxo cobre cadastro/importação, análise e aprovação do escopo, worktrees isoladas, sessões de Claude Code ou Codex CLI, testes, revisão e registro da entrega.

O servidor, os hooks, o runner de sessões, a captura de uso e o servidor MCP de testes visuais são implementados em C#/.NET 10. A interface usa HTML, CSS e JavaScript sem framework. Os dados existentes continuam em JSON e são lidos sem migração em massa.

## O que oferece

- Importação de work items do Azure DevOps com prévia, download de anexos e PAT protegido pelo DPAPI do Windows.
- Cadastro de tickets para diferentes repositórios, agente, tipo de branch e forma de entrega.
- Worktrees isoladas, preparação de componentes relacionados e conferência do diff.
- Aprovação vinculada ao plano e gates de sessão, escopo, evidências, compilação e testes.
- Captura de tela e interação assistida para registrar evidências de teste visual.
- Indicadores de qualidade, revisões, expansões de escopo, bloqueios e testes pendentes.
- Leituras de uso do Claude e Codex a partir das fontes locais fornecidas pelas próprias ferramentas.
- Sincronização sanitizada de aprendizados com fila local quando o AI Memory não está disponível.

## Requisitos

- Windows 10 ou 11.
- .NET 10 SDK, Git for Windows e acesso aos repositórios configurados.
- Claude Code e/ou Codex CLI instalado e autenticado separadamente.
- Para o fluxo ERP, skill `desenvolver-ticket` instalada e SQL Server local para testes isolados.

O instalador prepara os projetos .NET e cria o atalho **Central de Tickets** na Área de Trabalho. A instalação dos agentes e a autenticação deles não são feitas pela Central.

## Instalação e execução

1. Clone ou baixe este repositório.
2. Execute `instalar-central.cmd` no Windows. Se preferir, use `instalar-central.ps1` em PowerShell.
3. Abra **Central de Tickets** pelo atalho da Área de Trabalho.

O atalho inicia a API local sem exibir uma janela de console e abre o navegador. A aplicação escuta somente em `127.0.0.1:17861`. Na primeira execução, o instalador compila a solução; nas próximas, o atalho reutiliza os binários existentes. Se `%LOCALAPPDATA%\CentralTicketsClaude\config.json` não existir, a Central cria a configuração a partir do perfil local da skill `desenvolver-ticket`, quando disponível, ou tenta encontrar um clone Git em `C:\git` / `%USERPROFILE%\git`. Use `CENTRAL_TICKETS_REPOSITORIES_ROOT` para outra pasta de clones. Confira os caminhos gerados antes de cadastrar tickets.

```powershell
dotnet build .\CentralTickets.DotNet.sln --configuration Release
dotnet test .\central-dotnet\CentralTickets.Api.Tests\CentralTickets.Api.Tests.csproj --configuration Release
```

Para abrir a Central sem iniciar o navegador, execute `abrir-central-dotnet.ps1` com `-SomenteServidor`. Use `-Recompilar` para forçar uma nova compilação.

## Uso

Na tela inicial, cadastre um ticket ou use **Trazer do Azure** para consultar e revisar um work item antes de importá-lo. O cadastro não inicia o agente automaticamente. Depois, prepare a worktree, confira os anexos e o pré-flight e abra a conversa correspondente.

O agente começa pela análise. Leia o plano e aprove-o na conversa com a frase indicada pela Central. A aprovação não autoriza alterações fora do plano. Acompanhe compilação, testes, revisão, evidências e pendências na página do ticket. A entrega continua local: a Central não faz push, merge nem cria PR.

### Branches

- ERP: informe a release de origem, como `release/106.4.3`, respeitando o padrão do repositório.
- PDV/FVA/FVW/WCF e repositórios com fluxo equivalente: `main` como base e `pre_main` como destino de homologação, conforme configuração do repositório.
- A branch usa `feature/<sprint>/<ticket>` ou `hotfix/<sprint>/<ticket>`.

## Arquivos e dados locais

- Cadastro, eventos, anexos importados e métricas: `%LOCALAPPDATA%\CentralTicketsClaude`.
- Estado dos tickets: `%USERPROFILE%\.claude\ticket-state` (ou o caminho definido no perfil local).
- Referências, documentos e worktrees seguem os caminhos do `config.json` local.

Esses diretórios podem conter dados internos de clientes. Não inclua tickets, anexos, dumps, credenciais ou estados locais neste repositório.

## Testes e segurança

Os testes automatizados estão em `central-dotnet/CentralTickets.Api.Tests`. Eles cobrem as regras da API, integração simulada com Azure, sessões, políticas, qualidade, métricas e ferramentas auxiliares.

O servidor fica restrito a loopback e usa cookie local `HttpOnly`; operações de escrita exigem origem e cabeçalho próprios. No fluxo ERP, use SQL Server local com autenticação integrada e uma cópia exclusiva `SSBD_TESTE_<TICKET>`. Testes que dependem de dados de cliente devem ser marcados como bloqueados quando o cenário isolado não estiver disponível.

As evidências de build/teste devem registrar comando, ambiente, esperado, obtido e referência verificável. Teste visual exige screenshot real salvo no manifesto e no documento final; a Central não aceita resultado inventado nem considera encerramento manual como validação técnica.

## Visão da solução

- `central-dotnet/CentralTickets.Api`: servidor ASP.NET Core, páginas, Azure DevOps, worktrees, métricas e AI Memory.
- `central-dotnet/CentralTickets.Hook`: políticas e eventos das sessões Claude.
- `central-dotnet/CentralTickets.SessionRunner`: ciclo de vida das sessões Claude/Codex.
- `central-dotnet/CentralTickets.ComputerMcp`: captura de tela e ações de teste visual no Windows.
- `central-dotnet/CentralTickets.UsageCollector`: coletor local de limites do Claude.
- `central-dotnet/CentralTickets.Api.Tests`: testes automatizados.
- `central-dotnet/CentralTickets.Plugin`: manifesto estático do plugin de hooks .NET.

## Portfólio

O projeto também aparece no portfólio pessoal em [MyPort](https://github.com/euj4ckson/MyPort). A descrição e as tecnologias listadas devem refletir esta implementação .NET, sem mencionar a antiga versão Node como stack ativa.

## Licença

Projeto criado para uso interno da equipe. Antes de redistribuí-lo, confirme as políticas da empresa e das ferramentas e repositórios acessados.
