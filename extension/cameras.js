// HikTab - mosaico de câmeras sem plugin: busca fotos de cada canal pela API ISAPI do DVR e atualiza em sequência.
// Mosaico usa o substream (xx02, leve); a câmera ampliada usa o stream principal (xx01, alta resolução).

const $ = id => document.getElementById(id);
const grid = $('grid'), msg = $('msg');
const STALE_MS = 5000;

let cfg = null;
let base = '';
let tiles = [];
let focused = null;
let running = false;

const sleep = ms => new Promise(r => setTimeout(r, ms));

function showMessage(text, buttons = []) {
  msg.textContent = '';
  for (const line of text.split('\n')) {
    const p = document.createElement('div');
    p.textContent = line;
    msg.appendChild(p);
  }
  if (buttons.length) {
    const row = document.createElement('div');
    for (const [label, action] of buttons) {
      const b = document.createElement('button');
      b.textContent = label;
      b.onclick = action;
      row.appendChild(b);
    }
    msg.appendChild(row);
  }
  msg.hidden = false;
}

const openOptions = () => chrome.runtime.openOptionsPage();
const reload = () => location.reload();

// Escolhe o número de colunas que deixa cada imagem 16:9 o maior possível na área disponível.
function layout() {
  const n = focused ? 1 : tiles.length;
  if (!n) return;
  const W = grid.clientWidth, H = grid.clientHeight;
  let bestCols = 1, bestWidth = 0;
  for (let cols = 1; cols <= n; cols++) {
    const rows = Math.ceil(n / cols);
    const width = Math.min(W / cols, (H / rows) * 16 / 9);
    if (width > bestWidth) { bestWidth = width; bestCols = cols; }
  }
  grid.style.gridTemplateColumns = `repeat(${bestCols}, 1fr)`;
  grid.style.gridTemplateRows = `repeat(${Math.ceil(n / bestCols)}, 1fr)`;
}

function setFocus(t) {
  if (focused) focused.el.classList.remove('focus');
  focused = t;
  if (t) t.el.classList.add('focus');
  document.body.classList.toggle('single', !!t);
  $('back').hidden = !t;
  layout();
}

function makeTile(ch) {
  const el = document.createElement('div');
  el.className = 'tile';
  const img = document.createElement('img');
  img.alt = '';
  const label = document.createElement('div');
  label.className = 'label';
  label.textContent = 'Câmera ' + ch;
  const state = document.createElement('div');
  state.className = 'state';
  state.textContent = 'Carregando…';
  el.append(img, label, state);
  // Um cliente Digest por canal: cada um mantém o próprio nonce e contador, sem conflito entre requisições paralelas.
  const t = { ch, el, img, state, client: new DigestClient(base, cfg.user, cfg.pass), sub: true, last: 0, url: null };
  el.onclick = () => setFocus(focused === t ? null : t);
  return t;
}

async function fetchFrame(t) {
  const stream = focused === t || !t.sub ? 1 : 2;
  const res = await t.client.get(`/ISAPI/Streaming/channels/${t.ch * 100 + stream}/picture`);
  const isImage = /image/i.test(res.headers.get('Content-Type') || '');
  if (!res.ok || !isImage) {
    // Alguns DVRs não geram foto do substream: passa a usar o principal neste canal.
    if (stream === 2 && (res.ok || [400, 403, 404, 405, 501].includes(res.status))) {
      t.sub = false;
      return fetchFrame(t);
    }
    throw new Error(res.ok ? 'resposta sem imagem' : 'HTTP ' + res.status);
  }
  const url = URL.createObjectURL(await res.blob());
  // Decodifica antes de trocar, para a imagem não piscar.
  const pre = new Image();
  pre.src = url;
  try { await pre.decode(); } catch { URL.revokeObjectURL(url); throw new Error('imagem inválida'); }
  t.img.src = url;
  if (t.url) URL.revokeObjectURL(t.url);
  t.url = url;
}

