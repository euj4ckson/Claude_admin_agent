# Central de Tickets

A Central local gerencia o desenvolvimento de tickets com Claude Code ou Codex CLI em repositórios Git. O servidor e as ferramentas de sessão são C#/.NET 10; a interface web usa HTML, CSS e JavaScript.

## Começar

1. Execute `instalar-central.cmd` ou `instalar-central.ps1` para compilar a solução e criar o atalho.
2. Abra **Central de Tickets** na Área de Trabalho.
3. Cadastre um ticket ou consulte/importa um work item do Azure DevOps.
4. Confira o repositório, a branch, os anexos e o escopo antes de iniciar a conversa.
5. Leia o plano do agente e aprove-o na conversa antes de qualquer alteração no código.

O servidor local fica em `http://127.0.0.1:17861`. O atalho executa o serviço sem janela de console. Claude Code, Codex CLI, Git e a skill necessária para o repositório são instalados e autenticados separadamente.

## Desenvolvimento

```powershell
dotnet build .\CentralTickets.DotNet.sln --configuration Release
dotnet test .\central-dotnet\CentralTickets.Api.Tests\CentralTickets.Api.Tests.csproj --configuration Release
```

Para iniciar sem abrir o navegador: `abrir-central-dotnet.ps1 -SomenteServidor`. Para forçar recompilação: `abrir-central-dotnet.ps1 -Recompilar`.

Consulte o [README](README.md) para instalação, dados locais, branches, Azure, testes e segurança.
