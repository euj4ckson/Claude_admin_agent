const $ = (selector, root = document) => root.querySelector(selector);
const post = async (url, body = {}) => {
  const response = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Central-Request': '1' },
    body: JSON.stringify(body)
  });
  const value = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(value.error || 'A operação não pôde ser concluída.');
  return value;
};
const fillComponents = (form, profile, primaryId) => {
  const box = form.querySelector('.component-picker');
  if (!box) return;
  const repoList = (profile.repositories || []).filter(item => ['ss_fva', 'ss_wcf_fvan'].includes(item.id) || item.default);
  box.hidden = !repoList.some(item => item.id === primaryId);
  const rows = box.querySelector('[data-component-rows]'); rows.replaceChildren();
  for (const repo of repoList.filter(item => item.id !== primaryId)) {
    const row = document.createElement('label'); row.className = 'component-row';
    const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; checkbox.dataset.repositoryId = repo.id;
    const name = document.createElement('span'); name.textContent = repo.name;
    const release = document.createElement('input'); release.type = 'text'; release.placeholder = repo.defaultBase || 'Branch de origem (ex.: release/106.4.3)';
    release.value = repo.defaultBase || ''; release.readOnly = Boolean(repo.standard?.base);
    row.append(checkbox, name, release); rows.append(row);
  }
};
const selectedComponents = form => [...(form.querySelectorAll('.component-row input[type="checkbox"]'))]
  .filter(input => input.checked)
  .map(input => ({ repositoryId: input.dataset.repositoryId, release: input.parentElement.querySelector('input[type="text"]').value }));

const sessionFeedback = $('#session-feedback');
const sessionAction = async (buttonId, action, successMessage) => {
  const button = $(buttonId);
  if (!button || !window.centralTicket?.id) return;
  button.disabled = true;
  if (sessionFeedback) sessionFeedback.hidden = true;
  try {
    const result = await action();
    if (sessionFeedback) { sessionFeedback.textContent = result.message || successMessage; sessionFeedback.hidden = false; sessionFeedback.classList.remove('form-error'); sessionFeedback.classList.add('field-help'); }
    if (buttonId === '#prepare-worktree') {
      const check = await fetch(`/api/tickets/${encodeURIComponent(window.centralTicket.id)}/preflight`);
      const preflight = await check.json();
      if (!preflight.ok) throw new Error(`Pré-flight bloqueado: ${(preflight.blocking || []).join('; ')}`);
      if (sessionFeedback) sessionFeedback.textContent = 'Worktree preparada e pré-flight aprovado. Você já pode abrir a conversa.';
    }
  } catch (error) {
    if (sessionFeedback) { sessionFeedback.textContent = error.message; sessionFeedback.hidden = false; sessionFeedback.classList.add('form-error'); }
  } finally { button.disabled = false; }
};
$('#prepare-worktree')?.addEventListener('click', () => sessionAction('#prepare-worktree', () => post(`/api/tickets/${encodeURIComponent(window.centralTicket.id)}/prepare`), 'Worktree preparada.'));
$('#start-session')?.addEventListener('click', () => sessionAction('#start-session', () => post(`/api/tickets/${encodeURIComponent(window.centralTicket.id)}/start`), 'Conversa iniciada.'));

