# Validação da Central

Data: 24/09/2026.

- 11 testes automatizados passaram (`node --test central/tests/*.test.mjs`).
- Sintaxe dos scripts PowerShell de instalação, abertura do painel e abertura da sessão validada pelo parser do Windows PowerShell.
- Plugin local validado por `claude plugin validate central` (aviso apenas de autoria opcional).
- Teste Git criou repositório e remoto fictícios, preparou/reutilizou worktree, recuperou preparação interrompida e confirmou preservação do checkout principal.
- Testes cobriram duplicidade, processo filho órfão, aprovação vinculada ao plano e à sessão, invalidação por mudança, restrições de escrita, sem commit/push, reserva de build/teste, SQL remoto, anexos, estado inválido e autenticação/origem HTTP.
- Falha simulada de abertura preservou a worktree; nova tentativa reutilizou a sessão e recusou abertura duplicada.
- Interface exercitada no navegador com dados isolados: selecionar ticket, ler plano, cadastrar, escolher sem commit e confirmar que a demonstração bloqueia execução real.
- Painel real aberto com zero tickets cadastrados; atalho da área de trabalho instalado. Checagens de Claude, Git, repositório e skill aprovadas. AI Memory respondeu à consulta de saúde.

## Limites desta validação

Não foi iniciado desenvolvimento empresarial nem enviada solicitação ao modelo Claude. A execução completa de um ticket, a interação de permissões da primeira sessão, os builds/testes do ERP e a entrega DOCX ainda dependem de um primeiro uso acompanhado.

A captura de screenshot do navegador estava indisponível; a validação de interface foi funcional pelo conteúdo e controles exibidos, não uma revisão visual por imagem.

Os testes não tornam hooks um sandbox. Consulte os limites e instruções de recuperação em `CENTRAL-DE-TICKETS.md`.
