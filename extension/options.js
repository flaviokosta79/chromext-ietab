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

// ---------- Câmeras sem plugin ----------

function readCam() {
  return {
    host: $('camHost').value.trim() || CAM_DEFAULTS.host,
    user: $('camUser').value.trim(),
    pass: $('camPass').value,
    channels: Math.min(64, Math.max(1, parseInt($('camChannels').value, 10) || CAM_DEFAULTS.channels)),
    interval: parseInt($('camInterval').value, 10) || CAM_DEFAULTS.interval,
    overlay: $('camOverlay').checked
  };
}

function camStatus(text, cls) {
  $('camStatus').textContent = text;
  $('camStatus').className = 'status ' + (cls || '');
}

// Endereços fora do padrão do manifesto pedem permissão; precisa ser chamado direto no clique (gesto do usuário).
function requestHost(cam) {
  return chrome.permissions.request({ origins: [toBase(cam.host) + '/*'] });
}

function renderOverlayState() {
  const on = $('camOverlay').checked;
  $('overlayState').innerHTML = (on
    ? '<b>Ligado:</b> o DVR abre no Chrome e o player da extensão aparece na página, sem plugin.'
    : '<b>Desligado:</b> o DVR abre no modo IE com o plugin real da Hikvision — reordenar câmeras, PTZ, gravar e áudio.')
    + ' Feche e reabra a aba do DVR para trocar de modo.';
}

loadCamConfig().then(cam => {
  $('camHost').value = cam.host;
  $('camUser').value = cam.user;
  $('camPass').value = cam.pass;
  $('camChannels').value = cam.channels;
  $('camInterval').value = String(cam.interval);
  $('camOverlay').checked = cam.overlay !== false;
  renderOverlayState();
});

// O interruptor salva sozinho (mescla só o campo overlay, sem mexer no que estiver sendo digitado).
$('camOverlay').onchange = async () => {
  const { cam = {} } = await chrome.storage.local.get('cam');
  cam.overlay = $('camOverlay').checked;
  await chrome.storage.local.set({ cam });
  renderOverlayState();
};

$('camSave').onclick = async () => {
  const cam = readCam();
  const granted = requestHost(cam);
  await chrome.storage.local.set({ cam });
  camStatus((await granted) ? 'Salvo.' : 'Salvo, mas sem permissão de acesso ao DVR.', (await granted) ? 'ok' : 'err');
};

$('camTest').onclick = async () => {
  const cam = readCam();
  const granted = requestHost(cam);
  if (!cam.user || !cam.pass) return camStatus('Preencha usuário e senha.', 'err');
  if (!(await granted)) return camStatus('Sem permissão de acesso ao DVR.', 'err');
  camStatus('Testando…');
  const client = new DigestClient(toBase(cam.host), cam.user, cam.pass);
  try {
    const res = await client.get('/ISAPI/Streaming/channels/101/picture');
    if (!res.ok) return camStatus('O DVR respondeu HTTP ' + res.status + ' ao pedir a imagem do canal 1.', 'err');
    const kb = Math.round((await res.blob()).size / 1024);
    camStatus(`Conectado: imagem do canal 1 recebida (${kb} KB).`, 'ok');
  } catch (e) {
    if (e instanceof AuthError) camStatus(e.message, 'err');
    else camStatus('Não foi possível conectar: ' + (e.name === 'AbortError' ? 'o DVR não respondeu' : e.message), 'err');
  }
};

// Diagnóstico: capacidades do DVR + amostra do vídeo por HTTP (/SDK/play), para desenvolver o player sem plugin.
function saveFile(name, data, type) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([data], { type }));
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 10000);
}

const hex = bytes => Array.from(bytes, b => b.toString(16).padStart(2, '0')).join(' ');

async function diagRequest(client, path, report) {
  try {
    const res = await client.get(path);
    report.push(`### GET ${path} -> HTTP ${res.status}`);
    res.headers.forEach((v, k) => report.push(`${k}: ${v}`));
    return res;
  } catch (e) {
    report.push(`### GET ${path} -> ERRO ${e.name}: ${e.message}`);
    if (e instanceof AuthError) throw e; // não insiste: cada recusa conta para o bloqueio do DVR
    return null;
  }
}

