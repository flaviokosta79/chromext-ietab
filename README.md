# HikTab — Câmeras Hikvision no Chrome

Mostra as câmeras de um DVR/NVR Hikvision no Chrome **sem instalar plugin**. Tem três modos:

- **Vídeo ao vivo (sem plugin):** o componente do Windows abre o RTSP do DVR e o Chrome decodifica o H.264 com WebCodecs — vídeo fluido, inclusive dentro da própria página do DVR.
- **Modo IE (plugin real):** abre o DVR com o motor do Internet Explorer do Windows, rodando o WebComponents/ActiveX original (reordenar câmeras, PTZ, gravar, áudio).
- **Fotos (mosaico):** fotos de cada canal atualizadas em sequência, 100% no Chrome, sem nenhum componente.

Um interruptor nas opções alterna entre o vídeo ao vivo e o modo IE ao abrir o DVR. Código próprio, sem licença, sem dependências de terceiros.

## Como funciona

```
Modo IE:        Chrome (container.html)  ⇄  Native Messaging  ⇄  HikTabHost.exe (motor IE / ActiveX)
Vídeo ao vivo:  Chrome (video.html + WebCodecs)  ⇄  Native Messaging  ⇄  HikTabHost.exe (cliente RTSP + H.264)
Fotos:          Chrome (cameras.html)  ──  HTTP Digest  ──►  DVR (ISAPI /picture)
```

## Instalação no PC da recepção

1. Copie a pasta `hiktab` para o PC (ex.: `C:\HikTab`).
2. Dê dois cliques em **`install.cmd`**. Não precisa de administrador.
   - Ele sempre pergunta o IP do DVR para incluir nos Sites Confiáveis (recomendado para o plugin ActiveX).
     Pressione Enter para usar `10.0.45.117`, ou digite outro(s) endereço(s) separados por espaço.
3. No Chrome, abra `chrome://extensions`, ative o **Modo do desenvolvedor**, clique em **Carregar sem compactação** e escolha a pasta `hiktab\extension`.
4. Nas opções da extensão (abre sozinha), em **Conexão com o DVR** preencha endereço, usuário, senha e canais; clique em **Salvar** e **Testar conexão**.
5. Deixe o interruptor **"Vídeo ao vivo na página do DVR"** ligado e digite o IP do DVR na barra do Chrome: as câmeras aparecem ao vivo, sem plugin. (Desligado, ele abre no modo IE com o plugin real.)

As opções ficam em três blocos: **Conexão com o DVR**, o **interruptor** do vídeo ao vivo (com os botões *Abrir câmeras ao vivo* / *Abrir em fotos*) e **Avançado** (intervalo das fotos, testar componente, diagnóstico e a lista de endereços do modo IE).

Dica: em `chrome://settings/onStartup`, configure o Chrome para abrir o endereço do DVR ao iniciar.

## Câmeras ao vivo (vídeo, sem plugin do navegador)

Vídeo ao vivo de verdade, fluido, igual ao plugin — mas **sem** o WebComponents/ActiveX. O componente do Windows (`HikTabHost`) abre o RTSP do DVR (porta 554), desempacota o H.264 e entrega os quadros à extensão, que os decodifica com o **WebCodecs** do Chrome e desenha num `canvas`.

```
Chrome (video.html + WebCodecs)  ⇄  Native Messaging  ⇄  HikTabHost.exe (cliente RTSP + desempacotador H.264)
      └─ decodifica e desenha o H.264 ──────────────────┘  └─ abre rtsp://DVR:554/Streaming/Channels/<canal>0<1|2>
```

1. **Rode o `install.cmd`** neste PC (compila o `HikTabHost`, com o cliente RTSP) e recarregue a extensão em `chrome://extensions`.
2. Nas opções, em **Conexão com o DVR**, preencha endereço, usuário, senha e canais; **Salvar** e **Testar conexão**.
3. Abra com **Abrir câmeras ao vivo** nas opções, ou pelo botão direito no ícone → **Câmeras ao vivo (vídeo)**.
4. Clique numa câmera (ou tecle 1–8) para ampliar: aí ela passa para o **stream principal** (alta resolução); no mosaico usa o substream (leve). **Esc** volta.

