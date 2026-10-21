const live = new EventSource('/api/stream');
live.addEventListener('tickets', () => {
  if (document.visibilityState === 'visible') location.reload();
});
live.onerror = () => {};

const sidebar = document.querySelector('#ticket-sidebar');
const sidebarToggle = document.querySelector('#sidebar-toggle');
if (sidebar && sidebarToggle) {
  let collapsed = false;
  try { collapsed = localStorage.getItem('central-sidebar-collapsed') === 'true'; } catch { }
  const applySidebarState = () => {
    document.body.classList.toggle('sidebar-collapsed', collapsed);
    sidebarToggle.setAttribute('aria-expanded', String(!collapsed));
    sidebarToggle.setAttribute('aria-label', collapsed ? 'Expandir barra lateral' : 'Recolher barra lateral');
    sidebarToggle.title = collapsed ? 'Expandir barra lateral' : 'Recolher barra lateral';
  };
  applySidebarState();
  sidebarToggle.addEventListener('click', () => {
    collapsed = !collapsed;
    applySidebarState();
    try { localStorage.setItem('central-sidebar-collapsed', String(collapsed)); } catch { }
  });
}

document.querySelector('#refresh-dashboard')?.addEventListener('click', () => location.reload());

const environment = document.querySelector('#environment');
if (environment) {
  fetch('/api/profile', { cache: 'no-store' }).then(async response => {
    if (!response.ok) throw new Error('Perfil local indisponível.');
    return response.json();
  }).then(profile => {
    const checks = profile.checks || {};
    const labels = { claude: 'Claude Code', git: 'Git', repository: 'repositório principal', skill: 'skill desenvolver-ticket' };
    const missing = Object.entries(labels).filter(([key]) => checks[key] === false).map(([, label]) => label);
    const notices = [];
    if (profile.demo) notices.push('Modo de demonstração: sessões reais desativadas.');
    if (missing.length) notices.push(`Confira a instalação: ${missing.join(', ')}.`);
    if (profile.memory === false) notices.push('AI Memory indisponível; os aprendizados ficam na fila local.');
    if (profile.executionOwner) notices.push(`Compilação/testes reservados para ${profile.executionOwner}.`);
    if (profile.aiMemory?.pending > 0) notices.push(`${profile.aiMemory.pending} aprendizado(s) aguardando sincronização.`);
    environment.textContent = notices.join(' ');
    environment.hidden = notices.length === 0;
    const repo = document.querySelector('#repo-label');
    if (repo) repo.textContent = `${profile.repositories?.length || 0} repositório(s) configurado(s) · Central local em ASP.NET Core`;
  }).catch(() => {
    environment.textContent = 'Não foi possível carregar o estado do ambiente local.';
    environment.hidden = false;
  });
}

document.querySelector('#sync-memory-home')?.addEventListener('click', async event => {
  const button = event.currentTarget, feedback = document.querySelector('#home-feedback');
  button.disabled = true; feedback.hidden = true;
  try {
    const response = await fetch('/api/memory-sync', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Central-Request': '1' }, body: '{}' });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.error || 'Não foi possível atualizar o conhecimento.');
    feedback.textContent = `AI Memory: ${result.sent || 0} enviado(s), ${result.pending || 0} aguardando nova tentativa; ${result.alreadyCurrent || 0} já atualizado(s).`;
    feedback.classList.toggle('warning', Number(result.pending || 0) > 0);
    feedback.hidden = false;
  } catch (error) {
    feedback.textContent = error.message;
    feedback.classList.add('warning'); feedback.hidden = false;
  } finally { button.disabled = false; }
});
