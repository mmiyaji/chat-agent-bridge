(function () {
  var root = document.documentElement;
  var english = new URLSearchParams(location.search).get('lang') === 'en' && Boolean(window.CAB_EN);

  if (english) {
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
      if (link.hasAttribute('data-en-description')) {
        link.setAttribute('data-description', link.getAttribute('data-en-description'));
      }
    });
  }
  root.classList.remove('en-pending');

  // Language menu: the button shows the current language; the list shows each language in its own name.
  var currentLang = english ? 'en' : 'ja';
  var names = { ja: '日本語', en: 'English' };
  var langButton = document.getElementById('lang');
  var langLabel = document.getElementById('lang-label');
  var langList = document.getElementById('lang-list');
  if (langButton && langLabel && langList) {
    langLabel.textContent = names[currentLang];
    langLabel.lang = currentLang;
    langButton.setAttribute('aria-label', (english ? 'Language: ' : '言語: ') + names[currentLang]);
    var languageLinks = langList.querySelectorAll('a[data-lang]');
    languageLinks.forEach(function (link) {
      var code = link.getAttribute('data-lang');
      if (code === currentLang) link.setAttribute('aria-current', 'true');
    });
    function updateLanguageLinks() {
      languageLinks.forEach(function (link) {
        var code = link.getAttribute('data-lang');
        link.href = location.pathname + (code === 'ja' ? '' : '?lang=' + code) + location.hash;
      });
    }
    updateLanguageLinks();
    window.addEventListener('hashchange', updateLanguageLinks);
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
  // Sidebar: mark the section being read, and the link just clicked.
  var navLinks = Array.prototype.slice.call(document.querySelectorAll('aside a[href^="#"]'));
  var sections = navLinks.map(function (link) { return document.getElementById(link.getAttribute('href').slice(1)); })
    .filter(function (section) { return section; });
  function markSection(id) {
    navLinks.forEach(function (link) {
      var on = link.getAttribute('href') === '#' + id;
      link.classList.toggle('on', on);
      if (on) link.setAttribute('aria-current', 'location'); else link.removeAttribute('aria-current');
    });
  }
  function sectionInView() {
    // At the very bottom the last section may be too short to reach the top of the window.
    if (window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 2) return sections[sections.length - 1].id;
    var current = sections[0].id;
    sections.forEach(function (section) { if (section.getBoundingClientRect().top <= 120) current = section.id; });
    return current;
  }
  var pending = false, clicked = null, settle = 0;
  function finishScroll() {
    clearTimeout(settle);
    clicked = null;
    markSection(sectionInView());
  }
  function scheduleSelection() {
    if (pending) return;
    pending = true;
    requestAnimationFrame(function () { pending = false; if (!clicked) markSection(sectionInView()); });
  }
  if (sections.length) {
    window.addEventListener('scroll', function () {
      // After a click, keep the chosen link while the smooth scroll passes other sections.
      if (clicked) {
        clearTimeout(settle);
        settle = setTimeout(finishScroll, 160);
        return;
      }
      scheduleSelection();
    }, { passive: true });
    navLinks.forEach(function (link) {
      link.addEventListener('click', function (event) {
        if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        clicked = link.getAttribute('href').slice(1);
        markSection(clicked);
        // If the page does not move (already there), release the hold anyway.
        clearTimeout(settle);
        settle = setTimeout(finishScroll, 600);
      });
    });
    window.addEventListener('scrollend', finishScroll);
    window.addEventListener('resize', scheduleSelection);
    window.addEventListener('load', scheduleSelection);
    window.addEventListener('hashchange', function () { if (!clicked) scheduleSelection(); });
    // User scrolling cancels the temporary selection hold from a link click.
    window.addEventListener('wheel', finishScroll, { passive: true });
    window.addEventListener('touchstart', finishScroll, { passive: true });
    window.addEventListener('keydown', function (event) {
      if (['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' '].indexOf(event.key) !== -1) finishScroll();
    });
    markSection(location.hash && document.getElementById(location.hash.slice(1)) ? location.hash.slice(1) : sectionInView());
  }
  if (window.GLightbox) GLightbox({ selector: '.glightbox', touchNavigation: true, loop: true });
})();