Para recuperação rápida (o GOP do stream principal pode ser longo), o host pede um quadro-chave sob demanda ao DVR (`PUT /ISAPI/Streaming/channels/<id>/requestKeyFrame`) ao abrir/ampliar e ao ressincronizar — isso evita a "tela verde" prolongada. Requisitos e limites: precisa do componente instalado (`install.cmd`); é só vídeo (sem áudio, PTZ ou gravações); se o WebCodecs não existir no navegador, a página oferece o modo fotos.

### Dentro da própria página do DVR

Ao abrir a interface do DVR (`http://10.0.45.117`), a extensão injeta o player ao vivo exatamente na área onde o plugin desenharia o vídeo (`#main_plugin`), via `content.js`. Assim a página da Hikvision mostra as câmeras ao vivo, como se o WebComponents estivesse instalado — sem instalar nada no navegador. Para habilitar em outro DVR, acrescente o endereço em `content_scripts`/`web_accessible_resources` no `manifest.json`.

### Interruptor: sobreposição × plugin real (modo IE)

Nas opções há o interruptor **"Vídeo ao vivo na página do DVR"** (salva sozinho ao alternar):

- **Ligado:** ao abrir o DVR, ele carrega no Chrome e a extensão sobrepõe o player ao vivo (sem plugin). O menu de canais, PTZ e os botões da página do DVR **não** controlam esse player — ele mostra todos os canais; clique numa câmera para ampliar.
- **Desligado:** ao abrir o DVR, a extensão o abre em **modo IE com o plugin real** da Hikvision (como um IE Tab). Aí tudo funciona de forma original: reordenar câmeras, PTZ, gravar, áudio, etc.

O `background.js` decide isso pelo endereço do DVR (das opções) + o estado do interruptor, independentemente da lista "abrir sempre no modo IE". Na sobreposição há também um botão **"Usar plugin"** (canto superior) que troca só aquela aba para o modo IE, sem mexer na opção salva.

## Câmeras em fotos (mosaico, 100% sem componente)

Não precisa do WebComponents, do modo IE nem do `install.cmd`: funciona no Chrome normal.

1. Nas opções da extensão, em **Conexão com o DVR**, preencha endereço (`10.0.45.117`), usuário, senha e número de canais (8). Clique em **Salvar** e em **Testar conexão**.
2. Clique com o botão direito no ícone da extensão → **Câmeras em fotos (sem componente)** (ou **Abrir em fotos** nas opções).
3. Clique numa câmera (ou tecle 1–8) para ampliar em alta resolução; **Esc** volta ao mosaico.

As imagens são fotos da API ISAPI do DVR (`/ISAPI/Streaming/channels/<canal>0<1|2>/picture`) atualizadas a cada 0,5–5 s: não é vídeo contínuo, não tem áudio nem reprodução de gravações. A senha fica em `chrome.storage.local` (só neste PC); prefira um usuário do DVR só com permissão de visualização. Se a senha for recusada, o mosaico para na primeira tentativa para não bloquear o usuário no DVR.

## Uso

- **Botão da extensão**: alterna a aba atual entre o Chrome e o modo IE.
- **Clique direito num link**: opção *Abrir link no modo IE*.
- Dentro do modo IE funcionam: Ctrl+T, Ctrl+W, Ctrl+Tab, Ctrl+PgUp/PgDn e F11.

## Problemas

- **"componente não está instalado"**: rode `install.cmd` de novo. O ID da extensão deve ser `gopcobokmjhfhhhifedbgdnjpgcfgaep`.
- **Log de diagnóstico**: crie a variável de ambiente do usuário `HIKTAB_LOG=C:\HikTab\log.txt` e reabra o Chrome.
- **Desinstalar**: `uninstall.cmd` e depois remova a extensão em `chrome://extensions`.

## Arquivos

| Arquivo | Função |
|---|---|
| `extension/` | Extensão do Chrome (MV3) |
| `extension/video.*` | Câmeras ao vivo (vídeo RTSP + WebCodecs) |
| `extension/content.js` | Injeta o player ao vivo dentro da página do DVR |
| `extension/cameras.*` | Mosaico de câmeras em fotos (sem componente) |
| `extension/digest.js` | Login Digest (MD5) na API ISAPI do DVR |
| `host/HikTabHost.cs` | Componente do Windows: motor IE **e** cliente RTSP/desempacotador H.264 (compilado pelo `install.cmd`) |
| `key.pem` | Chave que fixa o ID da extensão. **Não distribua**; guarde para futuras versões. |
