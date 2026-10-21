# Cutover da Central para C#/.NET

Em 21/10/2026, a aplicação principal passou a ser a implementação C#/.NET 10. A interface permanece local e usa os dados JSON existentes; não há conversão em massa nem mudança de formato dos cadastros.

## Componentes ativos

| Projeto | Responsabilidade |
|---|---|
| `CentralTickets.Api` | API local, páginas, importação Azure, tickets, worktrees, métricas, AI Memory e eventos |
| `CentralTickets.Hook` | Eventos e políticas das sessões Claude |
| `CentralTickets.SessionRunner` | Reserva, inicialização, heartbeat e encerramento das sessões Claude/Codex |
| `CentralTickets.ComputerMcp` | Captura de tela e interação para evidências visuais no Windows |
| `CentralTickets.UsageCollector` | Registro dos limites do Claude fornecidos pela status line |
| `CentralTickets.Plugin` | Manifesto do plugin Claude, chamando o hook compilado em .NET |
| `CentralTickets.Api.Tests` | Testes de domínio e integração |

## Verificações feitas

- Todos os seis projetos estão na solução `CentralTickets.DotNet.sln`.
- Build Release da solução sem warnings/erros.
- Suíte automatizada com 44 testes aprovados.
- Smoke test isolado de inicialização, bootstrap, home, CSS, perfil e listagem sem dados.
- Interface migrada com filtros por repositório/agente, fila de atenção, contadores de sessões e barra lateral recolhível.
- Planos e documentos Markdown têm visualização formatada, texto bruto e impressão; o renderizador usa nós de texto para não executar conteúdo do ticket.
- Configuração de primeira execução importa o perfil local sem sobrescrever dados e permite informar a raiz dos clones por `CENTRAL_TICKETS_REPOSITORIES_ROOT`.
- Geração de `session-settings.json` preserva o acesso de computer-use sem retirar a negação de publicação.
- Launcher principal usa a porta 17861 e só compila quando os binários faltam ou quando solicitado.
- O instalador compila também o SessionRunner, necessário para iniciar e retomar agentes.
- Os hooks do plugin C# não dependem do runtime Node.

## Operação e limites

Use `instalar-central.cmd` para preparar o .NET e criar o atalho. A instância local escuta apenas em `127.0.0.1:17861`. O bootstrap cria cookie local `HttpOnly`; requisições de escrita também validam origem e cabeçalho.

Claude Code e Codex CLI são ferramentas externas, instaladas e autenticadas separadamente. O uso interativo deve ser validado no computador de destino. A Central não faz push, merge ou PR. Mantenha os dados existentes e as worktrees; não apague arquivos de usuário durante instalação ou atualização.

## Dados legados

Cadastros, estados, anexos, métricas e documentos continuam nos diretórios locais já configurados. O cutover não os reescreve. Os módulos Node usados pela antiga aplicação foram removidos; referências a npm/Node nas integrações referem-se apenas aos caminhos pelos quais os CLIs externos podem ter sido instalados, não à stack da Central.
