using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using SocWsManager.Localization;
using SocWsManager.Scm;
using SocWsManager.Services;

namespace SocWsManager.App.Services;

/// <summary>
/// El icono permanente del área de notificación (RF-30, RF-31): Shell_NotifyIcon sobre una ventana
/// de mensajes propia (vive aunque la ventana principal esté cerrada) y un menú de WPF con un
/// apartado por servicio de WSManager, su punto de color y sus acciones.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmTray = 0x8001;
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource _source;
    private readonly Func<ContextMenu> _buildMenu;
    private readonly Action _open;
    private bool _added;
    private string _tip = string.Empty;

    public TrayIcon(Func<ContextMenu> buildMenu, Action open)
    {
        _buildMenu = buildMenu;
        _open = open;
        // Ventana solo de mensajes (no se ve): recibe los clics del icono y el «enséñate» de otra instancia.
        _source = new HwndSource(new HwndSourceParameters("sOCWSManagerTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(Hook);
        Add();
    }

    /// <summary>Lo que se ve al pasar el ratón (servicios y cuántos en marcha).</summary>
    public void SetTip(string tip)
    {
        _tip = tip.Length > 127 ? tip[..127] : tip;
        if (!_added)
            return;
        var data = Data();
        data.uFlags = 0x4;   // NIF_TIP
        data.szTip = _tip;
        Shell_NotifyIcon(1, ref data);
    }

    /// <summary>Un globo (RF-32: un servicio que se ha parado sin pedirlo).</summary>
    public void Notify(string title, string text)
    {
        if (!_added)
            return;
        var data = Data();
        data.uFlags = 0x10;   // NIF_INFO
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = 0x2;   // NIIF_WARNING
        Shell_NotifyIcon(1, ref data);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTray)
        {
            var evt = (int)((long)lParam & 0xFFFF);
            if (evt == WmLButtonUp)
                _open();
            else if (evt is WmRButtonUp or WmContextMenu)
                ShowMenu();
            handled = true;
        }
        else if ((uint)msg == SingleInstance.ShowMessage)
        {
            _open();
            SingleInstance.NotifyShown();
            handled = true;
        }
        else if ((uint)msg == TaskbarCreated)
        {
            // El Explorador se ha reiniciado: el icono hay que volver a ponerlo.
            _added = false;
            Add();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = _buildMenu();
        menu.Placement = PlacementMode.MousePoint;
        menu.Opened += (_, _) =>
        {
            // Sin primer plano el menú no se cierra al pinchar fuera.
            if (PresentationSource.FromVisual(menu) is HwndSource popup)
                SetForegroundWindow(popup.Handle);
        };
        menu.IsOpen = true;
    }

    private void Add()
    {
        if (_added)
            return;
        var data = Data();
        data.uFlags = 0x1 | 0x2 | 0x4;
        data.uCallbackMessage = WmTray;
        data.hIcon = LoadAppIcon();
        data.szTip = _tip.Length > 0 ? _tip : Loc.Get("AppTitle");
        _added = Shell_NotifyIcon(0, ref data);
    }

    private NotifyIconData Data() => new() { cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = _source.Handle, uID = 1 };

    private static IntPtr LoadAppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } path)
            {
                var large = new IntPtr[1];
                var small = new IntPtr[1];
                if (ExtractIconEx(path, 0, large, small, 1) > 0 && small[0] != IntPtr.Zero)
                    return small[0];
            }
        }
        catch (Exception) { }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data();
            Shell_NotifyIcon(2, ref data);
            _added = false;
        }
        _source.Dispose();
    }

    // ------------------------------------------------------------------ piezas del menú

    /// <summary>Un elemento del menú con su icono plano (glifo de Segoe Fluent Icons).</summary>
    public static MenuItem Item(string text, string glyph, Action action, bool enabled = true, bool danger = false)
    {
        var item = new MenuItem
        {
            Header = text,
            Style = (Style)Application.Current.FindResource("TrayItem"),
            IsEnabled = enabled,
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 14,
                Foreground = (Brush)Application.Current.FindResource(danger ? "Danger" : "PrimaryText"),
            },
        };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>El punto de color del estado (verde, gris, ámbar o rojo).</summary>
    public static Ellipse Dot(ServiceState state, bool unexpected) => new()
    {
        Width = 10,
        Height = 10,
        Fill = (Brush)Application.Current.FindResource(StateColorKey(state, unexpected)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static string StateColorKey(ServiceState state, bool unexpected) => state switch
    {
        ServiceState.Running => "Success",
        ServiceState.Stopped => unexpected ? "Danger" : "Neutral",
        _ => "Warning",
    };

    public static Separator Separator() => new() { Style = (Style)Application.Current.FindResource("TraySeparator") };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, int count);
}
