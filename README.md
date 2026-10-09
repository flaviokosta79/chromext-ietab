# HikTab — Modo IE para câmeras

Abre o DVR/NVR Hikvision (ou qualquer página que exija Internet Explorer/ActiveX) **dentro de uma aba do Chrome**, usando o motor do IE do próprio Windows. Código próprio, sem licença, sem dependências de terceiros.

## Como funciona

```
Chrome (aba container.html)  ⇄  Native Messaging  ⇄  HikTabHost.exe (motor IE / ActiveX)
        └─ informa posição/tamanho da aba ───────────┘  └─ se desenha encaixado na aba
```

## Instalação no PC da recepção

1. Copie a pasta `hiktab` para o PC (ex.: `C:\HikTab`).
2. Dê dois cliques em **`install.cmd`**. Não precisa de administrador.
   - Ele sempre pergunta o IP do DVR para incluir nos Sites Confiáveis (recomendado para o plugin ActiveX).
     Pressione Enter para usar `10.0.45.117`, ou digite outro(s) endereço(s) separados por espaço.
3. No Chrome, abra `chrome://extensions`, ative o **Modo do desenvolvedor**, clique em **Carregar sem compactação** e escolha a pasta `hiktab\extension`.
4. Nas opções da extensão (abre sozinha), cadastre o endereço do DVR e clique em **Salvar** e depois em **Testar componente**.
5. Digite o IP do DVR na barra do Chrome: ele abre no modo IE. Na primeira vez, instale o plugin que o próprio DVR oferecer (WebComponents).

Dica: em `chrome://settings/onStartup`, configure o Chrome para abrir o endereço do DVR ao iniciar.

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
| `host/HikTabHost.cs` | Componente do Windows (compilado pelo `install.cmd` com o .NET que já vem no Windows) |
| `key.pem` | Chave que fixa o ID da extensão. **Não distribua**; guarde para futuras versões. |
