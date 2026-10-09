// HikTab - página da aba: conversa com o host nativo e informa onde ele deve se desenhar.

const HOST = 'com.hiktab.host';
const $ = id => document.getElementById(id);
const view = $('view'), msg = $('msg'), addr = $('addr');

let port = null;
let tabId = null, windowId = null;
let zoom = 1;
let lastBounds = '';
let currentUrl = '';
let stateBeforeFullscreen = 'maximized';

const containerUrl = url => chrome.runtime.getURL('container.html') + '#' + encodeURIComponent(url);

function initialUrl() {
  const h = location.hash.slice(1);
  try { return decodeURIComponent(h); } catch { return h; }
}

function normalize(url) {
  url = (url || '').trim();
  if (url && !/^[a-z][a-z0-9+.-]*:/i.test(url)) url = 'http://' + url;
  return url;
}

function send(m) {
  if (port) port.postMessage(m);
}

function showMessage(text, withRetry) {
  msg.textContent = '';
  for (const line of text.split('\n')) {
    const p = document.createElement('div');
    p.textContent = line;
    msg.appendChild(p);
  }
  if (withRetry) {
    const b = document.createElement('button');
    b.textContent = 'Tentar novamente';
    b.onclick = () => location.reload();
    msg.appendChild(b);
  }
  msg.hidden = false;
}

// Retângulo da área #view em pixels físicos da tela.
// screenX/outerWidth estão em DIPs; innerWidth e getBoundingClientRect em px CSS (afetados pelo zoom).
function measure() {
  const r = view.getBoundingClientRect();
  const scale = window.devicePixelRatio / zoom;
  const innerW = window.innerWidth * zoom;
  const innerH = window.innerHeight * zoom;
  const border = Math.max(0, (window.outerWidth - innerW) / 2);
  const left = window.screenX + border;
  const top = window.screenY + window.outerHeight - border - innerH;
  return {
    x: Math.round((left + r.left * zoom) * scale),
    y: Math.round((top + r.top * zoom) * scale),
    w: Math.round(r.width * zoom * scale),
    h: Math.round(r.height * zoom * scale)
  };
}

function sync(force) {
  if (!port || document.hidden) return;
  const b = measure();
  const key = JSON.stringify(b);
  if (force || key !== lastBounds) {
    lastBounds = key;
    send(Object.assign({ type: 'bounds' }, b));
  }
}

function navigate(url) {
  url = normalize(url);
  if (!url) return;
  currentUrl = url;
  addr.value = url;
  send({ type: 'navigate', url });
}

function onKey(action) {
  if (action === 'newtab') {
    chrome.tabs.create({ windowId });
  } else if (action === 'closetab') {
    chrome.tabs.remove(tabId);
  } else if (action === 'nexttab' || action === 'prevtab') {
    chrome.tabs.query({ windowId }, tabs => {
      const i = tabs.findIndex(t => t.id === tabId);
      const n = tabs.length;
      const j = (i + (action === 'nexttab' ? 1 : -1) + n) % n;
      chrome.tabs.update(tabs[j].id, { active: true });
    });
  } else if (action === 'fullscreen') {
    chrome.windows.get(windowId, w => {
      if (w.state === 'fullscreen') {
        chrome.windows.update(windowId, { state: stateBeforeFullscreen });
      } else {
        stateBeforeFullscreen = w.state === 'minimized' ? 'maximized' : w.state;
        chrome.windows.update(windowId, { state: 'fullscreen' });
      }
    });
  }
}

function onHostMessage(m) {
  switch (m.type) {
    case 'title':
      document.title = m.title || currentUrl || 'HikTab';
      break;
    case 'nav':
      msg.hidden = true;
      if (m.url) {
        currentUrl = m.url;
        if (document.activeElement !== addr) addr.value = m.url;
        history.replaceState(null, '', '#' + encodeURIComponent(m.url));
      }
      $('back').disabled = !m.back;
      $('forward').disabled = !m.forward;
      break;
    case 'newwindow':
      chrome.tabs.get(tabId, t => chrome.tabs.create({ url: containerUrl(m.url), windowId, index: t.index + 1 }));
      break;
    case 'close':
      chrome.tabs.remove(tabId);
      break;
    case 'key':
      onKey(m.action);
      break;
  }
}

function start() {
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch (e) {
    hostFailed(e.message);
    return;
  }
  port.onMessage.addListener(onHostMessage);
  port.onDisconnect.addListener(() => {
    const err = chrome.runtime.lastError;
    port = null;
    hostFailed(err && err.message);
  });

  const url = normalize(initialUrl());
  if (url) {
    navigate(url);
  } else {
    showMessage('Digite o endereço na barra acima e pressione Enter.');
    addr.focus();
  }
  send({ type: 'visible', value: !document.hidden });
  sync(true);
}

function hostFailed(detail) {
  $('back').disabled = $('forward').disabled = true;
  showMessage(
    'O componente HikTab Host não está instalado ou foi encerrado.\n' +
    'Execute o arquivo install.cmd (na pasta do HikTab) e tente novamente.\n' +
    (detail ? 'Detalhe: ' + detail : ''),
    true
  );
}

$('back').onclick = () => send({ type: 'back' });
$('forward').onclick = () => send({ type: 'forward' });
$('reload').onclick = () => send({ type: 'refresh' });
$('chrome').onclick = () => {
  if (currentUrl) chrome.runtime.sendMessage({ type: 'openInChrome', url: currentUrl });
};
$('go').onsubmit = e => {
  e.preventDefault();
  if (!port) {
    history.replaceState(null, '', '#' + encodeURIComponent(normalize(addr.value)));
    location.reload();
    return;
  }
  msg.hidden = true;
  navigate(addr.value);
  addr.blur();
};

document.addEventListener('visibilitychange', () => {
  send({ type: 'visible', value: !document.hidden });
  if (!document.hidden) sync(true);
});
window.addEventListener('resize', () => sync());
setInterval(sync, 100);

chrome.tabs.onZoomChange.addListener(info => {
  if (info.tabId === tabId) {
    zoom = info.newZoomFactor;
    sync(true);
  }
});
chrome.tabs.onAttached.addListener((id, info) => {
  if (id === tabId) {
    windowId = info.newWindowId;
    send({ type: 'reparent' });
  }
});

chrome.tabs.getCurrent(tab => {
  tabId = tab.id;
  windowId = tab.windowId;
  chrome.tabs.getZoom(tabId, z => {
    zoom = z || 1;
    start();
  });
});
