# Migração da Central para C#/.NET

Objetivo: substituir a Central Node por uma aplicação ASP.NET Core com paridade funcional, preservando os formatos de dados e os controles de segurança. Até todos os itens abaixo serem concluídos e validados, Node continua sendo a aplicação operacional; a tela C# não deve ser apresentada como substituta.

## Matriz de paridade

| Área | Fluxo existente | Situação C# | Critério para considerar migrado |
|---|---|---|---|
| Inicialização e configuração | perfil local, caminhos, executáveis, repositórios | Parcial | localizar perfil, validar diretórios e mostrar diagnóstico equivalente |
| Tickets | listar, filtrar, criar, editar cadastro, concluir manualmente | Listagem/detalhe parciais | operações compatíveis, gravação atômica e testes de conflito/recuperação |
| Azure DevOps | PAT protegido por DPAPI, consulta, prévia, anexos e importação | Ausente | preview sem escrita, PAT protegido, importação confirmada e limites de anexos testados |
| Worktrees e Git | preparar/reutilizar worktree, branches, componentes, diff | Ausente | diário de preparação recuperável, validação de caminhos e nenhuma sobrescrita |
| Sessões de agentes | iniciar, retomar, anexar, focar, estado/heartbeat e locks | Ausente | Claude e Codex operam sem duplicar sessão e locks órfãos são recuperados com segurança |
| Política e aprovação | hooks, hash do plano/orçamento, arquivos permitidos, comandos e recursos | Ausente | mesma decisão de allow/deny para casos aprovados, negados e concorrentes |
| Evidências e entrega | matriz, manifesto, gates de commit/entrega, conclusão manual | Ausente/parcial | qualidade é calculada a partir dos arquivos reais; nunca confiar só no estado declarado |
| Anexos e documentos | upload, abrir referências/worktree/documento, conteúdo do escopo | Parcial | limites, contenção de caminhos, escaping e UX equivalente |
| Métricas e uso | tokens, fases, revisões, bloqueios, Claude/Codex, SSE | Ausente | origem e precisão preservadas; atualização e reconexão testadas |
| AI Memory | geração sanitizada, sincronização e outbox/retry | Ausente | idempotência, falha remota e fila local cobertas por testes |
| Interface | navegação, ações, formulários, detalhes, filtros, gráficos, estados vazios | Somente leitura | todo fluxo executável na interface C#, sem depender do servidor Node |
| Instalação e operação | atalhos Windows, execução oculta, logs, shutdown e atualização | Parcial | instalação limpa em Windows e execução sem janela de terminal validadas |

## Ordem de migração

1. Paridade de domínio e operações locais de ticket, ainda sem iniciar agentes.
2. Azure, configuração e anexos, com segredos protegidos pelo Windows.
3. Preparação de worktrees, lançamento/retomada de agentes e recuperação de locks.
4. Portar a política dos hooks e os gates de qualidade; manter adaptadores só onde a ferramenta externa exigir Node.
5. Métricas, limites de uso, eventos em tempo real e AI Memory.
6. Interface completa, testes integrados e testes de regressão contra fixtures do formato Node.
7. Cutover: executar a suíte completa, documentar contingência, migrar o atalho padrão para C# e só então retirar o servidor Node do fluxo normal.

## Regra de cutover

Não migrar nem reescrever dados dos tickets como parte do cutover. As duas versões devem ler os mesmos JSONs durante a transição. Nunca executar os dois servidores como donos de um mesmo fluxo de escrita ou iniciar duas sessões para o mesmo ticket. O Node só deixa de ser a Central padrão quando todos os critérios da matriz estiverem implementados e aprovados por testes de integração e regressão.
