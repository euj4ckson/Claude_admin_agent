(() => {
  const storageKey = 'central-theme';
  const systemTheme = () => window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  let savedTheme;
  try { savedTheme = localStorage.getItem(storageKey); } catch { /* Private browsing can disable storage. */ }
  let theme = savedTheme === 'dark' || savedTheme === 'light' ? savedTheme : systemTheme();

  const apply = () => {
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme;
    const button = document.getElementById('theme-toggle');
    if (!button) return;
    const next = theme === 'dark' ? 'claro' : 'escuro';
    button.setAttribute('aria-label', `Ativar tema ${next}`);
    button.title = `Ativar tema ${next}`;
    button.setAttribute('aria-pressed', String(theme === 'dark'));
    button.querySelector('.theme-toggle-label').textContent = `Tema ${next}`;
  };

  apply();
  document.addEventListener('DOMContentLoaded', () => {
    apply();
    document.getElementById('theme-toggle')?.addEventListener('click', () => {
      theme = theme === 'dark' ? 'light' : 'dark';
      savedTheme = theme;
      try { localStorage.setItem(storageKey, theme); } catch { /* The current page still switches. */ }
      apply();
    });
  });
  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (savedTheme === 'dark' || savedTheme === 'light') return;
    theme = systemTheme();
    apply();
  });
})();
