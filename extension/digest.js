// HikTab - cliente HTTP com autenticação Digest (MD5), usada pela API ISAPI dos DVRs Hikvision.
// O fetch do navegador não faz Digest sozinho sem abrir a janela de login, então o cabeçalho é montado aqui.

function md5(text) {
  const bytes = new TextEncoder().encode(text);
  const len = bytes.length;
  const blocks = ((len + 8) >> 6) + 1;
  const w = new Uint32Array(blocks * 16);
  for (let i = 0; i < len; i++) w[i >> 2] |= bytes[i] << ((i % 4) * 8);
  w[len >> 2] |= 0x80 << ((len % 4) * 8);
  w[blocks * 16 - 2] = len * 8;

  const S = [7, 12, 17, 22, 5, 9, 14, 20, 4, 11, 16, 23, 6, 10, 15, 21];
  const K = new Uint32Array(64);
  for (let i = 0; i < 64; i++) K[i] = Math.floor(Math.abs(Math.sin(i + 1)) * 4294967296);

  let a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
  for (let off = 0; off < w.length; off += 16) {
    let a = a0, b = b0, c = c0, d = d0;
    for (let i = 0; i < 64; i++) {
      let f, g;
      if (i < 16) { f = (b & c) | (~b & d); g = i; }
      else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) % 16; }
      else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) % 16; }
      else { f = c ^ (b | ~d); g = (7 * i) % 16; }
      const s = S[(i >> 4) * 4 + (i % 4)];
      const t = (a + f + K[i] + w[off + g]) | 0;
      a = d; d = c; c = b;
      b = (b + ((t << s) | (t >>> (32 - s)))) | 0;
    }
    a0 = (a0 + a) | 0; b0 = (b0 + b) | 0; c0 = (c0 + c) | 0; d0 = (d0 + d) | 0;
  }
  return [a0, b0, c0, d0].map(x => {
    let h = '';
    for (let i = 0; i < 4; i++) h += ((x >>> (i * 8)) & 255).toString(16).padStart(2, '0');
    return h;
  }).join('');
}

class AuthError extends Error {}

// Configuração do mosaico de câmeras (chrome.storage.local: a senha não sincroniza com a conta Google).
const CAM_DEFAULTS = { host: '10.0.45.117', user: 'admin', pass: '', channels: 8, interval: 1000, overlay: true };
const toBase = host => (/^[a-z]+:\/\//i.test(host) ? host : 'http://' + host).replace(/\/+$/, '');

async function loadCamConfig() {
  const { cam = {} } = await chrome.storage.local.get('cam');
  return Object.assign({}, CAM_DEFAULTS, cam);
}

class DigestClient {
  // base: "http://10.0.45.117"
  constructor(base, user, pass) {
    this.base = base.replace(/\/+$/, '');
    this.user = user;
    this.pass = pass;
    this.challenge = null;
    this.nc = 0;
  }

  static parseChallenge(header) {
    const c = {};
    const re = /(\w+)=(?:"([^"]*)"|([^,\s]*))/g;
    let m;
    while ((m = re.exec(header.replace(/^\s*Digest\s+/i, '')))) c[m[1].toLowerCase()] = m[2] !== undefined ? m[2] : m[3];
    return c;
  }

  // O DVR explica a recusa no corpo do 401: <lockStatus>lock</lockStatus>, <unlockTime> (s), <retryLoginTime>.
  static rejectionMessage(body) {
    const tag = name => { const m = body.match(new RegExp(`<${name}>([^<]*)</${name}>`)); return m ? m[1] : ''; };
    if (tag('lockStatus') === 'lock') {
      const min = Math.ceil((parseInt(tag('unlockTime'), 10) || 0) / 60);
      return 'O DVR bloqueou o acesso deste computador por excesso de tentativas' + (min ? ` (libera em cerca de ${min} min).` : '.');
    }
    const left = tag('retryLoginTime');
    return 'Usuário ou senha recusados pelo DVR' + (left ? ` (restam ${left} tentativas antes do bloqueio).` : '.');
  }

  authorization(method, uri) {
    const c = this.challenge;
    const nc = (++this.nc).toString(16).padStart(8, '0');
    const cnonce = Array.from(crypto.getRandomValues(new Uint8Array(8)), b => b.toString(16).padStart(2, '0')).join('');
    const ha1 = md5(`${this.user}:${c.realm}:${this.pass}`);
    const ha2 = md5(`${method}:${uri}`);
    const qop = c.qop && c.qop.split(',').map(s => s.trim()).includes('auth') ? 'auth' : null;
    const response = qop ? md5(`${ha1}:${c.nonce}:${nc}:${cnonce}:${qop}:${ha2}`) : md5(`${ha1}:${c.nonce}:${ha2}`);
    let h = `Digest username="${this.user}", realm="${c.realm}", nonce="${c.nonce}", uri="${uri}", algorithm=MD5, response="${response}"`;
    if (qop) h += `, qop=${qop}, nc=${nc}, cnonce="${cnonce}"`;
    if (c.opaque !== undefined) h += `, opaque="${c.opaque}"`;
    return h;
  }

  // Faz GET em "path" (ex.: "/ISAPI/System/deviceInfo"). Lança AuthError se o usuário/senha forem recusados.
  // Recusa com um nonce recém-recebido é definitiva (evita somar tentativas erradas e bloquear o usuário no DVR);
  // recusa com nonce antigo pode ser só expiração, então pega o novo e tenta mais uma vez.
  async get(path, timeoutMs = 8000) {
    let fresh = false;
    for (;;) {
      const sent = !!this.challenge;
      const headers = {};
      if (sent) headers.Authorization = this.authorization('GET', path);
      const ctrl = new AbortController();
      const timer = setTimeout(() => ctrl.abort(), timeoutMs);
      let res;
      try {
        res = await fetch(this.base + path, { headers, credentials: 'omit', cache: 'no-store', signal: ctrl.signal });
      } finally {
        clearTimeout(timer);
      }
      if (res.status !== 401) return res;
      if (sent && fresh) throw new AuthError(DigestClient.rejectionMessage(await res.text().catch(() => '')));
      const www = res.headers.get('WWW-Authenticate') || '';
      if (!/^\s*Digest/i.test(www)) throw new AuthError('O DVR não aceitou autenticação Digest.');
      this.challenge = DigestClient.parseChallenge(www);
      this.nc = 0;
      fresh = true;
    }
  }
}
