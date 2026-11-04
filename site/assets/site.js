(function () {
  var root = document.documentElement;
  var english = new URLSearchParams(location.search).get('lang') === 'en';

  if (english && window.CAB_EN) {
    root.lang = 'en';
    document.title = 'Chat Agent Bridge';
    var description = document.querySelector('meta[name="description"]');
    if (description) description.content = window.CAB_EN['meta.description'];
    document.querySelectorAll('[data-i18n]').forEach(function (element) {
      var text = window.CAB_EN[element.getAttribute('data-i18n')];
      if (typeof text === 'string') element.innerHTML = text;
    });
    document.querySelectorAll('[data-en-src]').forEach(function (image) {
      image.src = image.getAttribute('data-en-src');
      if (image.hasAttribute('data-en-alt')) image.alt = image.getAttribute('data-en-alt');
    });
    document.querySelectorAll('[data-en-href]').forEach(function (link) {
      link.href = link.getAttribute('data-en-href');
      link.setAttribute('data-description', link.getAttribute('data-en-description') || '');
    });
    // Keep the language when moving between sections.
    var lang = document.getElementById('lang');
    if (lang) { lang.textContent = '日本語'; lang.lang = 'ja'; lang.hreflang = 'ja'; lang.href = location.pathname + location.hash; }
  }
  root.classList.remove('en-pending');

  var button = document.getElementById('theme');
  var labels = english ? ['Switch to light mode', 'Switch to dark mode'] : ['ライトモードに切り替え', 'ダークモードに切り替え'];
  function current() {
    return root.dataset.theme || (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');
  }
  function label() { button.setAttribute('aria-label', current() === 'dark' ? labels[0] : labels[1]); }
  if (button) {
    label();
    button.addEventListener('click', function () {
      root.dataset.theme = current() === 'dark' ? 'light' : 'dark';
      try { localStorage.setItem('cab-theme', root.dataset.theme); } catch (e) { /* not persisted */ }
      label();
    });
  }
  if (window.GLightbox) GLightbox({ selector: '.glightbox', touchNavigation: true, loop: true });
})();
