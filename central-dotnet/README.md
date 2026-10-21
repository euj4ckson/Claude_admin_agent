# Central de Tickets em C#

Esta pasta inicia a migração da Central para ASP.NET Core/.NET 10. O servidor Node continua sendo o sistema principal durante a transição; a API C# roda isolada na porta `17862` e não altera os dados existentes.

## O que já foi migrado

- Endpoint de saúde (`GET /health`).
- Leitura dos cadastros e estados JSON já usados pela Central.
- Listagem e filtros por etapa, texto, repositório e agente (`GET /api/tickets`).
- Detalhe do ticket com estado, anexos e histórico (`GET /api/tickets/{id}`).
- Interface web responsiva com resumo, busca, filtros por situação, cartões e detalhe formatado do ticket.
- Dashboard e contagens confiáveis (`GET /api/dashboard` e `GET /api/stats`).
- Acesso local com token de bootstrap e cookie HttpOnly.

## Interface

Abra `http://127.0.0.1:17862` para ver os tickets em andamento. A tela também permite filtrar os entregues ou consultar todos, pesquisar por código/título e abrir os detalhes de cada ticket. O CSS fica em `CentralTickets.Api/wwwroot/style.css` e é servido pela própria aplicação.

Esta tela é uma etapa inicial da migração, não substitui ainda a interface Node. A versão C# permanece somente leitura: não cadastra tickets, não inicia agentes e não altera status ou arquivos de estado.

Esta etapa é deliberadamente somente leitura. Cadastro, Azure, anexos, hooks, aprovação, inicialização de agentes, métricas de tokens e sincronização de conhecimento continuam no Node até que cada fluxo tenha uma implementação C# equivalente e testes.

## Rodar localmente

Requer o SDK .NET 10. Na raiz do repositório:

```powershell
dotnet build CentralTickets.DotNet.sln
dotnet test CentralTickets.DotNet.sln
.\abrir-central-dotnet.ps1
```

O atalho inicia o processo em segundo plano e abre a interface em `http://127.0.0.1:17862`. Ele não fecha nem substitui o servidor Node (`17861`). O processo cria `launch-dotnet.json` na pasta de dados local para armazenar o token de acesso desta instância; ele é removido ao encerrar normalmente. Se reiniciar o servidor manualmente, execute o atalho novamente para autenticar a nova sessão local.

Para apontar a leitura para outra instalação, configure `CENTRAL_TICKETS_DATA_ROOT`, `CENTRAL_TICKETS_STATE_ROOT` e `CENTRAL_TICKETS_REFERENCES_ROOT`. Os cadastros, estados e anexos são lidos sem alterações; apenas o arquivo de sessão `launch-dotnet.json` é criado/removido dentro da pasta de dados.
