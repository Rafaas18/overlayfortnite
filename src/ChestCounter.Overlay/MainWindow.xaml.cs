using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ChestCounter.Overlay;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0x0C0F;
    private const uint VkL = 0x4C;

    private readonly Settings _settings = Settings.Load();
    private readonly MatchStore _store = new();
    private readonly ReplayScanner _scanner;
    private readonly OverlayViewModel _view = new();
    private readonly DateTime _sessionStartUtc = DateTime.UtcNow;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly System.Windows.Forms.NotifyIcon _tray = new();

    private IntPtr _handle;
    private bool _scanning;
    private bool _hotkeyRegistered;
    private DateTime _lastScanEndUtc = DateTime.MinValue;
    private string? _scanError;

    public MainWindow()
    {
        InitializeComponent();

        _scanner = new ReplayScanner(_store);
        DataContext = _view;

        Left = _settings.WindowLeft;
        Top = _settings.WindowTop;
        Opacity = Math.Clamp(_settings.Opacity, 0.3, 1.0);

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
                SaveSettings();
            }
        };

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        _timer.Tick += (_, _) => Tick();

        BuildMenus();
        RefreshView();
        _timer.Start();
        Tick();
    }

    // ---- window plumbing -------------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_handle)?.AddHook(WndProc);

        _hotkeyRegistered = NativeMethods.RegisterHotKey(_handle, HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, VkL);

        ApplyClickThrough();
        KeepOnScreen();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleClickThrough();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ApplyClickThrough()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        // Tool window: no Alt+Tab entry. Transparent: mouse clicks go to the game underneath.
        var style = NativeMethods.GetExStyle(_handle) | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_LAYERED;
        style = _settings.ClickThrough
            ? style | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE
            : style & ~NativeMethods.WS_EX_TRANSPARENT & ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetExStyle(_handle, style);
    }

    private void ToggleClickThrough()
    {
        _settings.ClickThrough = !_settings.ClickThrough;
        ApplyClickThrough();
        SaveSettings();
        BuildMenus();
    }

    private void KeepOnScreen()
    {
        var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60;
        var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
        if (Left < SystemParameters.VirtualScreenLeft || Left > maxLeft || Top < SystemParameters.VirtualScreenTop || Top > maxTop)
        {
            Left = 40;
            Top = 40;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_handle, HotkeyId);
        }

        _tray.Visible = false;
        _tray.Dispose();
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            _view.StatusText = "No se pudo guardar la configuración: " + ex.Message;
        }
    }

    // ---- scanning --------------------------------------------------------------------------

    private void Tick()
    {
        // Games sometimes push themselves above topmost windows, so claim the top again.
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(_handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        RefreshView();

        if (_scanning || (DateTime.UtcNow - _lastScanEndUtc).TotalSeconds < 5)
        {
            return;
        }

        _scanning = true;
        var folder = _settings.EffectiveReplayFolder;
        var liveRefresh = _settings.LiveRefreshSeconds;

        Task.Run(() =>
        {
            // Reading a replay is CPU heavy: stay out of the game's way.
            var thread = Thread.CurrentThread;
            var previous = thread.Priority;
            thread.Priority = ThreadPriority.BelowNormal;
            try
            {
                _scanner.ScanOnce(folder, liveRefresh);
                return (string?)null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                thread.Priority = previous;
            }
        }).ContinueWith(task => Dispatcher.InvokeAsync(() =>
        {
            _scanError = task.IsFaulted ? task.Exception?.GetBaseException().Message : task.Result;
            _lastScanEndUtc = DateTime.UtcNow;
            _scanning = false;
            RefreshView();
        }));
    }

    private void RefreshView()
    {
        var matches = _store.All();
        _view.Update(matches, _settings, _sessionStartUtc);

        var folder = _settings.EffectiveReplayFolder;
        if (_scanner.CurrentFile is { } current)
        {
            _view.StatusText = "Leyendo " + current + "…";
        }
        else if (_scanError is not null)
        {
            _view.StatusText = "Error al leer replays: " + _scanError;
        }
        else if (!System.IO.Directory.Exists(folder))
        {
            _view.StatusText = "No existe la carpeta de replays: " + folder;
        }
        else if (matches.Count == 0)
        {
            _view.StatusText = "Sin replays todavía. Activa «Grabar repeticiones» en Fortnite y juega una partida.";
        }
        else
        {
            _view.StatusText = _settings.ClickThrough
                ? "Bloqueado · Ctrl+Alt+L para desbloquear"
                : "Clic derecho: opciones · arrastra para mover";
        }
    }

    // ---- menus -----------------------------------------------------------------------------

    private void BuildMenus()
    {
        // WPF menu for right click on the overlay.
        var menu = new ContextMenu();
        menu.Items.Add(Item("Ajustar total…", AdjustTotal));

        var mode = new MenuItem { Header = "Qué cofres contar" };
        mode.Items.Add(ModeItem("Automático", CountMode.Auto));
        mode.Items.Add(ModeItem("Solo los de mi squad (si el replay lo indica)", CountMode.Squad));
        mode.Items.Add(ModeItem("Abiertos a la vista", CountMode.Live));
        mode.Items.Add(ModeItem("Todos los que se ven abiertos", CountMode.All));
        menu.Items.Add(mode);

        menu.Items.Add(Item("Patrón de cofres…", EditPattern));
        menu.Items.Add(Item("Ver partidas…", () => DetailsWindow.Show(_store.All(), _settings)));
        menu.Items.Add(new Separator());

        var opacity = new MenuItem { Header = "Opacidad" };
        foreach (var value in new[] { 1.0, 0.9, 0.75, 0.5 })
        {
            var v = value;
            opacity.Items.Add(new MenuItem
            {
                Header = $"{v:P0}",
                IsChecked = Math.Abs(_settings.Opacity - v) < 0.01,
                Command = new ActionCommand(() => SetOpacity(v))
            });
        }

        menu.Items.Add(opacity);
        menu.Items.Add(new MenuItem
        {
            Header = "Bloquear (los clics pasan al juego)",
            InputGestureText = "Ctrl+Alt+L",
            IsChecked = _settings.ClickThrough,
            Command = new ActionCommand(ToggleClickThrough)
        });
        menu.Items.Add(Item("Abrir carpeta de datos", OpenDataFolder));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Salir", Close));
        ContextMenu = menu;

        // Tray menu: the only way in with the mouse while the overlay is locked.
        var tray = new System.Windows.Forms.ContextMenuStrip();
        tray.Items.Add(_settings.ClickThrough ? "Desbloquear overlay" : "Bloquear overlay (los clics pasan al juego)", null, (_, _) => ToggleClickThrough());
        tray.Items.Add("Ajustar total…", null, (_, _) => AdjustTotal());
        tray.Items.Add("Ver partidas…", null, (_, _) => DetailsWindow.Show(_store.All(), _settings));
        tray.Items.Add("Traer overlay a la esquina", null, (_, _) => { Left = 40; Top = 40; SaveSettings(); });
        tray.Items.Add("Salir", null, (_, _) => Close());

        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = tray;
        old?.Dispose();
        _tray.Icon ??= System.Drawing.SystemIcons.Application;
        _tray.Text = "Overlay Cofres";
        _tray.Visible = true;
    }

    private static MenuItem Item(string header, Action action) => new() { Header = header, Command = new ActionCommand(action) };

    private MenuItem ModeItem(string header, CountMode mode) => new()
    {
        Header = header,
        IsChecked = _settings.Mode == mode,
        Command = new ActionCommand(() =>
        {
            _settings.Mode = mode;
            SaveSettings();
            BuildMenus();
            RefreshView();
        })
    };

    private void SetOpacity(double value)
    {
        _settings.Opacity = value;
        Opacity = value;
        SaveSettings();
        BuildMenus();
    }

    private void AdjustTotal()
    {
        var current = _settings.Baseline + _view.HistoryTotal;
        var answer = InputDialog.Ask("Ajustar total",
            "¿Cuántos cofres lleva tu squad en total hasta ahora? El overlay seguirá sumando a partir de ese número.",
            current.ToString());
        if (answer is null)
        {
            return;
        }

        var digits = answer.Replace(".", "").Replace(",", "").Replace(" ", "").Trim();
        if (!int.TryParse(digits, out var total) || total < 0)
        {
            MessageBox.Show("Escribe un número entero, por ejemplo 40000.", "Overlay Cofres");
            return;
        }

        _settings.Baseline = total - _view.HistoryTotal;
        SaveSettings();
        RefreshView();
    }

    private void EditPattern()
    {
        var answer = InputDialog.Ask("Patrón de cofres",
            "Texto (expresión regular) que debe aparecer en el nombre del contenedor para contarlo como cofre. " +
            "Mira los nombres en «Ver partidas…». Ejemplo: Chest|Treasure",
            _settings.ChestPattern);
        if (answer is null)
        {
            return;
        }

        var before = _view.HistoryTotal;
        _settings.ChestPattern = string.IsNullOrWhiteSpace(answer) ? Counting.DefaultChestPattern : answer.Trim();
        RefreshView();

        // Keep the displayed total where it was: the pattern changes what past matches add up to.
        _settings.Baseline += before - _view.HistoryTotal;
        SaveSettings();
        RefreshView();
    }

    private static void OpenDataFolder()
    {
        System.IO.Directory.CreateDirectory(Settings.DataFolder);
        Process.Start(new ProcessStartInfo(Settings.DataFolder) { UseShellExecute = true });
    }

    private sealed class ActionCommand : ICommand
    {
        private readonly Action _action;

        public ActionCommand(Action action) => _action = action;

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _action();
    }
}