async function loop(t) {
  let fails = 0;
  while (running) {
    if (document.hidden || (focused && focused !== t)) { await sleep(300); continue; }
    const started = Date.now();
    try {
      await fetchFrame(t);
      fails = 0;
      t.last = Date.now();
      t.state.textContent = '';
    } catch (e) {
      if (e instanceof AuthError) return authFailed(e);
      fails++;
      if (!t.last) t.state.textContent = 'Sem imagem (' + (e.name === 'AbortError' ? 'tempo esgotado' : e.message) + ')';
      await sleep(Math.min(10000, 1000 * fails));
      continue;
    }
    const interval = focused === t ? Math.min(cfg.interval, 500) : cfg.interval;
    const wait = interval - (Date.now() - started);
    if (wait > 0) await sleep(wait);
  }
}

function authFailed(e) {
  if (!running) return;
  running = false;
  showMessage(
    e.message + '\nAs tentativas foram interrompidas para o DVR não bloquear o usuário.\nConfira usuário e senha nas opções.',
    [['Configurar', openOptions], ['Tentar novamente', reload]]
  );
}

async function ensurePermission() {
  const origins = [base + '/*'];
  if (await chrome.permissions.contains({ origins })) return true;
  showMessage(
    `A extensão precisa de permissão para acessar ${base}.`,
    [['Permitir acesso', () => chrome.permissions.request({ origins }, ok => ok && reload())], ['Configurar', openOptions]]
  );
  return false;
}

async function start() {
  cfg = await loadCamConfig();
  base = toBase(cfg.host);
  $('title').textContent = 'Câmeras — ' + cfg.host;

  if (!cfg.user || !cfg.pass) {
    showMessage('Cadastre o usuário e a senha do DVR nas opções da extensão.', [['Configurar', openOptions]]);
    return;
  }
  if (!(await ensurePermission())) return;

  // Confere o login uma única vez antes de abrir todos os canais (senha errada em 8 canais bloquearia o usuário).
  showMessage('Conectando ao DVR ' + cfg.host + '…');
  try {
    const res = await new DigestClient(base, cfg.user, cfg.pass).get('/ISAPI/System/deviceInfo');
    if (res.ok) {
      const xml = new DOMParser().parseFromString(await res.text(), 'text/xml');
      const name = xml.querySelector('deviceName');
      if (name && name.textContent) $('title').textContent = name.textContent + ' — ' + cfg.host;
    }
  } catch (e) {
    if (e instanceof AuthError) {
      showMessage(e.message + '\nConfira usuário e senha nas opções.', [['Configurar', openOptions], ['Tentar novamente', reload]]);
    } else {
      const why = e.name === 'AbortError' ? 'o DVR não respondeu' : e.message;
      showMessage(`Não foi possível conectar ao DVR em ${base} (${why}).`, [['Tentar novamente', reload], ['Configurar', openOptions]]);
    }
    return;
  }
  msg.hidden = true;

  for (let ch = 1; ch <= cfg.channels; ch++) {
    const t = makeTile(ch);
    tiles.push(t);
    grid.appendChild(t.el);
  }
  $('status').textContent = `${cfg.channels} canais · atualiza a cada ${cfg.interval / 1000} s · clique numa câmera para ampliar`;
  layout();
  running = true;
  tiles.forEach(loop);

  setInterval(() => {
    const now = Date.now();
    for (const t of tiles) t.el.classList.toggle('stale', !!t.last && now - t.last > STALE_MS);
  }, 1000);
}

$('back').onclick = () => setFocus(null);
$('options').onclick = openOptions;
$('ie').onclick = () => {
  location.href = chrome.runtime.getURL('container.html') + '#' + encodeURIComponent(base || toBase(CAM_DEFAULTS.host));
};

document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && focused) setFocus(null);
  const n = parseInt(e.key, 10);
  if (n >= 1 && n <= tiles.length) setFocus(focused === tiles[n - 1] ? null : tiles[n - 1]);
});
window.addEventListener('resize', layout);

start();