const ticketDialog = $('#ticket-dialog');
if (ticketDialog) {
  $('#new-ticket').onclick = async () => {
    try {
      const profileResponse = await fetch('/api/profile');
      if (!profileResponse.ok) throw new Error('Não foi possível carregar os repositórios configurados.');
      const profile = await profileResponse.json();
      const repository = $('#repository');
      repository.replaceChildren(...profile.repositories.map(item => {
        const option = document.createElement('option');
        option.value = item.id;
        option.textContent = `${item.name}${item.default ? ' (padrão)' : ''}`;
        option.dataset.base = item.defaultBase || item.standard?.base || 'main';
        return option;
      }));
      const agent = $('#ticket-form').elements.agent;
      for (const option of agent.options) {
        option.disabled = !profile.agents.find(item => item.id === option.value)?.available;
      }
      const firstAvailable = [...agent.options].find(option => !option.disabled);
      if (firstAvailable) agent.value = firstAvailable.value;
      fillComponents($('#ticket-form'), profile, repository.value);
      $('#ticket-form').elements.release.value = repository.selectedOptions[0]?.dataset.base || '';
      ticketDialog.showModal();
    } catch (error) {
      const box = $('#form-error'); box.textContent = error.message; box.hidden = false;
      ticketDialog.showModal();
    }
  };
  $('#repository').onchange = async event => {
    $('#ticket-form').elements.release.value = event.target.selectedOptions[0]?.dataset.base || '';
    const response = await fetch('/api/profile'); fillComponents($('#ticket-form'), await response.json(), event.target.value);
  };
  $('#cancel-ticket').onclick = () => ticketDialog.close();
  $('#ticket-form').onsubmit = async event => {
    event.preventDefault();
    const form = event.currentTarget;
    const fields = form.elements;
    const submit = $('button[type="submit"]', form);
    submit.disabled = true;
    const errorBox = $('#form-error'); errorBox.hidden = true;
    try {
      const ticket = await post('/api/tickets', {
        ticket: fields.ticket.value, sprint: fields.sprint.value,
        repository: fields.repository.value, agent: fields.agent.value,
        branchType: fields.branchType.value, release: fields.release.value,
        title: fields.title.value, scope: fields.scope.value, delivery: fields.delivery.value,
        components: JSON.stringify(selectedComponents(form))
      });
      location.href = `/tickets/${encodeURIComponent(ticket.id)}`;
    } catch (error) { errorBox.textContent = error.message; errorBox.hidden = false; }
    finally { submit.disabled = false; }
  };
}

const azureDialog = $('#azure-dialog');
if (azureDialog) {
  let previewData = null;
  $('#azure-import').onclick = async () => {
    $('#azure-error').hidden = true;
    $('#azure-fields').hidden = true;
    $('#azure-preview').hidden = true;
    $('#azure-status').textContent = '';
    previewData = null;
    azureDialog.showModal();
    try {
      const response = await fetch('/api/azure/status');
      const status = await response.json();
      $('#azure-status').textContent = status.configured ? 'Acesso Azure configurado e protegido neste Windows.' : 'Configure um PAT de leitura para consultar o Azure.';
    } catch { }
  };
  $('#cancel-azure').onclick = () => azureDialog.close();
  $('#save-azure-pat').onclick = async () => {
    const button = $('#save-azure-pat'), pat = $('#azure-form').elements.pat.value;
    button.disabled = true;
    try {
      await post('/api/azure/configure', { pat });
      $('#azure-form').elements.pat.value = '';
      $('#azure-status').textContent = 'Acesso protegido salvo neste Windows.';
    } catch (error) { $('#azure-error').textContent = error.message; $('#azure-error').hidden = false; }
    finally { button.disabled = false; }
  };
  $('#preview-azure').onclick = async () => {
    const button = $('#preview-azure'), form = $('#azure-form'), error = $('#azure-error');
    button.disabled = true; error.hidden = true; $('#import-azure').disabled = true;
    try {
      previewData = await post('/api/azure/preview', { reference: form.elements.reference.value });
      const panel = $('#azure-preview'); panel.replaceChildren(); panel.hidden = false;
      const heading = document.createElement('strong'); heading.textContent = `${previewData.suggested.ticket || 'Ticket não identificado'} · ${previewData.suggested.title || 'Sem título'}`; panel.append(heading);
      const state = document.createElement('p'); state.textContent = `${previewData.fields.state || 'Estado não informado'} · ${previewData.fields.iteration || 'Sprint não identificada'} · Work item #${previewData.source.id}`; panel.append(state);
      if (previewData.warnings.length) {
        const list = document.createElement('ul');
        for (const warning of previewData.warnings) { const li = document.createElement('li'); li.textContent = warning; list.append(li); }
        panel.append(list);
      }
      const formFields = $('#azure-fields'); formFields.hidden = false;
      form.elements.ticket.value = previewData.suggested.ticket || '';
      form.elements.sprint.value = previewData.suggested.sprint || '';
      form.elements.title.value = previewData.suggested.title || '';
      form.elements.scope.value = previewData.suggested.scope || '';
      form.elements.branchType.value = previewData.suggested.branchType || 'feature';
      const profileResponse = await fetch('/api/profile');
      const profile = await profileResponse.json();
      const repoSelect = $('#azure-repository');
      repoSelect.replaceChildren(...profile.repositories.map(item => {
        const option = document.createElement('option'); option.value = item.id; option.textContent = item.name;
        option.dataset.base = item.defaultBase || item.standard?.base || 'main';
        if (item.id === previewData.suggested.repositoryId) option.selected = true;
        return option;
      }));
      fillComponents(form, profile, repoSelect.value);
      form.elements.release.value = repoSelect.selectedOptions[0]?.dataset.base || previewData.suggested.release || '';
      const agent = form.elements.agent;
      for (const option of agent.options) option.disabled = !profile.agents.find(item => item.id === option.value)?.available;
      const available = [...agent.options].find(option => !option.disabled); if (available) agent.value = available.value;
      const files = $('#azure-attachments'); files.replaceChildren();
      if (!previewData.attachments.length) files.textContent = 'Nenhum anexo relacionado ao work item.';
      for (const [index, file] of previewData.attachments.entries()) {
        const label = document.createElement('label'), checkbox = document.createElement('input'), name = document.createElement('span');
        checkbox.type = 'checkbox'; checkbox.checked = true; checkbox.dataset.fileIndex = String(index); name.textContent = `${file.name}${file.size ? ` · ${Math.ceil(file.size / 1024)} KB` : ''}`;
        label.append(checkbox, name); files.append(label);
      }
      $('#import-azure').disabled = false;
    } catch (exception) { error.textContent = exception.message; error.hidden = false; }
    finally { button.disabled = false; }
  };
  $('#azure-repository').onchange = async event => {
    const selected = event.target.selectedOptions[0];
    if (selected) $('#azure-form').elements.release.value = selected.dataset.base || '';
    const response = await fetch('/api/profile');
    fillComponents($('#azure-form'), await response.json(), event.target.value);
  };
  $('#azure-form').onsubmit = async event => {
    event.preventDefault();
    if (!previewData) return;
    const form = event.currentTarget, fields = form.elements, button = $('#import-azure'), error = $('#azure-error');
    button.disabled = true; error.hidden = true;
    try {
      const attachments = [...document.querySelectorAll('#azure-attachments input[data-file-index]:checked')]
        .map(input => previewData.attachments[Number(input.dataset.fileIndex)]?.name).filter(Boolean);
      const result = await post('/api/azure/import', {
        reference: fields.reference.value, previewId: previewData.previewId,
        ticket: fields.ticket.value, sprint: fields.sprint.value, release: fields.release.value,
        title: fields.title.value, scope: fields.scope.value, repository: fields.repository.value,
        agent: fields.agent.value, branchType: fields.branchType.value, delivery: fields.delivery.value,
        attachments, downloadAttachments: true, components: JSON.stringify(selectedComponents(form))
      });
      const id = result.ticket.id;
      if (result.warnings?.length) alert(`Ticket criado, mas há anexos/avisos que precisam ser conferidos:\n\n${result.warnings.join('\n')}`);
      location.href = `/tickets/${encodeURIComponent(id)}`;
    } catch (exception) { error.textContent = exception.message; error.hidden = false; }
    finally { button.disabled = false; }
  };
}

