using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Bluggle.Bluetooth;
using Bluggle.Services;
using static Bluggle.Bluetooth.NativeMethods;

namespace Bluggle.Views;

public partial class WidgetWindow : Window
{
    private readonly WidgetController _controller;
    private readonly Storyboard _busySpin;
    private readonly Storyboard _busyPulse;
    private readonly DispatcherTimer _topmostGuard;

    // Every element that changes colour gets its own mutable brush so the colour itself can be
    // animated. Brushes pulled from a ResourceDictionary are frozen and cannot be.
    private readonly SolidColorBrush _iconStroke = new(Colors.Transparent);
    private readonly SolidColorBrush _cupFill = new(Colors.Transparent);
    private readonly SolidColorBrush _chipFill = new(ChipBase);
    private readonly SolidColorBrush _chipStroke = new(Colors.Transparent);
    private readonly SolidColorBrush _ringStroke = new(Colors.Transparent);

    private IntPtr _hwnd;
    private SettingsWindow? _settingsWindow;
    private WidgetState _lastState = WidgetState.NoDevice;
    private bool _hovered;
    private bool _busyRunning;

    // Drag tracking, all in physical pixels (see MonitorHelper for why).
    private bool _pointerDown;
    private bool _dragging;
    private POINT _dragOriginCursor;
    private POINT _dragOriginWindow;

    /// <summary>Movement, in DIPs, that turns a click into a drag.</summary>
    private const double DragThresholdDip = 5.0;

    // Palette. A constant dark chip keeps the icon readable over both a white document and a
    // dark wallpaper; the state is carried by icon colour, cup fill, glow and window opacity.
    private static readonly Color ChipBase = Color.FromArgb(0xB2, 0x10, 0x12, 0x15);
    private static readonly Color DisconnectedColor = Color.FromRgb(0x9A, 0xA0, 0xA6);
    private static readonly Color BusyColor = Color.FromRgb(0x6F, 0xB4, 0xFF);
    private static readonly Color ErrorColor = Color.FromRgb(0xFF, 0x6B, 0x6B);
    private static readonly Color UnavailableColor = Color.FromRgb(0x6B, 0x70, 0x75);

    private Color _accent = Color.FromRgb(0x4C, 0xC3, 0x8A);

    public WidgetWindow(WidgetController controller)
    {
        _controller = controller;
        InitializeComponent();

        _busySpin = (Storyboard)FindResource("BusySpin");
        _busyPulse = (Storyboard)FindResource("BusyPulse");

        Band.Stroke = _iconStroke;
        CupLeft.Stroke = _iconStroke;
        CupRight.Stroke = _iconStroke;
        CupLeft.Fill = _cupFill;
        CupRight.Fill = _cupFill;
        Chip.Background = _chipFill;
        Chip.BorderBrush = _chipStroke;
        BusyRing.Stroke = _ringStroke;

        ApplyConfigAppearance();

        _controller.Changed += (_, _) => ApplyVisualState();
        _controller.Store.SaveFailed += (_, message) => _controller.ShowError(message);

        // Some apps assert topmost aggressively; re-stating ours periodically is cheaper than
        // trying to detect being covered.
        _topmostGuard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _topmostGuard.Tick += (_, _) => ReassertTopmost();
        _topmostGuard.Start();

        MouseDown += OnWidgetMouseDown;
        MouseMove += OnWidgetMouseMove;
        MouseUp += OnWidgetMouseUp;
        LostMouseCapture += (_, _) => ResetPointer();
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
    }

    private AppConfig Config => _controller.Config;

    /// <summary>Applies size, accent colour and idle opacity from config. Safe to re-run.</summary>
    public void ApplyConfigAppearance()
    {
        Width = Config.WidgetSize;
        Height = Config.WidgetSize;

        try
        {
            if (ColorConverter.ConvertFromString(Config.AccentColor) is Color parsed)
                _accent = parsed;
        }
        catch (FormatException)
        {
            // Keep the default green if the hand-edited value is not a colour.
        }

        ApplyVisualState();
    }

