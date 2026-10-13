// HikTab - service worker: redirecionamento automático, botão da barra e menu de contexto.

const CONTAINER = chrome.runtime.getURL('container.html');
const containerUrl = url => CONTAINER + '#' + encodeURIComponent(url || '');
const isWeb = url => /^(https?|ftp|file):/i.test(url || '');

// "192.168.0.10" -> *://192.168.0.10/* ; aceita curingas (*) e URLs completas.
function toRegex(pattern) {
  let p = (pattern || '').trim();
  if (!p || p.startsWith('#')) return null;
  if (!p.includes('://')) p = '*://' + p;
  if (!/:\/\/[^/]*\//.test(p)) p += '/*';
  const body = p.split('*').map(s => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*');
  return new RegExp('^' + body + '$', 'i');
}

async function matchesRules(url) {
  const { sites = [] } = await chrome.storage.sync.get('sites');
  return sites.some(s => {
    const re = toRegex(s);
    return re && re.test(url);
  });
}

// Configuração das câmeras (endereço do DVR + interruptor da sobreposição ao vivo).
async function camInfo() {
  const { cam = {} } = await chrome.storage.local.get('cam');
  const host = (cam.host || '').replace(/^[a-z]+:\/\//i, '').replace(/[:/].*$/, '');
  return { host, overlay: cam.overlay !== false };
}

function hostOf(url) {
  try { return new URL(url).hostname; } catch (e) { return ''; }
}

// Abas em que o usuário pediu "Abrir no Chrome" não são redirecionadas de novo.
async function getBypass() {
  const { bypass = {} } = await chrome.storage.session.get('bypass');
  return bypass;
}

async function setBypass(tabId, value) {
  const bypass = await getBypass();
  if (value) bypass[tabId] = true;
  else delete bypass[tabId];
  await chrome.storage.session.set({ bypass });
}

async function openInChrome(tabId, url) {
  await setBypass(tabId, true);
  chrome.tabs.update(tabId, { url });
}

async function openInIE(tabId, url) {
  await setBypass(tabId, false);
  chrome.tabs.update(tabId, { url: containerUrl(url) });
}

chrome.webNavigation.onBeforeNavigate.addListener(async details => {
  if (details.frameId !== 0 || details.tabId < 0 || !/^https?:/i.test(details.url)) return;
  if ((await getBypass())[details.tabId]) return;

  // O endereço do DVR é decidido pelo interruptor da sobreposição, não pela lista de sites:
  // sobreposição ligada -> abre no Chrome (o content.js injeta o player ao vivo);
  // sobreposição desligada -> abre no modo IE, com o plugin real (reordenar câmeras, PTZ, etc.).
  const cam = await camInfo();
  if (cam.host && hostOf(details.url) === cam.host) {
    if (!cam.overlay) chrome.tabs.update(details.tabId, { url: containerUrl(details.url) }).catch(() => {});
    return;
  }

  if (!(await matchesRules(details.url))) return;
  chrome.tabs.update(details.tabId, { url: containerUrl(details.url) }).catch(() => {});
});

chrome.tabs.onRemoved.addListener(tabId => setBypass(tabId, false));

chrome.action.onClicked.addListener(tab => {
  if (tab.url && tab.url.startsWith(CONTAINER)) {
    const url = decodeURIComponent(new URL(tab.url).hash.slice(1));
    if (url) openInChrome(tab.id, url);
  } else if (isWeb(tab.url)) {
    openInIE(tab.id, tab.url);
  } else {
    chrome.tabs.update(tab.id, { url: CONTAINER });
  }
});

chrome.runtime.onInstalled.addListener(async details => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: 'live', title: 'Câmeras ao vivo (vídeo)', contexts: ['action'] });
    chrome.contextMenus.create({ id: 'cameras', title: 'Câmeras em fotos (sem componente)', contexts: ['action'] });
    chrome.contextMenus.create({ id: 'link', title: 'Abrir link no modo IE (HikTab)', contexts: ['link'] });
    chrome.contextMenus.create({
      id: 'page',
      title: 'Abrir esta página no modo IE (HikTab)',
      contexts: ['page'],
      documentUrlPatterns: ['http://*/*', 'https://*/*', 'file://*/*']
    });
  });
  if (details.reason === 'install') chrome.runtime.openOptionsPage();
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === 'live') {
    chrome.tabs.create({ url: chrome.runtime.getURL('video.html') });
  } else if (info.menuItemId === 'cameras') {
    chrome.tabs.create({ url: chrome.runtime.getURL('cameras.html') });
  } else if (info.menuItemId === 'link') {
    chrome.tabs.create({ url: containerUrl(info.linkUrl), index: tab ? tab.index + 1 : undefined });
  } else if (info.menuItemId === 'page' && tab) {
    openInIE(tab.id, info.pageUrl);
  }
});

chrome.runtime.onMessage.addListener((msg, sender) => {
  if (msg.type === 'openInChrome' && sender.tab) openInChrome(sender.tab.id, msg.url);
  // Mensagem vinda do player embutido (iframe) na página do DVR: abre a aba no modo IE (plugin real).
  else if (msg.type === 'openInIE' && sender.tab) openInIE(sender.tab.id, msg.url || sender.tab.url);
});
