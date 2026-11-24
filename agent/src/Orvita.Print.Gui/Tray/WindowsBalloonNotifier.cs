using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orvita.Print.Gui.Tray;

/// <summary>
/// Notificaciones de globo del tray (GUI-03, Doc/08 §4.6) — P/Invoke de Shell_NotifyIconW
/// sobre una entrada de notificación PROPIA e invisible (icono transparente): Avalonia 11
/// no expone globos en su <c>TrayIcon</c>. La ventana oculta se crea en el hilo de UI (el
/// bucle de mensajes de Avalonia bombea los callbacks). Best-effort: si el globo falla, la
/// GUI sigue; NUNCA rompe por una notificación.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBalloonNotifier : IDisposable
{
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifInfo = 0x10;
    private const uint NiifNone = 0x00;
    private const uint NiifInfo = 0x01;
    private const uint NiifError = 0x03;

    private const int WmApp = 0x0400; // WM_APP: callbacks del globo (no los consumimos)

    private readonly uint _id = (uint)Random.Shared.Next(1, short.MaxValue);
    private IntPtr _window;
    private IntPtr _transparentIcon;
    private bool _added;

    public void Show(string title, string message, bool error)
    {
        try
        {
            EnsureIcon();
            if (_window == IntPtr.Zero)
            {
                return;
            }

            var data = new NotifyIconData
            {
                cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                hWnd = _window,
                uID = _id,
                uFlags = NifMessage | NifInfo,
                uCallbackMessage = WmApp,
                szInfoTitle = title,
                szInfo = message,
                dwInfoFlags = error ? NiifError : NiifInfo,
            };

            Shell_NotifyIconW(_added ? NimModify : NimAdd, ref data);
            _added = true;
        }
        catch (Exception)
        {
            // Notificación best-effort (Doc/08 §9: la bandeja no es crítica).
        }
    }

    /// <summary>Crea la ventana oculta + entrada de notificación invisible (una sola vez).</summary>
    private void EnsureIcon()
    {
        if (_window != IntPtr.Zero)
        {
            return;
        }

        _transparentIcon = CreateTransparentIcon();
        var wndClass = "OrvitaPrintGuiBalloon";
        var hInstance = GetModuleHandleW(null);

        var wc = new WndClassW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(DefProc),
            lpszClassName = wndClass,
            hInstance = hInstance,
        };
        _ = RegisterClassW(ref wc);

        // WS_OVERLAPPED sin WS_VISIBLE: existe para recibir callbacks, nunca se muestra.
        _window = CreateWindowExW(0, wndClass, "Orvita Print GUI", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            return;
        }

        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window,
            uID = _id,
            uFlags = NifMessage | NifIcon,
            uCallbackMessage = WmApp,
            hIcon = _transparentIcon,
        };
        _added = Shell_NotifyIconW(NimAdd, ref data);
    }

    /// <summary>Icono monocromo totalmente transparente (la entrada de notificación no se ve).</summary>
    private static IntPtr CreateTransparentIcon()
    {
        var andBits = new byte[64];  // máscara AND a 1 → transparente (16x16 bits, 1 bpp)
        var xorBits = new byte[128]; // XOR a 0
        for (var i = 0; i < andBits.Length; i++)
        {
            andBits[i] = 0xFF;
        }

        return CreateIcon(IntPtr.Zero, 16, 16, 1, 1, andBits, xorBits);
    }

    public void Dispose()
    {
        try
        {
            if (_added && _window != IntPtr.Zero)
            {
                var data = new NotifyIconData
                {
                    cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                    hWnd = _window,
                    uID = _id,
                };
                _ = Shell_NotifyIconW(NimDelete, ref data);
            }
        }
        catch (Exception)
        {
            // nada que hacer al salir
        }
        finally
        {
            if (_window != IntPtr.Zero)
            {
                _ = DestroyWindow(_window);
                _window = IntPtr.Zero;
            }

            if (_transparentIcon != IntPtr.Zero)
            {
                _ = DestroyIcon(_transparentIcon);
                _transparentIcon = IntPtr.Zero;
            }
        }
    }

    // ------------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip = string.Empty;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo = string.Empty;
        public uint uVersion; // unión con uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle = string.Empty;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;

        public NotifyIconData()
        {
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszClassName;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Procedimiento de ventana por defecto; el delegado SE MANTIENE vivo (estático):</summary>
    private static readonly WndProc DefProc = DefWindowProc;

    private static IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) => IntPtr.Zero;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ushort RegisterClassW(ref WndClassW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int width, int height,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateIcon(
        IntPtr hInstance, int nWidth, int nHeight, byte cPlanes, byte cBitsPixel,
        byte[] andBits, byte[] xorBits);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
