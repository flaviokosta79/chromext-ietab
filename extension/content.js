// HikTab - injeta o player de vídeo ao vivo dentro da página de visualização do DVR Hikvision,
// cobrindo a área onde o plugin (WebComponents/ActiveX) desenharia o vídeo. Assim a própria página
// da câmera mostra as imagens, como se o plugin estivesse instalado.

(function () {
  const IFRAME_ID = 'hiktab-live';
  const SRC = chrome.runtime.getURL('video.html?embed=1');

  // Onde o plugin se desenha na página (preview.asp / playback.asp): <div id="main_plugin">.
  function mount() {
    // Só na visualização ao vivo; na aba de reprodução (gravações) o player ao vivo não faz sentido.
    if (/playback/i.test(location.pathname)) return;
    const holder = document.getElementById('main_plugin');
    if (!holder) return;
    if (document.getElementById(IFRAME_ID)) return; // já montado
    holder.innerHTML = '';
    if (getComputedStyle(holder).position === 'static') holder.style.position = 'relative';
    if (!holder.style.height) holder.style.height = '100%';
    const f = document.createElement('iframe');
    f.id = IFRAME_ID;
    f.src = SRC;
    f.allow = 'autoplay';
    f.setAttribute('style', 'position:absolute;left:0;top:0;width:100%;height:100%;border:0;background:#000;z-index:5;');
    holder.appendChild(f);
  }

  // Só injeta se a sobreposição estiver ligada nas opções; desligada, a página do DVR já foi aberta
  // em modo IE pelo service worker (plugin real), e este script nem chega a rodar aqui.
  chrome.storage.local.get('cam', ({ cam = {} }) => {
    if (cam.overlay === false) return;
    // A página monta o #main_plugin de forma assíncrona (AngularJS) e pode recriá-lo ao trocar de aba;
    // o observador remonta o player sempre que isso acontecer.
    const observer = new MutationObserver(() => mount());
    observer.observe(document.documentElement, { childList: true, subtree: true });
    mount();
  });
})();
