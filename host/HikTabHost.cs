// HikTab Host - motor do Internet Explorer (WebBrowser/Trident) embutido numa aba do Chrome.
// Comunica-se com a extensão via Native Messaging (stdin/stdout, mensagens JSON com prefixo de 4 bytes).
// Compilar (x86, para compatibilidade com plugins ActiveX 32 bits): ver install.ps1.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
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
        static void Main()
        {
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
