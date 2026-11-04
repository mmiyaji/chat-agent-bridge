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
  }
  root.classList.remove('en-pending');

  // Language menu: the button shows the current language; the list shows each language in its own name.
  var current = english ? 'en' : 'ja';
  var names = { ja: '日本語', en: 'English' };
  var langButton = document.getElementById('lang');
  var langLabel = document.getElementById('lang-label');
  var langList = document.getElementById('lang-list');
  if (langButton && langLabel && langList) {
    langLabel.textContent = names[current];
    langLabel.lang = current;
    langButton.setAttribute('aria-label', (english ? 'Language: ' : '言語: ') + names[current]);
    langList.querySelectorAll('a[data-lang]').forEach(function (link) {
      var code = link.getAttribute('data-lang');
      link.href = location.pathname + (code === 'ja' ? '' : '?lang=' + code) + location.hash;
      if (code === current) link.setAttribute('aria-current', 'true');
    });
    var links = function () { return Array.prototype.slice.call(langList.querySelectorAll('a')); };
    var setOpen = function (open, focusFirst) {
      langList.hidden = !open;
      langButton.setAttribute('aria-expanded', open ? 'true' : 'false');
      if (open && focusFirst) (langList.querySelector('[aria-current]') || links()[0]).focus();
    };
    langButton.addEventListener('click', function () { setOpen(langList.hidden, false); });
    langButton.addEventListener('keydown', function (event) {
      if (event.key === 'ArrowDown' || event.key === 'Enter' || event.key === ' ') { event.preventDefault(); setOpen(true, true); }
    });
    langList.addEventListener('keydown', function (event) {
      var items = links(), index = items.indexOf(document.activeElement);
      if (event.key === 'Escape') { setOpen(false); langButton.focus(); }
      else if (event.key === 'ArrowDown') { event.preventDefault(); items[(index + 1) % items.length].focus(); }
      else if (event.key === 'ArrowUp') { event.preventDefault(); items[(index - 1 + items.length) % items.length].focus(); }
    });
    document.addEventListener('click', function (event) {
      if (!langList.hidden && !event.target.closest('.langmenu')) setOpen(false);
    });
    langList.addEventListener('focusout', function (event) {
      if (!event.relatedTarget || !event.relatedTarget.closest('.langmenu')) setOpen(false);
    });
  }

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
