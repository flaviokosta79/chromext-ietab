// HikTab - vídeo ao vivo sem plugin do navegador.
// A extensão pede ao componente nativo (HikTabHost) o fluxo RTSP de cada canal; o host devolve o H.264
// já desempacotado e aqui o WebCodecs decodifica e desenha num canvas. Mosaico usa substream (xx02);
// a câmera ampliada usa o stream principal (xx01, alta resolução), como faz o plugin original.

const HOST = 'com.hiktab.host';
const $ = id => document.getElementById(id);
const grid = $('grid'), msg = $('msg');
const STALE_MS = 5000;

let cfg = null;
let rtspBase = '';
let port = null;
let tiles = [];
let byId = {};
let focused = null;

// Modo embutido: o player roda dentro de um <iframe> colado na página do DVR, então esconde a própria barra.
const EMBED = new URLSearchParams(location.search).get('embed') === '1';
if (EMBED) {
  document.body.classList.add('embed');
  const pb = $('plugin');
  if (pb) {
    pb.hidden = false;
    // Troca esta aba para o modo IE com o plugin real da Hikvision (sem alterar a opção salva).
    pb.onclick = () => chrome.runtime.sendMessage({ type: 'openInIE' });
  }
}

function showMessage(text, buttons) {
  msg.textContent = '';
  for (const line of text.split('\n')) {
    const p = document.createElement('div');
    p.textContent = line;
    msg.appendChild(p);
  }
  if (buttons) {
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
const b64ToBytes = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));

// Colunas que deixam cada imagem 16:9 o maior possível na área.
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

function channelUrl(ch, main) {
  return `${rtspBase}/Streaming/Channels/${ch * 100 + (main ? 1 : 2)}`;
}

function openStream(t, main) {
  t.main = main;
  t.epoch = (t.epoch || 0) + 1; // cada abertura marca uma geração; mensagens de gerações antigas são ignoradas
  closeDecoder(t);
  clearCanvas(t); // não deixa o quadro anterior (ou esticado do outro stream) congelado durante a troca
  if (port) port.postMessage({ type: 'rtsp.open', id: t.id, url: channelUrl(ch_of(t), main), user: cfg.user, pass: cfg.pass });
}

const ch_of = t => t.ch;

function closeDecoder(t) {
  if (t.dec) { try { t.dec.close(); } catch (e) {} t.dec = null; }
  t.configured = false;
}

// Pede ao host (que pede ao DVR) um quadro-chave imediato — recuperação rápida sem esperar o GOP longo.
// Com limite para não repetir o pedido a cada quadro.
function requestKey(t) {
  const now = Date.now();
  if (port && now - (t.lastKeyReq || 0) > 1500) {
    t.lastKeyReq = now;
    port.postMessage({ type: 'rtsp.key', id: t.id });
  }
}

// Apaga o canvas (evita que um quadro antigo/verde fique congelado na tela durante a ressincronização).
function clearCanvas(t) {
  try { t.ctx.clearRect(0, 0, t.canvas.width, t.canvas.height); } catch (e) {}
}

function setFocus(t) {
  if (focused === t) return;
  const prev = focused;
  focused = t;
  if (prev) { prev.el.classList.remove('focus'); openStream(prev, false); }
  document.body.classList.toggle('single', !!t);
  $('back').hidden = !t;
  if (t) { t.el.classList.add('focus'); openStream(t, true); } // amplia -> stream principal
  layout();
}

function makeTile(ch) {
  const el = document.createElement('div');
  el.className = 'tile';
  const canvas = document.createElement('canvas');
  canvas.width = 16; canvas.height = 9;
  const label = document.createElement('div');
  label.className = 'label';
  label.textContent = 'Câmera ' + ch;
  const state = document.createElement('div');
  state.className = 'state';
  state.textContent = 'Conectando…';
  el.append(canvas, label, state);
  const t = { ch, id: 'c' + ch, el, canvas, ctx: canvas.getContext('2d'), label, state, dec: null, configured: false, main: false, last: 0 };
  el.onclick = () => setFocus(focused === t ? null : t);
  return t;
}

