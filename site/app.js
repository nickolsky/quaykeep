// Download page: highlights the visitor's OS and fills in the latest version and file sizes from the GitHub API.
// Without JavaScript (or when the API is unreachable) the links still work: they point at releases/latest/download.
(function () {
  var repo = 'nickolsky/quaykeep';

  function detectOs() {
    var p = ((navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || '').toLowerCase();
    var ua = navigator.userAgent.toLowerCase();
    if (p.indexOf('win') >= 0 || ua.indexOf('windows') >= 0) return 'win';
    if (p.indexOf('mac') >= 0 || ua.indexOf('mac os') >= 0) return 'mac';
    if (p.indexOf('linux') >= 0 || ua.indexOf('linux') >= 0) return 'linux';
    return null;
  }

  var os = detectOs();
  if (os) {
    var card = document.querySelector('.dl[data-os="' + os + '"]');
    if (card) {
      card.classList.add('primary');
      card.parentNode.insertBefore(card, card.parentNode.firstChild);
    }
  }

  function mb(bytes) { return (bytes / 1048576).toFixed(0) + ' MB'; }

  fetch('https://api.github.com/repos/' + repo + '/releases/latest', { headers: { Accept: 'application/vnd.github+json' } })
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(function (rel) {
      if (!rel || !rel.tag_name) return;
      var version = rel.tag_name.replace(/^v/, '');
      document.querySelectorAll('[data-version]').forEach(function (el) { el.textContent = version; });
      var date = rel.published_at ? rel.published_at.slice(0, 10) : '';
      document.querySelectorAll('[data-date]').forEach(function (el) { el.textContent = date; });
      var sizes = {};
      (rel.assets || []).forEach(function (a) { sizes[a.name] = a.size; });
      document.querySelectorAll('[data-asset]').forEach(function (el) {
        var size = sizes[el.getAttribute('data-asset')];
        var slot = el.querySelector('.size');
        if (size && slot) slot.textContent = mb(size);
      });
    })
    .catch(function () { /* offline or rate-limited: keep the static text */ });
})();
