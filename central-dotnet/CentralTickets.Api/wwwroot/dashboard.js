const live = new EventSource('/api/stream');
live.addEventListener('tickets', () => {
  if (document.visibilityState === 'visible') location.reload();
});
live.onerror = () => {};
