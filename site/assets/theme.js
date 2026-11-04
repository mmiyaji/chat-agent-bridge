// Applies the saved light/dark choice before the page draws. Without one, the OS setting decides.
(function () {
  var root = document.documentElement;
  try {
    var saved = localStorage.getItem('cab-theme');
    if (saved === 'light' || saved === 'dark') root.dataset.theme = saved;
  } catch (e) { /* storage may be unavailable */ }
})();
