# Central de Tickets em C#

Esta pasta inicia a migração da Central para ASP.NET Core/.NET 10. O servidor Node continua sendo o sistema principal durante a transição; a API C# roda isolada na porta `17862` e não altera os dados existentes.

## O que já foi migrado

- Endpoint de saúde (`GET /health`).
- Leitura dos cadastros e estados JSON já usados pela Central.
- Listagem e filtros por etapa, texto, repositório e agente (`GET /api/tickets`).
- Detalhe do ticket com estado, anexos e histórico (`GET /api/tickets/{id}`).
- Dashboard e contagens confiáveis (`GET /api/dashboard` e `GET /api/stats`).
- Acesso local com token de bootstrap e cookie HttpOnly.

Esta etapa é deliberadamente somente leitura. Cadastro, Azure, anexos, hooks, aprovação, inicialização de agentes, métricas de tokens e sincronização de conhecimento continuam no Node até que cada fluxo tenha uma implementação C# equivalente e testes.

## Rodar localmente

Requer o SDK .NET 10. Na raiz do repositório:

```powershell
dotnet build CentralTickets.DotNet.sln
dotnet test CentralTickets.DotNet.sln
.\abrir-central-dotnet.ps1
```

O atalho inicia o processo em segundo plano e abre a página da API em `http://127.0.0.1:17862`. Ele não fecha nem substitui o servidor Node (`17861`). O processo cria `launch-dotnet.json` na pasta de dados local para armazenar o token de acesso desta instância; ele é removido ao encerrar normalmente.

Para apontar a leitura para outra instalação, configure `CENTRAL_TICKETS_DATA_ROOT`, `CENTRAL_TICKETS_STATE_ROOT` e `CENTRAL_TICKETS_REFERENCES_ROOT`. Os cadastros, estados e anexos são lidos sem alterações; apenas o arquivo de sessão `launch-dotnet.json` é criado/removido dentro da pasta de dados.
