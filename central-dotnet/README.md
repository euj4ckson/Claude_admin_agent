# Central de Tickets em C#

Implementação principal da Central em C# e ASP.NET Core 10. Ela usa o cadastro e os estados JSON existentes, sem converter os dados locais.

## Projetos

- `CentralTickets.Api`: interface local, endpoints e serviços de tickets, Azure DevOps, Git/worktrees, uso, eventos e AI Memory.
- `CentralTickets.Hook`: hook de política das sessões Claude.
- `CentralTickets.SessionRunner`: reserva, início, retomada e encerramento de sessões.
- `CentralTickets.ComputerMcp`: ferramentas MCP de captura e interação visual no Windows.
- `CentralTickets.UsageCollector`: grava snapshots de limite do Claude recebidos pela status line.
- `CentralTickets.Plugin`: manifesto do plugin Claude com hooks que chamam o executável .NET.
- `CentralTickets.Api.Tests`: testes unitários e de integração.

## Executar

Requer Windows e .NET 10 SDK. Na raiz do repositório:

```powershell
dotnet build .\CentralTickets.DotNet.sln --configuration Release
dotnet test .\central-dotnet\CentralTickets.Api.Tests\CentralTickets.Api.Tests.csproj --configuration Release
.\abrir-central-dotnet.ps1
```

O servidor é local, em `http://127.0.0.1:17861`. O atalho executa sem console visível e reaproveita os binários já compilados. Para compilar novamente, use `abrir-central-dotnet.ps1 -Recompilar`.

Em tickets Claude com conversa anterior, **Continuar em sessão nova** abre uma conversa com contexto limpo e um checkpoint curto baseado no estado salvo. Feche a conversa anterior antes de usar a opção. A worktree, o plano, os testes, as evidências e a aprovação permanecem no ticket; a nova sessão confere esses registros antes de prosseguir. Isso reduz o histórico enviado a cada interação, mas não reinicia o limite de uso da conta Claude.

Nos testes de tela, `central-computer` pode esperar o título de uma janela conhecida com `computer_wait_for_title` e executar passos determinísticos com `computer_sequence`, que confere a janela antes de cada passo. Capturas continuam salvas em resolução integral para o documento; `computer_screenshot` aceita `includeImage=false` quando o agente só precisa guardar a evidência, sem carregar a imagem na conversa.

## Configuração

A configuração fica em `%LOCALAPPDATA%\CentralTicketsClaude\config.json`. Na primeira execução, a Central preserva um `config.json` existente; se não houver, importa o perfil local `~/.claude/skills/desenvolver-ticket/perfil.json` e completa os caminhos padrão. Sem perfil, tenta localizar um clone Git em `C:\git` ou `%USERPROFILE%\git`; defina `CENTRAL_TICKETS_REPOSITORIES_ROOT` para indicar outra pasta de clones. Confira e ajuste os caminhos em `config.json` antes de cadastrar tickets. As variáveis `CENTRAL_TICKETS_DATA_ROOT`, `CENTRAL_TICKETS_STATE_ROOT`, `CENTRAL_TICKETS_REFERENCES_ROOT` e `CENTRAL_TICKETS_WORKTREES_ROOT` podem sobrescrever diretórios. A porta padrão é 17861 e pode ser alterada com `CENTRAL_TICKETS_DOTNET_PORT`.

Claude Code e Codex CLI devem estar instalados e autenticados à parte. O token do Azure pode ser configurado pela interface e fica protegido com DPAPI no perfil do Windows.

## Compatibilidade e testes

Os dados JSON existentes são preservados. A aplicação não faz migração em massa; novos registros e eventos usam os mesmos arquivos locais. Os testes incluem fixtures isoladas e chamadas simuladas ao Azure, sem depender de tickets reais.

Para o ERP, use uma cópia local exclusiva de banco. Para os ecossistemas FVA/WCF, a política documentada permite o uso controlado de `SSBDFV_PD` nos cenários cobertos pelo ticket; qualquer gravação continua restrita ao caso aprovado e registrado.
