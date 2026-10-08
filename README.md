# Central de Tickets — Claude Code e Codex CLI

Central local para organizar desenvolvimentos assistidos por IA em múltiplos repositórios Git. Ela cadastra o escopo do PO, cria uma worktree isolada por ticket, acompanha o plano de análise, registra aprovações e abre uma sessão interativa no Claude Code ou no Codex CLI.

## O que a Central faz

- Lista automaticamente os repositórios Git diretamente dentro de `C:\git`.
- Permite escolher o repositório, o agente (Claude Code ou Codex CLI) e o tipo de branch (`feature` ou `hotfix`).
- Cria uma worktree exclusiva para cada ticket, sem trocar o checkout principal.
- Mantém anexos, escopo, plano, eventos e estado separados por ticket.
- Mostra arquivos Markdown em visualização formatada, com opção de texto bruto.
- Mantém a entrega local: não faz push, merge, PR ou publicação automática.

## Pré-requisitos

- Windows 10/11.
- Acesso aos repositórios em `C:\git`.
- Git for Windows.
- Pelo menos um agente instalado:
  - Claude Code: `npm install --global @anthropic-ai/claude-code`
  - Codex CLI: `npm install --global @openai/codex`
- Para o fluxo ERP, a skill `desenvolver-ticket` deve estar instalada em `%USERPROFILE%\.claude\skills\desenvolver-ticket`.

O instalador da Central verifica ou instala Node.js 22+ e npm. A instalação dos agentes pode exigir autenticação própria de cada ferramenta.

## Instalação

1. Baixe ou clone este repositório.
2. Abra a pasta do projeto.
3. Execute:

```cmd
instalar-central.cmd
```

O instalador instala as dependências npm, cria o atalho **Central de Tickets Claude** na Área de Trabalho e configura a execução local.

Para iniciar somente o servidor, sem abrir o navegador:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\abrir-central.ps1 -SomenteServidor
```

A interface fica disponível em `http://127.0.0.1:17861`. Abra-a pelo atalho para receber o cookie de autenticação local.

## Fluxo de uso

1. Abra a Central e clique em **Novo ticket**.
2. Informe ticket, sprint, origem/base, escopo e entrega.
3. Escolha o repositório disponível em `C:\git`.
4. Escolha `feature` ou `hotfix`.
5. Escolha Claude Code ou Codex CLI.
6. Selecione anexos diretamente no formulário, quando necessário.
7. Confira os dados e clique em **Iniciar análise**.
8. Leia o plano apresentado pelo agente.
9. Só depois envie na conversa a frase exata `APROVAR TICKET`.
10. Acompanhe testes, revisão, diff e documento final pela Central.

### Importar diretamente do Azure DevOps

Use **Importar do Azure** na tela inicial e cole o link do work item (ou somente o ID). Na primeira utilização, informe um PAT do Azure com permissão de leitura de Work Items; a Central o protege com o DPAPI do Windows e não o salva no repositório. A Central consulta o título, descrição, sprint, tags e anexos, mostra uma prévia e só cadastra depois da sua confirmação.

O repositório, agente, tipo de branch, forma de entrega e anexos ficam revisáveis antes do cadastro. Os anexos selecionados são baixados para a pasta de referências do ticket. A importação não abre Claude/Codex e não publica branch, PR ou comentário no Azure. Se o Azure não informar o código do ticket ou a sprint em formato numérico, esses campos devem ser preenchidos manualmente na prévia.

### Computer Use nas sessões Claude

Tickets Claude novos recebem um servidor MCP local `central-computer` iniciado somente durante a sessão do ticket. Ele fornece captura de tela, janela ativa, clique, teclado e espera visual. Os prints são gravados diretamente em `scratch/evidence` com o ticket no nome do arquivo e retornados ao Claude como imagem para inspeção. A sessão deve manter o sistema sob teste em primeiro plano, não capturar credenciais e incluir os estados `before`, `action` e `result` no documento final. Sessões antigas precisam ser encerradas e abertas novamente para carregar o MCP.