function configureDecoder(t, codec) {
  closeDecoder(t);
  const dec = new VideoDecoder({
    output: frame => {
      if (t.canvas.width !== frame.displayWidth || t.canvas.height !== frame.displayHeight) {
        t.canvas.width = frame.displayWidth;
        t.canvas.height = frame.displayHeight;
      }
      try { t.ctx.drawImage(frame, 0, 0); } catch (e) {}
      frame.close();
      t.last = Date.now();
      if (t.state.textContent) t.state.textContent = '';
    },
    error: e => {
      // Decodificador quebrou (ex.: dado corrompido): recria, limpa a tela e pede um quadro-chave novo,
      // sem derrubar o fluxo RTSP. Com um limite para não entrar em laço se o erro for contínuo.
      const now = Date.now();
      clearCanvas(t);
      requestKey(t);
      if (t.codec && now - (t.lastReconfig || 0) > 1000) { t.lastReconfig = now; configureDecoder(t, t.codec); }
      else t.waitingKey = true;
    }
  });
  try {
    // Software é mais estável com vários canais ao mesmo tempo e evita a "tela verde" de alguns
    // decodificadores de hardware quando o fluxo oscila.
    dec.configure({ codec, optimizeForLatency: true, hardwareAcceleration: 'prefer-software' });
    t.dec = dec;
    t.codec = codec;
    t.configured = true;
    t.waitingKey = true;
  } catch (e) {
    try { dec.configure({ codec, optimizeForLatency: true }); t.dec = dec; t.codec = codec; t.configured = true; t.waitingKey = true; }
    catch (e2) { t.state.textContent = 'Codec não suportado (' + codec + ')'; }
  }
}

function onFrame(t, key, bytes, ts) {
  if (!t.configured || !t.dec) return;
  // Só começa (e recomeça) num quadro-chave: entregar um quadro intermediário sem o quadro de
  // referência anterior é o que produz a tela verde. Por isso, se a fila atrasar, descartamos até
  // o próximo quadro-chave em vez de descartar quadros no meio.
  if (t.waitingKey) { if (!key) return; t.waitingKey = false; }
  // Atrasou: descarta até o próximo quadro-chave (nunca entrega quadro sem referência -> evita tela verde)
  // e pede um quadro-chave imediato para recuperar rápido (o GOP pode ser longo).
  if (!key && t.dec.decodeQueueSize > 4) { t.waitingKey = true; requestKey(t); return; }
  try {
    t.dec.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp: ts || 0, data: bytes }));
  } catch (e) {
    t.waitingKey = true;
  }
}

function onHostMessage(m) {
  const t = byId[m.id];
  switch (m.type) {
    case 'rtsp.sdp':
      if (t) configureDecoder(t, m.codec || 'avc1.42E01E');
      break;
    case 'rtsp.frame':
      if (t) onFrame(t, !!m.key, b64ToBytes(m.data), m.ts);
      break;
    case 'rtsp.error':
      // Falha real do fluxo (ex.: sem sinal). Reconecta, mas só se a câmera não tiver trocado de stream
      // nesse meio-tempo (a geração ainda é a mesma) — assim um erro atrasado não atropela uma troca nova.
      if (t) {
        t.configured = false;
        if (!t.last) t.state.textContent = 'Reconectando… (' + (m.message || 'sem resposta') + ')';
        const epoch = t.epoch;
        setTimeout(() => { if (t.epoch === epoch) openStream(t, t.main); }, 2000);
      }
      break;
  }
}

function hostFailed(detail) {
  showMessage(
    'O componente HikTab Host não está instalado ou foi encerrado.\n' +
    'Execute o arquivo install.cmd (na pasta do HikTab) e tente novamente.\n' +
    (detail ? 'Detalhe: ' + detail : ''),
    [['Tentar novamente', reload], ['Modo fotos (sem componente)', () => location.href = chrome.runtime.getURL('cameras.html')]]
  );
}

async function start() {
  cfg = await loadCamConfig();
  const hostOnly = cfg.host.replace(/^[a-z]+:\/\//i, '').replace(/[:/].*$/, '');
  rtspBase = 'rtsp://' + hostOnly + ':554';
  $('title').textContent = 'Câmeras ao vivo — ' + cfg.host;

  if (typeof VideoDecoder === 'undefined') {
    showMessage('Este navegador não tem suporte a vídeo por WebCodecs. Use o modo fotos.',
      [['Modo fotos', () => location.href = chrome.runtime.getURL('cameras.html')]]);
    return;
  }
  if (!cfg.user || !cfg.pass) {
    showMessage('Cadastre o usuário e a senha do DVR nas opções da extensão.', [['Configurar', openOptions]]);
    return;
  }

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

  for (let ch = 1; ch <= cfg.channels; ch++) {
    const t = makeTile(ch);
    tiles.push(t);
    byId[t.id] = t;
    grid.appendChild(t.el);
    openStream(t, false);
  }
  $('status').textContent = `${cfg.channels} canais · clique numa câmera (ou tecle 1–${Math.min(9, cfg.channels)}) para ampliar`;
  layout();

  setInterval(() => {
    const now = Date.now();
    for (const t of tiles) t.el.classList.toggle('stale', !!t.last && now - t.last > STALE_MS);
  }, 1000);
}

$('back').onclick = () => setFocus(null);
$('options').onclick = openOptions;
$('fotos').onclick = () => location.href = chrome.runtime.getURL('cameras.html');

document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && focused) setFocus(null);
  const n = parseInt(e.key, 10);
  if (n >= 1 && n <= tiles.length) setFocus(focused === tiles[n - 1] ? null : tiles[n - 1]);
});
window.addEventListener('resize', layout);

start();
