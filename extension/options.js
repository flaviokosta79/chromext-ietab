const $ = id => document.getElementById(id);

function readSites() {
  return $('sites').value.split('\n').map(s => s.trim()).filter(Boolean);
}

chrome.storage.sync.get('sites', ({ sites = [] }) => {
  $('sites').value = sites.join('\n');
});

$('save').onclick = () => {
  chrome.storage.sync.set({ sites: readSites() }, () => {
    $('saved').textContent = 'Salvo.';
    $('saved').className = 'status ok';
    setTimeout(() => { $('saved').textContent = ''; }, 2500);
  });
};

$('open').onclick = () => {
  // "http://10.0.45.117/*" -> "http://10.0.45.117"; ignora curingas no endereço (ex.: 192.168.0.*)
  const first = readSites().map(s => s.replace(/\/?\*$/, '')).find(s => s && !s.includes('*'));
  if (!first) return;
  const url = /^[a-z]+:\/\//i.test(first) ? first : 'http://' + first;
  chrome.tabs.create({ url: chrome.runtime.getURL('container.html') + '#' + encodeURIComponent(url) });
};

$('test').onclick = () => {
  const out = $('hoststatus');
  out.textContent = 'Testando…';
  out.className = 'status';
  let port;
  try {
    port = chrome.runtime.connectNative('com.hiktab.host');
  } catch (e) {
    out.textContent = 'Falhou: ' + e.message;
    out.className = 'status err';
    return;
  }
  let failed = false;
  port.onDisconnect.addListener(() => {
    failed = true;
    const err = chrome.runtime.lastError;
    out.textContent = 'Falhou: ' + (err ? err.message : 'o componente fechou');
    out.className = 'status err';
  });
  setTimeout(() => {
    if (failed) return;
    port.disconnect();
    out.textContent = 'Componente instalado e funcionando.';
    out.className = 'status ok';
  }, 1500);
};