const appendInlineMarkdown = (parent, value) => {
  const pattern = /(`[^`]+`|\*\*[^*]+\*\*|\*[^*]+\*)/g;
  let position = 0;
  for (const match of value.matchAll(pattern)) {
    if (match.index > position) parent.append(document.createTextNode(value.slice(position, match.index)));
    const token = match[0];
    const element = document.createElement(token.startsWith('`') ? 'code' : token.startsWith('**') ? 'strong' : 'em');
    element.textContent = token.startsWith('`') ? token.slice(1, -1) : token.startsWith('**') ? token.slice(2, -2) : token.slice(1, -1);
    parent.append(element);
    position = match.index + token.length;
  }
  if (position < value.length) parent.append(document.createTextNode(value.slice(position)));
};

const renderMarkdown = (host, source) => {
  const lines = String(source || '').replace(/\r\n?/g, '\n').split('\n');
  const fragment = document.createDocumentFragment(), headings = [];
  let index = 0;
  const appendList = (type) => {
    const list = document.createElement(type);
    while (index < lines.length) {
      const match = type === 'ol' ? /^\s*\d+[.)]\s+(.+)$/.exec(lines[index]) : /^\s*[-*+]\s+(.+)$/.exec(lines[index]);
      if (!match) break;
      const item = document.createElement('li'); appendInlineMarkdown(item, match[1]); list.append(item); index++;
    }
    fragment.append(list);
  };
  while (index < lines.length) {
    const line = lines[index].trim();
    if (!line) { index++; continue; }
    if (/^```/.test(line)) {
      index++; const code = [];
      while (index < lines.length && !/^\s*```/.test(lines[index])) code.push(lines[index++]);
      if (index < lines.length) index++;
      const pre = document.createElement('pre'), block = document.createElement('code');
      block.textContent = code.join('\n'); pre.append(block); fragment.append(pre); continue;
    }
    const heading = /^(#{1,3})\s+(.+)$/.exec(line);
    if (heading) {
      const level = heading[1].length, element = document.createElement(`h${level}`);
      appendInlineMarkdown(element, heading[2]);
      const slug = heading[2].normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || `secao-${headings.length + 1}`;
      element.id = `md-${slug}-${headings.length}`; headings.push({ id: element.id, text: heading[2] });
      fragment.append(element); index++; continue;
    }
    if (/^([-*_])(?:\s*\1){2,}$/.test(line)) { fragment.append(document.createElement('hr')); index++; continue; }
    if (/^\s*[-*+]\s+/.test(lines[index])) { appendList('ul'); continue; }
    if (/^\s*\d+[.)]\s+/.test(lines[index])) { appendList('ol'); continue; }
    if (line.includes('|') && /^\s*\|?\s*:?-{3,}/.test(lines[index + 1] || '')) {
      const cells = value => value.trim().replace(/^\||\|$/g, '').split('|').map(cell => cell.trim());
      const wrapper = document.createElement('div'), table = document.createElement('table'), head = document.createElement('thead'), body = document.createElement('tbody');
      wrapper.className = 'md-table-wrap';
      const headerRow = document.createElement('tr');
      for (const value of cells(lines[index])) { const cell = document.createElement('th'); appendInlineMarkdown(cell, value); headerRow.append(cell); }
      head.append(headerRow); index += 2;
      while (index < lines.length && lines[index].includes('|')) {
        const row = document.createElement('tr');
        for (const value of cells(lines[index])) { const cell = document.createElement('td'); appendInlineMarkdown(cell, value); row.append(cell); }
        body.append(row); index++;
      }
      table.append(head, body); wrapper.append(table); fragment.append(wrapper); continue;
    }
    if (/^>\s?/.test(line)) {
      const quote = document.createElement('blockquote'); appendInlineMarkdown(quote, line.replace(/^>\s?/, '')); fragment.append(quote); index++; continue;
    }
    const paragraph = document.createElement('p');
    const text = [line]; index++;
    while (index < lines.length && lines[index].trim() && !/^(#{1,3}\s|```|[-*+]\s|\d+[.)]\s|>\s?)/.test(lines[index].trim())) text.push(lines[index++].trim());
    appendInlineMarkdown(paragraph, text.join(' ')); fragment.append(paragraph);
  }
  host.replaceChildren();
  if (headings.length) {
    const outline = document.createElement('nav'); outline.className = 'md-outline'; outline.setAttribute('aria-label', 'Índice');
    const label = document.createElement('strong'); label.textContent = 'Índice'; outline.append(label);
    for (const heading of headings) { const link = document.createElement('a'); link.href = `#${heading.id}`; link.textContent = heading.text; outline.append(link); }
    host.append(outline);
  }
  host.append(fragment);
};