async function readSample(res, maxBytes, maxMs) {
  const reader = res.body.getReader();
  const chunks = [];
  let total = 0;
  const t0 = Date.now();
  const timeline = [];
  while (total < maxBytes && Date.now() - t0 < maxMs) {
    const r = await Promise.race([reader.read(), new Promise(ok => setTimeout(() => ok({ timeout: true }), maxMs))]);
    if (r.timeout || r.done) break;
    chunks.push(r.value);
    total += r.value.length;
    if (timeline.length < 40) timeline.push(`+${Date.now() - t0}ms ${r.value.length}B`);
  }
  reader.cancel().catch(() => {});
  const all = new Uint8Array(total);
  let off = 0;
  for (const c of chunks) { all.set(c, off); off += c.length; }
  return { all, ms: Date.now() - t0, timeline };
}

$('camDiag').onclick = async () => {
  const cam = readCam();
  const granted = requestHost(cam);
  const out = $('diagStatus');
  if (!cam.user || !cam.pass) { out.textContent = 'Preencha usuário e senha.'; out.className = 'status err'; return; }
  if (!(await granted)) { out.textContent = 'Sem permissão de acesso ao DVR.'; out.className = 'status err'; return; }
  out.textContent = 'Coletando… (cerca de 15 s)';
  out.className = 'status';
  const base = toBase(cam.host);
  const report = [`HikTab diagnóstico ${new Date().toISOString()}`, `DVR ${base}`, navigator.userAgent, ''];
  const client = new DigestClient(base, cam.user, cam.pass);
  try {
    for (const path of ['/ISAPI/System/deviceInfo', '/SDK/capabilities', '/ISAPI/Streaming/channels/101', '/ISAPI/Streaming/channels/102']) {
      const res = await diagRequest(client, path, report);
      if (res) report.push('', (await res.text()).slice(0, 6000), '');
    }
    let sample = null;
    for (const path of ['/SDK/play/102/004', '/SDK/play/101/004']) {
      const res = await diagRequest(client, path, report);
      if (!res || !res.ok || !res.body) { if (res) report.push((await res.text()).slice(0, 2000)); continue; }
      const s = await readSample(res, 4 * 1024 * 1024, 6000);
      report.push(`bytes=${s.all.length} em ${s.ms}ms`, 'chegadas: ' + s.timeline.join(', '), 'início (hex): ' + hex(s.all.subarray(0, 512)), '');
      if (!sample) sample = s.all;
    }
    // WebCodecs disponível neste Chrome?
    for (const codec of ['avc1.42E01E', 'avc1.4D401F', 'avc1.640028', 'hvc1.1.6.L93.B0', 'hev1.1.6.L120.90']) {
      try {
        const r = await VideoDecoder.isConfigSupported({ codec });
        report.push(`VideoDecoder ${codec}: ${r.supported}`);
      } catch (e) { report.push(`VideoDecoder ${codec}: erro ${e.message}`); }
    }
    saveFile('hiktab-diag.txt', report.join('\n'), 'text/plain');
    if (sample) saveFile('hiktab-diag.bin', sample, 'application/octet-stream');
    out.textContent = sample ? 'Pronto: arquivos salvos em Downloads.' : 'Relatório salvo em Downloads (sem amostra de vídeo).';
    out.className = 'status ok';
  } catch (e) {
    saveFile('hiktab-diag.txt', report.join('\n'), 'text/plain');
    out.textContent = (e instanceof AuthError ? '' : 'Falhou: ') + e.message;
    out.className = 'status err';
  }
};

$('camLive').onclick = () => chrome.tabs.create({ url: chrome.runtime.getURL('video.html') });
$('camOpen').onclick = () => chrome.tabs.create({ url: chrome.runtime.getURL('cameras.html') });

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