    // -------------------------------------------------------------- window setup ----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_TOOLWINDOW keeps the widget out of Alt+Tab. WS_EX_NOACTIVATE additionally
        // stops a click from pulling focus off the user's current app, but it also makes the
        // right-click menu dismiss less reliably, so it stays opt-in.
        long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TOOLWINDOW;
        if (Config.NoActivate) exStyle |= WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        RestorePosition();
    }

    /// <summary>
    /// Places the window at the saved physical-pixel position, pulled back onto a monitor that
    /// currently exists. Runs before the window is shown, so there is no visible jump.
    /// </summary>
    private void RestorePosition()
    {
        if (!GetWindowRect(_hwnd, out RECT current)) return;

        int width = current.Width;
        int height = current.Height;

        int x, y;
        if (Config.WindowX is int savedX && Config.WindowY is int savedY)
        {
            x = savedX;
            y = savedY;
        }
        else
        {
            (x, y) = MonitorHelper.DefaultPosition(width, height);
        }

        RECT target = MonitorHelper.ClampToVisibleWorkArea(new RECT
        {
            Left = x,
            Top = y,
            Right = x + width,
            Bottom = y + height,
        });

        MoveTo(target.Left, target.Top);
        StorePosition(target.Left, target.Top, immediate: true);
    }

    private void MoveTo(int x, int y) =>
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);

    private void ReassertTopmost()
    {
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void StorePosition(int x, int y, bool immediate = false)
    {
        if (Config.WindowX == x && Config.WindowY == y) return;

        Config.WindowX = x;
        Config.WindowY = y;

        if (immediate) _controller.Store.SaveNow();
        else _controller.Store.SaveDebounced();
    }

    // -------------------------------------------------------------------- input ----

    private void OnWidgetMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _pointerDown = true;
        _dragging = false;

        GetCursorPos(out _dragOriginCursor);
        if (GetWindowRect(_hwnd, out RECT rect))
            _dragOriginWindow = new POINT { X = rect.Left, Y = rect.Top };

        CaptureMouse();
        e.Handled = true;
    }

    private void OnWidgetMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pointerDown) return;

        GetCursorPos(out POINT now);
        int dx = now.X - _dragOriginCursor.X;
        int dy = now.Y - _dragOriginCursor.Y;

        if (!_dragging)
        {
            // Threshold expressed in DIPs but compared in physical pixels, so the feel is the
            // same at 100% and 200% scaling.
            double thresholdPx = DragThresholdDip * VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (Math.Abs(dx) < thresholdPx && Math.Abs(dy) < thresholdPx) return;
            _dragging = true;
        }

        MoveTo(_dragOriginWindow.X + dx, _dragOriginWindow.Y + dy);
    }

    private void OnWidgetMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            ShowContextMenu();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left || !_pointerDown) return;

        bool wasDrag = _dragging;
        ResetPointer();
        e.Handled = true;

        if (wasDrag)
        {
            // Snap back on-screen in case the drag ended past an edge or on a monitor that is
            // about to go away, then persist wherever it settled.
            if (GetWindowRect(_hwnd, out RECT rect))
            {
                RECT clamped = MonitorHelper.ClampToVisibleWorkArea(rect);
                if (clamped.Left != rect.Left || clamped.Top != rect.Top)
                    MoveTo(clamped.Left, clamped.Top);
                StorePosition(clamped.Left, clamped.Top);
            }
            return;
        }

        _ = _controller.ToggleAsync();
    }

    private void ResetPointer()
    {
        _pointerDown = false;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void SetHover(bool hovered)
    {
        _hovered = hovered;
        AnimateDouble(HoverScale, ScaleTransform.ScaleXProperty, hovered ? 1.08 : 1.0, 140);
        AnimateDouble(HoverScale, ScaleTransform.ScaleYProperty, hovered ? 1.08 : 1.0, 140);
        AnimateWindowOpacity(hovered ? 1.0 : IdleOpacityForState(_controller.State));
    }

    // --------------------------------------------------------------- context menu ----

    private void ShowContextMenu() => BuildContextMenu().IsOpen = true;

    /// <summary>
    /// Builds the menu fresh on every right-click. Rebuilding is cheaper than keeping check
    /// marks, the device list and the startup state in sync with the world.
    /// </summary>
    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = this,
            Placement = PlacementMode.MousePoint,
        };

        menu.Items.Add(new MenuItem
        {
            Header = Escape(_controller.StatusText),
            IsEnabled = false,
        });
        menu.Items.Add(new Separator());

        IEnumerable<PairedDevice> devices = Config.ShowAllPairedDevices
            ? _controller.Devices
            : _controller.Devices.Where(d => d.IsAudioDevice);

        var list = devices.ToList();
        if (list.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = Config.ShowAllPairedDevices
                    ? "No paired devices found"
                    : "No paired audio devices found",
                IsEnabled = false,
            });
        }
        else
        {
            ulong selected = Config.DeviceAddressValue;
            foreach (PairedDevice device in list)
            {
                var item = new MenuItem
                {
                    Header = Escape(device.DisplayName) + (device.IsConnected ? "   ●" : string.Empty),
                    IsCheckable = true,
                    IsChecked = device.Address == selected,
                    ToolTip = $"{device.AddressText}  (CoD 0x{device.ClassOfDevice:X6})",
                };
                PairedDevice captured = device;
                item.Click += (_, _) => _controller.SelectDevice(captured);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());

        menu.Items.Add(MakeItem("Refresh devices", async () =>
        {
            await _controller.RefreshDevicesAsync();
        }));

        var showAll = new MenuItem
        {
            Header = "Show all paired devices",
            IsCheckable = true,
            IsChecked = Config.ShowAllPairedDevices,
            ToolTip = "Useful when a headset reports an unusual device class and is filtered out.",
        };
        showAll.Click += (_, _) =>
        {
            Config.ShowAllPairedDevices = !Config.ShowAllPairedDevices;
            _controller.Store.SaveNow();
        };
        menu.Items.Add(showAll);

        var startup = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = StartupManager.IsEnabled(),
        };
        startup.Click += (_, _) =>
        {
            bool enable = !StartupManager.IsEnabled();
            string? error = StartupManager.SetEnabled(enable);
            if (error is not null)
            {
                _controller.ShowError(error);
                return;
            }
            Config.StartWithWindows = enable;
            _controller.Store.SaveNow();
        };
        menu.Items.Add(startup);

        menu.Items.Add(MakeItem("Settings...", ShowSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Exit", () => Application.Current.Shutdown()));

        return menu;
    }

    private static MenuItem MakeItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static MenuItem MakeItem(string header, Func<Task> onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => _ = onClick();
        return item;
    }

    /// <summary>Menu headers treat "_" as an access-key marker; doubling it shows a literal one.</summary>
    private static string Escape(string text) => text.Replace("_", "__");

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_controller);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ApplyConfigAppearance();
            _controller.ApplyConfigChanges();
        };
        _settingsWindow.Show();
    }

    // ------------------------------------------------------------- state visuals ----

    private double IdleOpacityForState(WidgetState state) => state switch
    {
        WidgetState.Connected => 1.0,
        WidgetState.Connecting or WidgetState.Disconnecting => 0.95,
        WidgetState.Error => 1.0,
        WidgetState.Unavailable or WidgetState.NoDevice => Math.Clamp(Config.IdleOpacity * 0.85, 0.1, 1.0),
        _ => Config.IdleOpacity,
    };

    private void ApplyVisualState()
    {
        WidgetState state = _controller.State;

        Color iconColor;
        Color borderColor;
        double glow;
        bool filledCups;
        bool busy;

        switch (state)
        {
            case WidgetState.Connected:
                iconColor = _accent;
                borderColor = WithAlpha(_accent, 0xAA);
                glow = 0.9;
                filledCups = true;
                busy = false;
                break;

            case WidgetState.Connecting:
            case WidgetState.Disconnecting:
                iconColor = BusyColor;
                borderColor = WithAlpha(BusyColor, 0x88);
                glow = 0.45;
                filledCups = false;
                busy = true;
                break;

            case WidgetState.Error:
                iconColor = ErrorColor;
                borderColor = WithAlpha(ErrorColor, 0xCC);
                glow = 0.75;
                filledCups = false;
                busy = false;
                break;

            case WidgetState.Unavailable:
            case WidgetState.NoDevice:
                iconColor = UnavailableColor;
                borderColor = Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF);
                glow = 0;
                filledCups = false;
                busy = false;
                break;

            default: // Disconnected
                iconColor = DisconnectedColor;
                borderColor = Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF);
                glow = 0;
                filledCups = false;
                busy = false;
                break;
        }

        AnimateColor(_iconStroke, iconColor);
        AnimateColor(_cupFill, filledCups ? iconColor : WithAlpha(iconColor, 0x00));
        AnimateColor(_chipStroke, borderColor);
        AnimateColor(_ringStroke, iconColor);

        ChipGlow.Color = iconColor;
        AnimateDouble(ChipGlow, DropShadowEffect.OpacityProperty, glow);
        AnimateDouble(BusyRing, OpacityProperty, busy ? 1.0 : 0.0);

        if (busy) StartBusyAnimation();
        else StopBusyAnimation();

        if (state == WidgetState.Error && _lastState != WidgetState.Error) FlashError();

        AnimateWindowOpacity(_hovered ? 1.0 : IdleOpacityForState(state));
        ToolTip = _controller.StatusText;
        _lastState = state;
    }

    private void StartBusyAnimation()
    {
        if (_busyRunning) return;
        _busyRunning = true;
        _busySpin.Begin(this, isControllable: true);
        _busyPulse.Begin(this, isControllable: true);
    }

    private void StopBusyAnimation()
    {
        if (!_busyRunning) return;
        _busyRunning = false;
        _busySpin.Stop(this);
        _busyPulse.Stop(this);
        IconHost.Opacity = 1.0;
    }

    /// <summary>
    /// Two red pulses across the chip. FillBehavior.Stop lets the brush snap back to its base
    /// colour on its own, so nothing has to un-set it afterwards.
    /// </summary>
    private void FlashError()
    {
        var flash = new ColorAnimation
        {
            To = Color.FromArgb(0xE0, 0x7A, 0x1F, 0x24),
            Duration = TimeSpan.FromMilliseconds(180),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            FillBehavior = FillBehavior.Stop,
        };
        _chipFill.BeginAnimation(SolidColorBrush.ColorProperty, flash);
    }

    private static void AnimateColor(SolidColorBrush brush, Color to, int milliseconds = 180)
    {
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
        });
    }

    private static void AnimateDouble(
        System.Windows.Media.Animation.IAnimatable target,
        DependencyProperty property,
        double to,
        int milliseconds = 180)
    {
        target.BeginAnimation(property, new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
        });
    }

    private void AnimateWindowOpacity(double to) =>
        AnimateDouble(this, OpacityProperty, Math.Clamp(to, 0.05, 1.0), 160);

    // ------------------------------------------------------------------ shutdown ----

    protected override void OnClosed(EventArgs e)
    {
        _topmostGuard.Stop();

        if (_hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out RECT rect))
            StorePosition(rect.Left, rect.Top, immediate: true);

        base.OnClosed(e);
    }

    /// <summary>Opens the config folder in Explorer. Used from the settings window.</summary>
    public void OpenConfigFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_controller.Store.Directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _controller.Store.Directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _controller.ShowError($"Could not open the config folder: {ex.Message}");
        }
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}