const ticket = window.centralTicket;
if (ticket) {
  const dialog = $('#registration-dialog');
  $('#edit-ticket').onclick = () => {
    const form = $('#registration-form');
    for (const key of ['agent', 'branchType', 'sprint', 'release', 'delivery']) form.elements[key].value = ticket[key] || '';
    $('#registration-error').hidden = true;
    dialog.showModal();
  };
  $('#cancel-edit').onclick = () => dialog.close();
  $('#registration-form').onsubmit = async event => {
    event.preventDefault();
    const form = event.currentTarget, fields = form.elements, error = $('#registration-error');
    const submit = $('button[type="submit"]', form); submit.disabled = true; error.hidden = true;
    try {
      await post(`/api/tickets/${encodeURIComponent(ticket.id)}/registration`, {
        agent: fields.agent.value, branchType: fields.branchType.value,
        sprint: fields.sprint.value, release: fields.release.value, delivery: fields.delivery.value
      });
      location.reload();
    } catch (exception) { error.textContent = exception.message; error.hidden = false; }
    finally { submit.disabled = false; }
  };
  $('#complete-ticket').onclick = async event => {
    const button = event.currentTarget;
    if (!confirm(`Marcar ${ticket.id} como entregue? Isso registra a decisão e preserva evidências e pendências.`)) return;
    button.disabled = true;
    try { await post(`/api/tickets/${encodeURIComponent(ticket.id)}/complete`); location.reload(); }
    catch (error) { alert(error.message); button.disabled = false; }
  };
}

