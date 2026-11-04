(function () {
  var root = document.documentElement;
  var button = document.getElementById('theme');
  function current() {
    return root.dataset.theme || (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');
  }
  function label() {
    button.setAttribute('aria-label', current() === 'dark' ? 'ライトモードに切り替え' : 'ダークモードに切り替え');
  }
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
