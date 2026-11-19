(() => {
    const root = document.documentElement;
    let preference;
    try { preference = localStorage.getItem('theme'); } catch { }
    root.dataset.theme = preference === 'dark' || preference === 'light'
        ? preference
        : (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');

    const syncButtons = () => {
        document.querySelectorAll('.theme-toggle').forEach(button => {
            button.setAttribute('aria-pressed', String(root.dataset.theme === 'dark'));
        });
    };

    document.addEventListener('click', event => {
        if (!event.target.closest('.theme-toggle')) return;
        root.dataset.theme = root.dataset.theme === 'dark' ? 'light' : 'dark';
        try { localStorage.setItem('theme', root.dataset.theme); } catch { }
        syncButtons();
    });
    document.addEventListener('DOMContentLoaded', () => {
        syncButtons();
        new MutationObserver(syncButtons).observe(document.body, { childList: true, subtree: true });
    });
})();