if (ticket) {
  const showFeedback = (message, failed = false) => {
    if (!sessionFeedback) return;
    sessionFeedback.textContent = message; sessionFeedback.hidden = false;
    sessionFeedback.classList.toggle('form-error', failed); sessionFeedback.classList.toggle('field-help', !failed);
  };
  $('#focus-session')?.addEventListener('click', async event => {
    event.currentTarget.disabled = true;
    try { const result = await post(`/api/tickets/${encodeURIComponent(ticket.id)}/focus`); showFeedback(result.message); }
    catch (error) { showFeedback(error.message, true); }
    finally { event.currentTarget.disabled = false; }
  });
  document.querySelectorAll('[data-open]').forEach(button => button.addEventListener('click', async () => {
    try { const result = await post(`/api/tickets/${encodeURIComponent(ticket.id)}/open`, { kind: button.dataset.open }); showFeedback(result.message); }
    catch (error) { showFeedback(error.message, true); }
  }));
  const contentDialog = $('#content-dialog');
  $('#close-content')?.addEventListener('click', () => contentDialog.close());
  const renderedContent = $('#content-rendered'), rawContent = $('#content-body');
  $('#show-rendered')?.addEventListener('click', () => { renderedContent.hidden = false; rawContent.hidden = true; $('#show-rendered').classList.add('active'); $('#show-raw').classList.remove('active'); });
  $('#show-raw')?.addEventListener('click', () => { renderedContent.hidden = true; rawContent.hidden = false; $('#show-raw').classList.add('active'); $('#show-rendered').classList.remove('active'); });
  $('#print-content')?.addEventListener('click', () => window.print());
  document.querySelectorAll('.content-action').forEach(button => button.addEventListener('click', async () => {
    const kind = button.dataset.kind;
    try {
      const url = kind === 'diff' ? `/api/tickets/${encodeURIComponent(ticket.id)}/diff` : `/api/tickets/${encodeURIComponent(ticket.id)}/content?kind=${encodeURIComponent(kind)}`;
      const response = await fetch(url, { cache: 'no-store' }); const body = await response.json();
      if (!response.ok) throw new Error(body.error || 'Não foi possível abrir o conteúdo.');
      $('#content-title').textContent = kind === 'diff' ? 'ALTERAÇÕES' : kind === 'plan' ? 'PLANO DE ANÁLISE' : 'ENTRADA DO TICKET';
      const content = body.text || 'O conteúdo ainda não foi registrado.';
      $('#content-body').textContent = content; renderMarkdown(renderedContent, content);
      renderedContent.hidden = false; rawContent.hidden = true;
      $('#show-rendered').classList.add('active'); $('#show-raw').classList.remove('active');
      contentDialog.showModal();
    } catch (error) { showFeedback(error.message, true); }
  }));
  const attachmentInput = $('#attachment-files');
  $('#add-attachments')?.addEventListener('click', () => attachmentInput.click());
  attachmentInput?.addEventListener('change', async () => {
    if (!attachmentInput.files?.length) return;
    const button = $('#add-attachments'); button.disabled = true;
    try {
      const form = new FormData(); for (const file of attachmentInput.files) form.append('files', file, file.name);
      const response = await fetch(`/api/tickets/${encodeURIComponent(ticket.id)}/attachments`, { method: 'POST', headers: { 'X-Central-Request': '1' }, body: form });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(result.error || 'Não foi possível adicionar os arquivos.');
      location.reload();
    } catch (error) { showFeedback(error.message, true); }
    finally { button.disabled = false; attachmentInput.value = ''; }
  });
  $('#sync-memory')?.addEventListener('click', async event => {
    const button = event.currentTarget; button.disabled = true;
    try {
      const result = await post(`/api/tickets/${encodeURIComponent(ticket.id)}/memory-sync`);
      showFeedback(result.remote === 'synced' ? 'Conhecimento sincronizado com o AI Memory.' : 'Conhecimento preservado na fila local para nova tentativa.');
    } catch (error) { showFeedback(error.message, true); }
    finally { button.disabled = false; }
  });
}