O filtro inicial mostra somente tickets em andamento. Tickets entregues podem ser consultados pelo filtro **Entregues**.

## Regras de branches

### ERP

Use uma origem `release/...`, conforme a release do ticket. A Central cria a branch no formato:

```text
feature/<sprint>/<ticket>
```

ou:

```text
hotfix/<sprint>/<ticket>
```

### PDV e Força de Vendas Web

Para repositórios com essa padronização, use `main` como origem. A branch segue o mesmo formato `feature`/`hotfix` e o destino de homologação é `pre_main`.

A Central não abre o PR automaticamente. Depois da validação local, o PR deve ser criado manualmente conforme as regras do Azure DevOps da equipe.

## Anexos e dados locais

Os anexos selecionados são copiados para a pasta de referências configurada no perfil local. Os dados de tickets, planos e eventos ficam em:

```text
%LOCALAPPDATA%\CentralTicketsClaude
```

O estado do coordenador fica na raiz configurada pela skill, normalmente:

```text
%USERPROFILE%\.claude\ticket-state
```

Essas pastas podem conter informações internas. Não publique dados de clientes, dumps, tokens ou anexos no GitHub.

## Banco de dados e testes

No fluxo ERP, os testes devem usar SQL Server local (`localhost`) com autenticação integrada. O procedimento recomendado é restaurar uma cópia exclusiva, como `SSBD_TESTE_<TICKET>`, e direcionar o harness para essa cópia. O banco original não deve ser alterado.

Se o cenário depender de dados específicos de um cliente e eles não estiverem disponíveis localmente, o teste deve ser marcado como bloqueado — não aprovado com dados apenas parecidos.

## Desenvolvimento e testes do projeto

Instalar dependências:

```cmd
npm install
```

Executar testes:

```cmd
npm test
```

Verificações sintáticas:

```cmd
node --check central/server.mjs
node --check central/session-runner.mjs
node --check central/public/app.js
```

## Segurança e limitações

- O servidor escuta somente em `127.0.0.1`.
- Cada ticket possui lock e worktree próprios.
- Push, PR e comandos Git destrutivos são bloqueados pelo fluxo da Central.
- Sessões Claude recebem os hooks e as políticas da Central.
- Sessões Codex usam `workspace-write` e aprovação sob demanda; os hooks específicos do Claude não são aplicados ao Codex.
- A Central não substitui a revisão humana do diff, dos testes e dos dados usados.

### Gates de qualidade e evidencias

Cada worktree nova recebe `test-matrix.json`, `evidence-manifest.json` e a pasta `scratch/evidence`. O agente deve registrar, para cada criterio e variante, o esperado, o obtido, o resultado e uma referencia real de log, consulta, screenshot ou artefato. A Central confere se os caminhos existem e ficam dentro das pastas permitidas.

O commit local e bloqueado quando faltam compilacao, testes, revisao, esperado/obtido ou evidencias declaradas. O status **Entregue** e bloqueado quando ha resultado falho, pendente, bloqueado ou nao executado, quando um teste de tela nao tem screenshot, ou quando o documento final/evidencias informados nao existem. A tela mostra os motivos concretos para correcao, em vez de aceitar apenas um `estado.json` autoafirmado.

## Estrutura principal

```text
central/server.mjs          servidor HTTP local
central/core.mjs            tickets, worktrees e estado
central/session-runner.mjs  abertura das sessões Claude/Codex
central/policy.mjs          controles da sessão Claude
central/public/             interface web
central/tests/              testes automatizados
instalar-central.ps1        instalador Windows
```

## Licença e uso interno

Este projeto foi criado para uso interno da equipe. Antes de redistribuir, confirme as políticas da empresa, do Claude Code, do Codex CLI e dos repositórios acessados.
