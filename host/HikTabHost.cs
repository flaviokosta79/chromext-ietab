// HikTab Host - motor do Internet Explorer (WebBrowser/Trident) embutido numa aba do Chrome.
// Comunica-se com a extensão via Native Messaging (stdin/stdout, mensagens JSON com prefixo de 4 bytes).
// Compilar (x86, para compatibilidade com plugins ActiveX 32 bits): ver install.ps1.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace HikTab
{
    static class Program
    {
        // Log opcional: defina a variável de ambiente HIKTAB_LOG com o caminho de um arquivo.
        static readonly string logPath = Environment.GetEnvironmentVariable("HIKTAB_LOG");

        public static void Log(string text)
        {
            if (string.IsNullOrEmpty(logPath)) return;
            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff ") + text + Environment.NewLine); } catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            // Teste de linha de comando do cliente RTSP (sem Chrome):
            //   HikTabHost.exe --rtsp-selftest rtsp://ip:554/Streaming/Channels/102 usuario senha [segundos] [arquivo.h264]
            if (args.Length >= 2 && args[0] == "--rtsp-selftest") { RtspManager.SelfTest(args); return; }

            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("ERRO: " + e.ExceptionObject);
            Application.ThreadException += (s, e) => Log("ERRO: " + e.Exception);
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var host = new HostWindow();
            Application.AddMessageFilter(new KeyFilter(host.SendKey));
            Application.Run(new ApplicationContext());
        }
    }

    class HostWindow : Form
    {
        readonly Browser web;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly Stream stdout = Console.OpenStandardOutput();
        readonly object writeLock = new object();
        readonly Dictionary<uint, bool> browserPids = new Dictionary<uint, bool>();
        readonly RtspManager rtsp;
        IntPtr chromeWnd = IntPtr.Zero;
        Rectangle target = Rectangle.Empty;
        bool visibleWanted = true;
        string pendingUrl;

        public HostWindow()
        {
            // ShowInTaskbar fica true de propósito: false faz o WinForms recriar a janela no Show() e o
            // WebBrowser fica órfão. A janela some da barra de tarefas pelo WS_EX_TOOLWINDOW (CreateParams).
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-32000, -32000, 100, 100);
            Text = "HikTab";

            web = new Browser { Dock = DockStyle.Fill, ScriptErrorsSuppressed = false };
            web.DocumentTitleChanged += (s, e) => Send(new { type = "title", title = web.DocumentTitle });
            web.Navigated += (s, e) => SendNav();
            web.CanGoBackChanged += (s, e) => SendNav();
            web.CanGoForwardChanged += (s, e) => SendNav();
            web.DocumentCompleted += (s, e) =>
            {
                // Silencia janelas de erro de script sem bloquear outros diálogos (login, instalação de plugin).
                try { if (web.Document != null && web.Document.Window != null) web.Document.Window.Error += (o, a) => a.Handled = true; } catch { }
                SendNav();
            };
            web.NewWindowRequested += url => Send(new { type = "newwindow", url = url });
            web.WindowClosingRequested += () => Send(new { type = "close" });
            Controls.Add(web);

            rtsp = new RtspManager(Send);

            CreateHandle();
            new Thread(ReadLoop) { IsBackground = true }.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle = (cp.ExStyle & ~Native.WS_EX_APPWINDOW) | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        // ---------- Native Messaging ----------

        void ReadLoop()
        {
            var reader = new JavaScriptSerializer();
            var stdin = Console.OpenStandardInput();
            var lenBuf = new byte[4];
            try
            {
                while (ReadExact(stdin, lenBuf, 4))
                {
                    int len = BitConverter.ToInt32(lenBuf, 0);
                    if (len <= 0 || len > (1 << 24)) break;
                    var buf = new byte[len];
                    if (!ReadExact(stdin, buf, len)) break;
                    var msg = reader.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(buf));
                    BeginInvoke(new Action(() => OnMessage(msg)));
                }
            }
            catch { }
            try { BeginInvoke(new Action(Quit)); } catch { Environment.Exit(0); }
        }

        static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        void Send(object o)
        {
            try
            {
                var text = json.Serialize(o);
                Program.Log("<- " + text);
                var bytes = Encoding.UTF8.GetBytes(text);
                lock (writeLock)
                {
                    stdout.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
                    stdout.Write(bytes, 0, bytes.Length);
                    stdout.Flush();
                }
            }
            catch (Exception ex) { Program.Log("ERRO ao enviar: " + ex.Message); }
        }

        void SendNav()
        {
            Send(new { type = "nav", url = web.Url == null ? "" : web.Url.ToString(), back = web.CanGoBack, forward = web.CanGoForward });
        }

        public void SendKey(string action)
        {
            Send(new { type = "key", action = action });
        }

        void OnMessage(Dictionary<string, object> m)
        {
            try { HandleMessage(m); }
            catch (Exception ex) { Program.Log("ERRO em " + Str(m, "type") + ": " + ex); }
        }

        void HandleMessage(Dictionary<string, object> m)
        {
            Program.Log("-> " + json.Serialize(m));
            switch (Str(m, "type"))
            {
                case "navigate":
                    var url = Str(m, "url");
                    if (string.IsNullOrEmpty(url)) break;
                    if (web.IsHandleCreated) web.Navigate(url);
                    else pendingUrl = url;
                    break;
                case "back": if (web.CanGoBack) web.GoBack(); break;
                case "forward": if (web.CanGoForward) web.GoForward(); break;
                case "refresh": web.Refresh(WebBrowserRefreshOption.Completely); break;
                case "stop": web.Stop(); break;
                case "print": web.ShowPrintDialog(); break;
                case "bounds":
                    target = new Rectangle(Int(m, "x"), Int(m, "y"), Int(m, "w"), Int(m, "h"));
                    Place();
                    break;
                case "visible":
                    visibleWanted = Bool(m, "value");
                    Place();
                    break;
                case "reparent":
                    chromeWnd = IntPtr.Zero;
                    Place();
                    break;
                case "close": Quit(); break;
                // Ponte de vídeo ao vivo: a extensão abre/fecha fluxos RTSP; o host devolve os quadros H.264.
                case "rtsp.open": rtsp.Open(Str(m, "id"), Str(m, "url"), Str(m, "user"), Str(m, "pass")); break;
                case "rtsp.close": rtsp.Close(Str(m, "id")); break;
                case "rtsp.key": rtsp.RequestKey(Str(m, "id")); break;
            }
        }

        static string Str(Dictionary<string, object> m, string k) { object v; return m.TryGetValue(k, out v) && v != null ? v.ToString() : null; }
        static int Int(Dictionary<string, object> m, string k) { object v; return m.TryGetValue(k, out v) && v != null ? Convert.ToInt32(v) : 0; }
        static bool Bool(Dictionary<string, object> m, string k) { object v; return m.TryGetValue(k, out v) && v is bool && (bool)v; }

        // ---------- Encaixe na janela do Chrome ----------

        void Place()
        {
            if (!visibleWanted || target.Width <= 0 || target.Height <= 0)
            {
                if (Visible) Hide();
                return;
            }
            var owner = FindChromeWindow();
            if (owner == IntPtr.Zero)
            {
                if (Visible) Hide();
                Program.Log("Janela do Chrome não encontrada para " + target);
                return;
            }
            if (owner != chromeWnd)
            {
                // Janela "possuída" pelo Chrome: fica sempre à frente dele, minimiza junto e não
                // cobre outros programas. (Janela-filha de outro processo não funciona com o WinForms.)
                Native.SetWindowLong(Handle, Native.GWL_HWNDPARENT, owner.ToInt32());
                chromeWnd = owner;
                Program.Log("Vinculado à janela do Chrome " + owner);
            }

            if (Bounds != target) Bounds = target;
            if (!Visible)
            {
                Show();
                Program.Log(string.Format("Exibido: janela={0} web={1} webParent={2}", Handle, web.Handle, Native.GetParent(web.Handle)));
            }
            if (pendingUrl != null)
            {
                web.Navigate(pendingUrl);
                pendingUrl = null;
            }
        }

        IntPtr FindChromeWindow()
        {
            if (chromeWnd != IntPtr.Zero && Native.IsWindow(chromeWnd) && Native.IsWindowVisible(chromeWnd)
                && !Native.IsIconic(chromeWnd) && Covers(chromeWnd, target))
                return chromeWnd;

            // EnumWindows percorre em ordem Z (de cima para baixo): pega a janela do navegador mais à frente
            // que contém a área da aba.
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                if (ClassOf(h) != "Chrome_WidgetWin_1") return true;
                if (Native.GetWindowTextLength(h) == 0) return true;
                if (!Covers(h, target)) return true;
                if (!IsBrowserProcess(h)) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        static bool Covers(IntPtr h, Rectangle r)
        {
            Native.RECT wr;
            if (!Native.GetWindowRect(h, out wr)) return false;
            const int tol = 4;
            return r.Left >= wr.Left - tol && r.Top >= wr.Top - tol && r.Right <= wr.Right + tol && r.Bottom <= wr.Bottom + tol;
        }

        static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64);
            Native.GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        bool IsBrowserProcess(IntPtr h)
        {
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            bool ok;
            if (browserPids.TryGetValue(pid, out ok)) return ok;
            try
            {
                var name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
                ok = name == "chrome" || name == "msedge";
            }
            catch { ok = false; }
            browserPids[pid] = ok;
            return ok;
        }

        void Quit()
        {
            try { rtsp.CloseAll(); } catch { }
            try { Native.ShowWindow(Handle, Native.SW_HIDE); } catch { }
            Environment.Exit(0);
        }
    }

    // Atalhos do Chrome que, com o foco dentro do IE, seriam engolidos pelo motor IE.
    class KeyFilter : IMessageFilter
    {
        const int WM_KEYDOWN = 0x0100;
        readonly Action<string> send;
        public KeyFilter(Action<string> send) { this.send = send; }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_KEYDOWN) return false;
            var key = (Keys)((int)m.WParam & 0xFFFF);
            bool ctrl = (Control.ModifierKeys & Keys.Control) != 0;
            bool shift = (Control.ModifierKeys & Keys.Shift) != 0;
            string action = null;
            if (ctrl && key == Keys.T) action = "newtab";
            else if (ctrl && key == Keys.W) action = "closetab";
            else if (ctrl && key == Keys.Tab) action = shift ? "prevtab" : "nexttab";
            else if (ctrl && key == Keys.PageDown) action = "nexttab";
            else if (ctrl && key == Keys.PageUp) action = "prevtab";
            else if (key == Keys.F11) action = "fullscreen";
            if (action == null) return false;
            send(action);
            return true;
        }
    }

    // ---------- WebBrowser com eventos NewWindow3 / WindowClosing ----------

    [ComImport, Guid("34A715A0-6587-11D0-924A-0020AFC7AC4D"),
     InterfaceType(ComInterfaceType.InterfaceIsIDispatch), TypeLibType(TypeLibTypeFlags.FHidden)]
    public interface DWebBrowserEvents2
    {
        [DispId(263)]
        void WindowClosing([In] bool IsChildWindow, [In, Out] ref bool Cancel);

        [DispId(273)]
        void NewWindow3([In, Out, MarshalAs(UnmanagedType.IDispatch)] ref object ppDisp, [In, Out] ref bool Cancel,
            [In] uint dwFlags, [In, MarshalAs(UnmanagedType.BStr)] string bstrUrlContext,
            [In, MarshalAs(UnmanagedType.BStr)] string bstrUrl);
    }

    class Browser : WebBrowser
    {
        AxHost.ConnectionPointCookie cookie;
        public event Action<string> NewWindowRequested;
        public event Action WindowClosingRequested;

        protected override void CreateSink()
        {
            base.CreateSink();
            cookie = new AxHost.ConnectionPointCookie(ActiveXInstance, new EventSink(this), typeof(DWebBrowserEvents2));
        }

        protected override void DetachSink()
        {
            if (cookie != null) { cookie.Disconnect(); cookie = null; }
            base.DetachSink();
        }

        void RaiseNewWindow(string url) { var h = NewWindowRequested; if (h != null) h(url); }
        void RaiseClosing() { var h = WindowClosingRequested; if (h != null) h(); }

        [ClassInterface(ClassInterfaceType.None)]
        class EventSink : StandardOleMarshalObject, DWebBrowserEvents2
        {
            readonly Browser owner;
            public EventSink(Browser owner) { this.owner = owner; }

            public void WindowClosing(bool isChildWindow, ref bool cancel)
            {
                cancel = true;
                owner.RaiseClosing();
            }

            public void NewWindow3(ref object ppDisp, ref bool cancel, uint flags, string context, string url)
            {
                // Pop-ups sem URL (window.open('') + document.write) ficam com o comportamento padrão do IE.
                if (string.IsNullOrEmpty(url) || url == "about:blank") return;
                cancel = true;
                owner.RaiseNewWindow(url);
            }
        }
    }

    // ---------- Ponte RTSP -> navegador ----------
    // Abre o fluxo RTSP (porta 554) do DVR, desempacota o H.264 (RFC 6184) e entrega cada quadro
    // (unidade de acesso em Annex B) para a extensão, que decodifica com WebCodecs. O host NÃO decodifica.

    class RtspManager
    {
        readonly Action<object> send;
        readonly Dictionary<string, RtspSession> sessions = new Dictionary<string, RtspSession>();
        readonly object gate = new object();

        public RtspManager(Action<object> send) { this.send = send; }

        public void Open(string id, string url, string user, string pass)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url)) return;
            Close(id);
            var s = new RtspSession(url, user, pass);
            s.Sdp = (codec, init) => send(new { type = "rtsp.sdp", id = id, codec = codec, init = Convert.ToBase64String(init) });
            s.Frame = (key, data, ts) => send(new { type = "rtsp.frame", id = id, key = key, ts = ts, data = Convert.ToBase64String(data) });
            s.Closed = msg => { lock (gate) sessions.Remove(id); send(new { type = "rtsp.error", id = id, message = msg }); };
            lock (gate) sessions[id] = s;
            s.Start();
        }

        public void Close(string id)
        {
            RtspSession s = null;
            lock (gate) { if (id != null && sessions.TryGetValue(id, out s)) sessions.Remove(id); }
            if (s != null) s.Stop();
        }

        // Pede ao DVR um quadro-chave imediato para este canal (recuperação rápida, sem esperar o GOP).
        public void RequestKey(string id)
        {
            RtspSession s = null;
            lock (gate) { if (id != null) sessions.TryGetValue(id, out s); }
            if (s != null) s.RequestKeyFrameAsync();
        }

        public void CloseAll()
        {
            RtspSession[] all;
            lock (gate) { all = new RtspSession[sessions.Count]; sessions.Values.CopyTo(all, 0); sessions.Clear(); }
            foreach (var s in all) s.Stop();
        }

        public static void SelfTest(string[] args)
        {
            string url = args[1], user = args.Length > 2 ? args[2] : "", pass = args.Length > 3 ? args[3] : "";
            int secs = args.Length > 4 ? int.Parse(args[4]) : 4;
            string outf = args.Length > 5 ? args[5] : null;
            FileStream fs = outf != null ? new FileStream(outf, FileMode.Create) : null;
            // Índice (um quadro por linha: "offset tamanho key ts") para o teste separar as unidades de acesso.
            StreamWriter idx = outf != null ? new StreamWriter(outf + ".idx") : null;
            long offset = 0;
            int frames = 0, keys = 0; string codecOut = "";
            var s = new RtspSession(url, user, pass);
            s.Sdp = (codec, init) => { codecOut = codec; Console.Error.WriteLine("SDP codec=" + codec + " init=" + init.Length + "B"); };
            s.Frame = (key, data, ts) =>
            {
                frames++; if (key) keys++;
                if (fs != null) { fs.Write(data, 0, data.Length); if (idx != null) idx.WriteLine(offset + " " + data.Length + " " + (key ? 1 : 0) + " " + ts); offset += data.Length; }
            };
            s.Closed = msg => Console.Error.WriteLine("FIM: " + msg);
            s.Start();
            Thread.Sleep(secs * 1000);
            s.Stop();
            Thread.Sleep(200);
            if (fs != null) fs.Close();
            if (idx != null) idx.Close();
            Console.Error.WriteLine(string.Format("frames={0} keyframes={1} codec={2} arquivo={3}", frames, keys, codecOut, outf));
        }
    }

    class RtspSession
    {
        readonly string url, user, pass;
        public Action<string, byte[]> Sdp;        // codec (ex.: avc1.4D0014), SPS+PPS em Annex B
        public Action<bool, byte[], long> Frame;  // keyframe?, unidade de acesso Annex B, timestamp (us)
        public Action<string> Closed;

        TcpClient client;
        volatile bool running;
        volatile bool stopped; // encerramento pedido por nós (troca de stream/fechar) -> não é falha
        Thread thread;
        readonly int channel; // nº do canal ISAPI (ex.: 101, 102), extraído da URL RTSP

        // Estado do desempacotador H.264
        MemoryStream au = new MemoryStream();
        MemoryStream fu;
        bool auKey, auHasParam, haveBase;
        uint baseTs;
        byte[] sps, pps, init;
        static readonly byte[] StartCode = { 0, 0, 0, 1 };

        public RtspSession(string url, string user, string pass) { this.url = url; this.user = user ?? ""; this.pass = pass ?? ""; channel = ParseChannel(url); }

        static int ParseChannel(string url)
        {
            int i = url.IndexOf("/Channels/", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return 0;
            i += 10;
            int j = i;
            while (j < url.Length && char.IsDigit(url[j])) j++;
            int n; return int.TryParse(url.Substring(i, j - i), out n) ? n : 0;
        }

        // Força um quadro-chave (IDR) imediato neste canal via ISAPI, numa thread (não bloqueia o fluxo).
        public void RequestKeyFrameAsync()
        {
            if (channel <= 0) return;
            new Thread(RequestKeyFrame) { IsBackground = true }.Start();
        }

        void RequestKeyFrame()
        {
            try
            {
                var u = ParseUrl(url);
                var req = (HttpWebRequest)WebRequest.Create("http://" + u.Host + "/ISAPI/Streaming/channels/" + channel + "/requestKeyFrame");
                req.Method = "PUT";
                req.Timeout = 5000;
                req.ContentType = "application/xml";
                var cache = new CredentialCache();
                cache.Add(new Uri("http://" + u.Host), "Digest", new NetworkCredential(user, pass));
                req.Credentials = cache;
                var body = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>");
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (var r = req.GetResponse()) { }
            }
            catch { }
        }

        public void Start() { running = true; thread = new Thread(Run) { IsBackground = true }; thread.Start(); }

        public void Stop()
        {
            stopped = true;
            running = false;
            try { if (client != null) client.Close(); } catch { }
        }

        void Run()
        {
            string reason = "encerrado";
            try
            {
                var u = ParseUrl(url);
                client = new TcpClient();
                client.Connect(u.Host, u.Port);
                var stream = client.GetStream();
                var reader = new Reader(stream);
                var digest = new DigestRtsp(user, pass);
                int cseq = 1;

                // DESCRIBE (com repetição após o desafio Digest)
                string sdp = null;
                for (int attempt = 0; attempt < 2 && sdp == null; attempt++)
                {
                    WriteRequest(stream, "DESCRIBE", url, digest, cseq++, "Accept: application/sdp\r\n", null);
                    var r = ReadResponse(reader);
                    if (r.Status == 401 && attempt == 0) { digest.Challenge(r.Header("WWW-Authenticate")); continue; }
                    if (r.Status != 200) { reason = "DESCRIBE HTTP " + r.Status; Finish(reason); return; }
                    sdp = r.Body;
                }
                if (sdp == null) { Finish("falha na autenticação (DESCRIBE)"); return; }

                string codec, control;
                ParseSdp(sdp, out codec, out control, out sps, out pps);
                RebuildInit();
                if (init != null && Sdp != null) Sdp(codec, init);

                string setupUrl = control.StartsWith("rtsp://") ? control : url.TrimEnd('/') + "/" + control;
                WriteRequest(stream, "SETUP", setupUrl, digest, cseq++, "Transport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n", null);
                var sr = ReadResponse(reader);
                if (sr.Status != 200) { Finish("SETUP HTTP " + sr.Status); return; }
                string session = sr.Header("Session");
                if (session != null) { int semi = session.IndexOf(';'); if (semi >= 0) session = session.Substring(0, semi); session = session.Trim(); }

                WriteRequest(stream, "PLAY", url, digest, cseq++, "Session: " + session + "\r\nRange: npt=0.000-\r\n", null);
                var pr = ReadResponse(reader);
                if (pr.Status != 200) { Finish("PLAY HTTP " + pr.Status); return; }
                RequestKeyFrameAsync(); // o GOP pode ser longo; pede um quadro-chave já para começar rápido

                // Laço de mídia: pacotes RTP intercalados ($ canal tamanho) e eventuais respostas RTSP.
                var lenBuf = new byte[2];
                while (running)
                {
                    int b = reader.ReadByte();
                    if (b < 0) { reason = "conexão encerrada pelo DVR"; break; }
                    if (b != 0x24) { reader.SkipResponse(b); continue; }
                    int ch = reader.ReadByte();
                    if (!reader.ReadExact(lenBuf, 2)) break;
                    int len = (lenBuf[0] << 8) | lenBuf[1];
                    var pkt = new byte[len];
                    if (!reader.ReadExact(pkt, len)) break;
                    if (ch == 0) Depacketize(pkt);
                }
            }
            catch (Exception ex) { reason = ex.Message; }
            Finish(reason);
        }

        bool finished;
        // Só avisa a extensão quando foi falha de verdade; encerramento pedido por nós (troca de stream) é silencioso.
        void Finish(string reason) { if (finished) return; finished = true; try { if (client != null) client.Close(); } catch { } var h = Closed; if (h != null && !stopped) h(reason); }

        // ----- Desempacotamento H.264 (RFC 6184) -----

        void Depacketize(byte[] p)
        {
            if (p.Length < 12) return;
            int cc = p[0] & 0x0f;
            bool ext = (p[0] & 0x10) != 0;
            bool marker = (p[1] & 0x80) != 0;
            uint ts = (uint)((p[4] << 24) | (p[5] << 16) | (p[6] << 8) | p[7]);
            int hl = 12 + 4 * cc;
            if (ext) { if (p.Length < hl + 4) return; int extLen = (p[hl + 2] << 8) | p[hl + 3]; hl += 4 + 4 * extLen; }
            int payLen = p.Length - hl;
            if (payLen < 1) return;
            if ((p[0] & 0x20) != 0) { int pad = p[p.Length - 1]; payLen -= pad; if (payLen < 1) return; }

            int f = p[hl], nalType = f & 0x1f;
            if (nalType >= 1 && nalType <= 23)
            {
                WriteNal(p, hl, payLen);
            }
            else if (nalType == 28) // FU-A (NAL fragmentado)
            {
                if (payLen < 2) return;
                int fuH = p[hl + 1];
                bool start = (fuH & 0x80) != 0, end = (fuH & 0x40) != 0;
                if (start) { fu = new MemoryStream(); fu.WriteByte((byte)((f & 0xe0) | (fuH & 0x1f))); }
                if (fu != null)
                {
                    fu.Write(p, hl + 2, payLen - 2);
                    if (end) { var nal = fu.ToArray(); fu = null; WriteNal(nal, 0, nal.Length); }
                }
            }
            else if (nalType == 24) // STAP-A (vários NAL num pacote; costuma trazer SPS/PPS)
            {
                int o = hl + 1, endPos = hl + payLen;
                while (o + 2 <= endPos)
                {
                    int sz = (p[o] << 8) | p[o + 1]; o += 2;
                    if (sz <= 0 || o + sz > endPos) break;
                    WriteNal(p, o, sz); o += sz;
                }
            }

            if (marker) FlushAu(ts);
        }

        void WriteNal(byte[] buf, int off, int len)
        {
            if (len < 1) return;
            int t = buf[off] & 0x1f;
            if (t == 7) { sps = Slice(buf, off, len); auHasParam = true; RebuildInit(); }
            else if (t == 8) { pps = Slice(buf, off, len); auHasParam = true; RebuildInit(); }
            if (t == 5 || t == 7) auKey = true;
            if (t == 6 || t == 9) return; // SEI e delimitador de acesso: dispensáveis
            au.Write(StartCode, 0, 4);
            au.Write(buf, off, len);
        }

        void FlushAu(uint ts)
        {
            byte[] data = au.ToArray();
            au = new MemoryStream();
            bool key = auKey; bool hadParam = auHasParam;
            auKey = false; auHasParam = false;
            if (data.Length == 0) return;
            if (key && !hadParam && init != null)
            {
                var merged = new byte[init.Length + data.Length];
                Buffer.BlockCopy(init, 0, merged, 0, init.Length);
                Buffer.BlockCopy(data, 0, merged, init.Length, data.Length);
                data = merged;
            }
            if (!haveBase) { baseTs = ts; haveBase = true; }
            long micros = (long)((uint)(ts - baseTs)) * 1000000L / 90000L;
            var h = Frame; if (h != null) h(key, data, micros);
        }

        void RebuildInit()
        {
            if (sps == null || pps == null) { init = null; return; }
            var ms = new MemoryStream();
            ms.Write(StartCode, 0, 4); ms.Write(sps, 0, sps.Length);
            ms.Write(StartCode, 0, 4); ms.Write(pps, 0, pps.Length);
            init = ms.ToArray();
        }

        static byte[] Slice(byte[] b, int off, int len) { var r = new byte[len]; Buffer.BlockCopy(b, off, r, 0, len); return r; }

        // ----- RTSP: requisições, respostas, SDP, URL -----

        void WriteRequest(Stream s, string method, string uri, DigestRtsp digest, int cseq, string extra, string body)
        {
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(uri).Append(" RTSP/1.0\r\n");
            sb.Append("CSeq: ").Append(cseq).Append("\r\n");
            string az = digest.Header(method, uri);
            if (az != null) sb.Append(az);
            if (extra != null) sb.Append(extra);
            sb.Append("User-Agent: HikTab\r\n\r\n");
            if (body != null) sb.Append(body);
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }

        static RtspResponse ReadResponse(Reader reader)
        {
            var r = new RtspResponse();
            string first = reader.ReadLine();
            while (first != null && first.Length == 0) first = reader.ReadLine(); // pula linhas em branco iniciais
            if (first == null) return r;
            var parts = first.Split(' ');
            if (parts.Length >= 2) int.TryParse(parts[1], out r.Status);
            string line;
            while ((line = reader.ReadLine()) != null && line.Length > 0)
            {
                int c = line.IndexOf(':');
                if (c > 0) r.Headers[line.Substring(0, c).Trim().ToLowerInvariant()] = line.Substring(c + 1).Trim();
            }
            int len;
            if (int.TryParse(r.Header("Content-Length"), out len) && len > 0)
            {
                var buf = new byte[len];
                if (reader.ReadExact(buf, len)) r.Body = Encoding.UTF8.GetString(buf);
            }
            return r;
        }

        static void ParseSdp(string sdp, out string codec, out string control, out byte[] sps, out byte[] pps)
        {
            codec = "avc1.42E01E"; control = "trackID=video"; sps = null; pps = null;
            bool inVideo = false;
            foreach (var raw in sdp.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("m=")) inVideo = line.StartsWith("m=video");
                else if (inVideo && line.StartsWith("a=control:")) control = line.Substring(10).Trim();
                else if (inVideo && line.StartsWith("a=fmtp:"))
                {
                    foreach (var tok in line.Substring(line.IndexOf(' ') + 1).Split(';'))
                    {
                        var kv = tok.Trim();
                        if (kv.StartsWith("profile-level-id=")) { var v = kv.Substring(17).Trim(); if (v.Length >= 6) codec = "avc1." + v.Substring(0, 6).ToUpperInvariant(); }
                        else if (kv.StartsWith("sprop-parameter-sets="))
                        {
                            var sets = kv.Substring(21).Split(',');
                            if (sets.Length >= 1) sps = TryB64(sets[0]);
                            if (sets.Length >= 2) pps = TryB64(sets[1]);
                        }
                    }
                }
            }
        }

        static byte[] TryB64(string s) { try { return Convert.FromBase64String(s.Trim()); } catch { return null; } }

        struct Url { public string Host; public int Port; }
        static Url ParseUrl(string url)
        {
            var u = new Url(); u.Port = 554;
            string rest = url.Substring(url.IndexOf("://") + 3);
            int slash = rest.IndexOf('/');
            string authority = slash < 0 ? rest : rest.Substring(0, slash);
            int at = authority.IndexOf('@');
            if (at >= 0) authority = authority.Substring(at + 1);
            int colon = authority.LastIndexOf(':');
            if (colon >= 0) { u.Host = authority.Substring(0, colon); int.TryParse(authority.Substring(colon + 1), out u.Port); }
            else u.Host = authority;
            return u;
        }
    }

    class RtspResponse
    {
        public int Status;
        public string Body = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public string Header(string name) { string v; return Headers.TryGetValue(name.ToLowerInvariant(), out v) ? v : null; }
    }

    // Leitor bufferizado: linhas de texto (RTSP) e blocos binários (RTP) no mesmo fluxo.
    class Reader
    {
        readonly Stream s;
        readonly byte[] buf = new byte[65536];
        int pos, len;
        public Reader(Stream s) { this.s = s; }

        public int ReadByte()
        {
            if (pos >= len) { len = s.Read(buf, 0, buf.Length); pos = 0; if (len <= 0) return -1; }
            return buf[pos++];
        }

        public bool ReadExact(byte[] dst, int count)
        {
            int off = 0;
            while (off < count)
            {
                if (pos >= len) { len = s.Read(buf, 0, buf.Length); pos = 0; if (len <= 0) return false; }
                int n = Math.Min(count - off, len - pos);
                Buffer.BlockCopy(buf, pos, dst, off, n); pos += n; off += n;
            }
            return true;
        }

        public string ReadLine()
        {
            var sb = new StringBuilder();
            int b;
            while ((b = ReadByte()) >= 0)
            {
                if (b == '\n') break;
                if (b != '\r') sb.Append((char)b);
            }
            if (b < 0 && sb.Length == 0) return null;
            return sb.ToString();
        }

        // Já consumimos o 1º byte de uma resposta RTSP (não era '$'); lê e descarta o resto dela.
        public void SkipResponse(int firstByte)
        {
            var sb = new StringBuilder();
            sb.Append((char)firstByte);
            string line = sb.ToString() + ReadLineRaw();
            int contentLen = 0;
            string l;
            while ((l = ReadLine()) != null && l.Length > 0)
            {
                int c = l.IndexOf(':');
                if (c > 0 && l.Substring(0, c).Trim().ToLowerInvariant() == "content-length") int.TryParse(l.Substring(c + 1).Trim(), out contentLen);
            }
            if (contentLen > 0) { var b = new byte[contentLen]; ReadExact(b, contentLen); }
        }

        string ReadLineRaw() { return ReadLine() ?? ""; }
    }

    // Autenticação Digest para RTSP (MD5, com ou sem qop=auth).
    class DigestRtsp
    {
        readonly string user, pass;
        string realm, nonce, qop, opaque;
        int nc;

        public DigestRtsp(string user, string pass) { this.user = user; this.pass = pass; }

        public void Challenge(string www)
        {
            if (www == null) return;
            realm = Extract(www, "realm");
            nonce = Extract(www, "nonce");
            qop = Extract(www, "qop");
            opaque = Extract(www, "opaque");
            nc = 0;
        }

        public string Header(string method, string uri)
        {
            if (nonce == null) return null;
            string ha1 = Md5(user + ":" + realm + ":" + pass);
            string ha2 = Md5(method + ":" + uri);
            var sb = new StringBuilder("Authorization: Digest username=\"" + user + "\", realm=\"" + realm + "\", nonce=\"" + nonce + "\", uri=\"" + uri + "\", algorithm=MD5");
            if (!string.IsNullOrEmpty(qop))
            {
                string ncv = (++nc).ToString("x8");
                string cnonce = Md5(Environment.TickCount + ":" + uri).Substring(0, 16);
                string response = Md5(ha1 + ":" + nonce + ":" + ncv + ":" + cnonce + ":auth:" + ha2);
                sb.Append(", response=\"" + response + "\", qop=auth, nc=" + ncv + ", cnonce=\"" + cnonce + "\"");
            }
            else
            {
                sb.Append(", response=\"" + Md5(ha1 + ":" + nonce + ":" + ha2) + "\"");
            }
            if (opaque != null) sb.Append(", opaque=\"" + opaque + "\"");
            sb.Append("\r\n");
            return sb.ToString();
        }

        static string Extract(string s, string key)
        {
            int i = s.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length + 1;
            if (i < s.Length && s[i] == '"') { int e = s.IndexOf('"', i + 1); return e < 0 ? null : s.Substring(i + 1, e - i - 1); }
            int end = i; while (end < s.Length && s[end] != ',' && s[end] != ' ') end++;
            return s.Substring(i, end - i);
        }

        static string Md5(string text)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }

    static class Native
    {
        public const int GWL_HWNDPARENT = -8;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int SW_HIDE = 0;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int idx, int val);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    }
}
