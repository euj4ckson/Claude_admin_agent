const $ = (selector) => document.querySelector(selector);
const labels = { fiveHour: 'Janela de 5 horas', sevenDay: 'Janela de 7 dias' };
const percent = value => Number.isFinite(Number(value)) && Number(value) >= 0 && Number(value) <= 100 ? `${Number(value).toFixed(1)}%` : 'Indisponível';
function renderWindows(target, snapshot) {
  const windows = snapshot?.windows || {};
  target.innerHTML = Object.entries(labels).map(([key, label]) => {
    const item = windows[key] || {}, valid = ['current', 'stale', 'expired'].includes(item.status) && Number.isFinite(Number(item.used));
    const width = valid ? Math.max(0, Math.min(100, Number(item.used))) : 0;
    const statusText = item.status === 'current' ? 'Atual' : item.status === 'stale' ? 'Desatualizado' : item.status === 'expired' ? 'Janela encerrada' : 'Sem leitura';
    const reset = item.resetsAt ? new Date(item.resetsAt).toLocaleString('pt-BR') : '';
    return `<div class="usage-window"><div class="usage-window-label"><strong>${label}</strong><span>${valid ? percent(item.used) : statusText}</span></div><div class="usage-track"><i style="width:${width}%"></i></div><small>${reset ? `Renova ${reset} · ${statusText}` : statusText}</small></div>`;
  }).join('');
}
function renderTokens(snapshot) {
  const node = $('#codex-tokens'), tokens = snapshot?.tokens;
  if (!tokens) { node.textContent = 'Contagem de tokens indisponível.'; return; }
  const rows = Array.isArray(tokens.daily) ? tokens.daily : null;
  const recent = rows?.slice(-14) || [];
  const max = Math.max(1, ...recent.map(row => Number(row.tokens) || 0));
  node.innerHTML = `<div class="token-summary"><div><span>Tokens acumulados</span><strong>${Number.isSafeInteger(tokens.lifetime) ? tokens.lifetime.toLocaleString('pt-BR') : 'Indisponível'}</strong></div><span>Fonte: Codex app-server</span></div>${rows ? `<div class="token-chart" aria-label="Tokens por dia">${recent.map(row => `<div class="token-day" title="${row.date}: ${Number(row.tokens).toLocaleString('pt-BR')} tokens"><i style="height:${Math.max(2, Number(row.tokens) / max * 100)}%"></i><small>${new Date(`${row.date}T12:00:00`).toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit' })}</small></div>`).join('')}</div><p class="chart-caption">Tokens por dia conforme os registros retornados pela conta. Dias sem registro não são preenchidos.</p>` : '<p class="chart-caption">A API não forneceu contagem diária para esta leitura.</p>'}`;
}
async function refresh() {
  const [claude, codex] = await Promise.allSettled(['/api/usage', '/api/usage/codex'].map(url => fetch(url, { cache: 'no-store' }).then(response => { if (!response.ok) throw new Error('Falha ao consultar a fonte local.'); return response.json(); })));
  if (claude.status === 'fulfilled') {
    renderWindows($('#claude-windows'), claude.value);
    $('#claude-status').textContent = claude.value.unavailable ? 'Sem leitura válida' : claude.value.windows?.fiveHour?.status === 'stale' || claude.value.windows?.sevenDay?.status === 'stale' ? 'Leitura desatualizada' : 'Fonte lida';
    $('#claude-source').textContent = claude.value.source ? `Fonte: ${claude.value.source} · capturada ${new Date(claude.value.capturedAt).toLocaleString('pt-BR')}` : 'Nenhum snapshot local válido encontrado.';
  } else { $('#claude-status').textContent = 'Falha na leitura'; $('#claude-windows').textContent = 'Não foi possível consultar o snapshot local.'; }
  if (codex.status === 'fulfilled') {
    renderWindows($('#codex-windows'), codex.value); renderTokens(codex.value);
    $('#codex-status').textContent = codex.value.refreshFailed ? 'Última consulta falhou' : codex.value.unavailable ? 'Sem leitura válida' : 'Fonte lida';
    $('#codex-source').textContent = codex.value.source ? `Fonte: ${codex.value.source} · capturada ${new Date(codex.value.capturedAt).toLocaleString('pt-BR')}` : 'Codex app-server não retornou dados de uso.';
  } else { $('#codex-status').textContent = 'Falha na leitura'; $('#codex-windows').textContent = 'Não foi possível consultar o Codex app-server.'; renderTokens(null); }
  $('#usage-updated').textContent = `Atualizado ${new Date().toLocaleTimeString('pt-BR')}`;
}
refresh();
setInterval(refresh, 60_000);
const liveUsage = new EventSource('/api/stream');
liveUsage.addEventListener('usage', event => {
  try {
    const snapshot = JSON.parse(event.data);
    if (snapshot.provider === 'claude') {
      renderWindows($('#claude-windows'), snapshot);
      $('#claude-status').textContent = snapshot.unavailable ? 'Sem leitura válida' : 'Fonte lida';
      $('#claude-source').textContent = snapshot.source ? `Fonte: ${snapshot.source} · capturada ${new Date(snapshot.capturedAt).toLocaleString('pt-BR')}` : 'Nenhum snapshot local válido encontrado.';
    }
    if (snapshot.provider === 'codex') {
      renderWindows($('#codex-windows'), snapshot); renderTokens(snapshot);
      $('#codex-status').textContent = snapshot.refreshFailed ? 'Última consulta falhou' : snapshot.unavailable ? 'Sem leitura válida' : 'Fonte lida';
      $('#codex-source').textContent = snapshot.source ? `Fonte: ${snapshot.source} · capturada ${new Date(snapshot.capturedAt).toLocaleString('pt-BR')}` : 'Codex app-server não retornou dados de uso.';
    }
    $('#usage-updated').textContent = `Atualizado ${new Date().toLocaleTimeString('pt-BR')}`;
  } catch { }
});
