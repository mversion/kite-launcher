using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kite.App.Services;
using Kite.Core.Search;
using Kite.Plugins;

namespace Kite.App;

public partial class LauncherWindow : Window
{
    private readonly KiteServices _services;
    private readonly ObservableCollection<ResultRow> _rows = [];
    private readonly ObservableCollection<Chip> _chips = [];
    private readonly DispatcherTimer _resultsDelay;
    private CancellationTokenSource? _searchCts;
    private int _searchVersion;
    private bool _resultsAllowed = true;
    private readonly LauncherCommands _commands;
    private readonly Storyboard _spin;
    private bool _hiding;
    private bool _dragging;
    private PixelRect _workArea;

    public LauncherWindow(KiteServices services, LauncherCommands commands)
    {
        _services = services;
        _commands = commands;
        InitializeComponent();
        Results.ItemsSource = _rows;
        ChipList.ItemsSource = _chips;
        _resultsDelay = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _resultsDelay.Tick += (_, _) =>
        {
            _resultsDelay.Stop();
            _resultsAllowed = true;
            UpdateResultsVisibility();
        };

        Deactivated += (_, _) =>
        {
            if (_services.Settings.Current.General.HideOnFocusLoss && !Frame.ContextMenu.IsOpen) HideLauncher();
        };
        DpiChanged += (_, _) => { if (IsVisible && !_dragging) Dispatcher.BeginInvoke(PositionWindow, DispatcherPriority.Loaded); };
        _services.Catalog.Changed += _ => Dispatcher.BeginInvoke(() => { if (IsVisible) RunSearch(); });

        var spinAnimation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTargetName(spinAnimation, nameof(SpinnerRotation));
        Storyboard.SetTargetProperty(spinAnimation, new PropertyPath(RotateTransform.AngleProperty));
        _spin = new Storyboard { Children = { spinAnimation } };
        _services.Catalog.RebuildingChanged += busy => Dispatcher.BeginInvoke(() => SetSpinner(busy));
        SetSpinner(_services.Catalog.IsRebuilding);

        WireContextMenu();
    }

    private void SetSpinner(bool busy)
    {
        Spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) _spin.Begin(this, isControllable: true);
        else _spin.Stop(this);
    }

    // ---------------------------------------------------------------- right-click menu

    private void WireContextMenu()
    {
        var menu = Frame.ContextMenu;
        MenuItem Item(string name) => (MenuItem)LogicalTreeHelper.FindLogicalNode(menu, name);

        Item("MenuPaste").Click += (_, _) => { Input.Focus(); Input.Paste(); };
        Item("MenuRebuild").Click += (_, _) => _commands.RebuildCatalog();
        Item("MenuReloadSkin").Click += (_, _) => _commands.ReloadSkin();
        Item("MenuOptions").Click += (_, _) => _commands.OpenOptions();
        Item("MenuExit").Click += (_, _) => _commands.Exit();

        var skins = Item("MenuSkins");
        menu.Opened += (_, _) =>
        {
            Item("MenuPaste").IsEnabled = Clipboard.ContainsText();
            Item("MenuRebuild").IsEnabled = !_services.Catalog.IsRebuilding;
            skins.Items.Clear();
            var current = _services.Settings.Current.General.Skin;
            foreach (var name in _commands.ListSkins())
            {
                var item = new MenuItem { Header = name, IsChecked = name.Equals(current, StringComparison.OrdinalIgnoreCase) };
                item.Click += (_, _) => _commands.SelectSkin(name);
                skins.Items.Add(item);
            }
        };
        menu.Closed += (_, _) => { if (IsVisible) Input.Focus(); };
        Input.ContextMenu = menu; // replace the TextBox's own cut/copy/paste menu, like Launchy
    }

    public bool IsExiting { get; set; }

    /// <summary>Raised after the launcher has finished hiding.</summary>
    public event Action? Hidden;

    public event Action? Shown;

    /// <summary>Creates the native window up front so the first hotkey press is instant.</summary>
    public HwndSource EnsureHandle()
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        return HwndSource.FromHwnd(helper.Handle);
    }

    public void ApplySettings()
    {
        var g = _services.Settings.Current.General;
        Topmost = g.AlwaysOnTop;
        UpdateResultsMaxHeight();
    }

    public void UpdateResultsMaxHeight()
    {
        var rowHeight = TryFindResource("Kite.Results.RowHeight") is double h ? h : 48;
        Results.MaxHeight = (rowHeight + 2) * Math.Max(1, _services.Settings.Current.General.VisibleResults) + 2;
    }

    /// <summary>
    /// Lays out and renders the launcher (including a few result rows) once, off-screen and without
    /// taking focus, so the first real hotkey press doesn't pay for building the visual tree.
    /// </summary>
    public void Prewarm()
    {
        if (IsVisible) return;
        ShowActivated = false;
        Root.Opacity = 0;
        Left = -32000;
        Top = -32000;
        var sample = _services.Catalog.Current.Entries.Take(3).Select(e => e.Item).ToList();
        Display(sample);
        _resultsAllowed = true;
        ResultsPanel.Visibility = Visibility.Visible;
        Show();
        UpdateLayout();
        Hide();
        ShowActivated = true;
        Display([]);
    }

    // ---------------------------------------------------------------- show / hide

    public void Toggle()
    {
        if (IsVisible && IsActive && !_hiding) HideLauncher();
        else ShowLauncher();
    }

    public void ShowLauncher()
    {
        _hiding = false;
        var shownAt = System.Diagnostics.Stopwatch.StartNew();
        var g = _services.Settings.Current.General;
        // Decide the monitor before we take focus: afterwards the foreground window is Kite itself.
        if (!IsVisible) _workArea = PickWorkArea(g.OpenOn);
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0;
        if (!IsVisible) Show();
        PositionWindow();
        Activate();
        ForceForeground();
        Input.Focus();
        Input.SelectAll();

        _resultsAllowed = g.ResultsDelayMs <= 0;
        RunSearch();

        var target = Math.Clamp(g.Opacity, 20, 100) / 100.0;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(Math.Max(1, g.FadeInMs))));
        _services.Plugins.NotifyShow();
        Shown?.Invoke();
        // Record slow opens (time to the first rendered frame, e.g. right after memory was released).
        EventHandler? firstFrame = null;
        firstFrame = (_, _) =>
        {
            System.Windows.Media.CompositionTarget.Rendering -= firstFrame;
            if (shownAt.ElapsedMilliseconds > 30) Logger.Log($"Launcher shown in {shownAt.ElapsedMilliseconds} ms");
        };
        System.Windows.Media.CompositionTarget.Rendering += firstFrame;
    }

    public void HideLauncher()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        _searchCts?.Cancel();
        var fade = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromMilliseconds(Math.Max(1, _services.Settings.Current.General.FadeOutMs)));
        fade.Completed += (_, _) =>
        {
            if (!_hiding) return; // re-shown during the fade
            Hide();
            _hiding = false;
            Hidden?.Invoke();
            _chips.Clear();
            UpdatePlaceholder();
        };
        Root.BeginAnimation(OpacityProperty, fade);
        _services.Plugins.NotifyHide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!IsExiting)
        {
            e.Cancel = true; // Alt+F4 just hides
            HideLauncher();
        }
        base.OnClosing(e);
    }

    /// <summary>The work area (device pixels) of the monitor the launcher should open on.</summary>
    private PixelRect PickWorkArea(Core.Settings.OpenOnMonitor mode)
    {
        var foreground = GetForegroundWindow();
        if (mode == Core.Settings.OpenOnMonitor.ActiveWindow && foreground != IntPtr.Zero &&
            foreground != new WindowInteropHelper(this).Handle)
            return Monitors.WorkAreaFromWindow(foreground);
        if (mode == Core.Settings.OpenOnMonitor.Primary)
            return Monitors.PrimaryWorkArea();
        var (cx, cy) = Monitors.CursorPosition();
        return Monitors.WorkAreaFromPoint(cx, cy);
    }

    /// <summary>
    /// Places the launcher on the chosen monitor: centred, or at the spot it was last dragged to,
    /// stored relative to the monitor so it lands in the same place on any screen.
    /// </summary>
    private void PositionWindow()
    {
        var g = _services.Settings.Current.General;
        var wa = _workArea.IsEmpty ? PickWorkArea(g.OpenOn) : _workArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var widthPx = (int)Math.Round((ActualWidth > 0 ? ActualWidth : Width) * dpi.DpiScaleX);
        var shadowPx = (int)Math.Round(Root.Margin.Top * dpi.DpiScaleY);

        var centreX = g.CenterHorizontally || g.PositionRelX is null
            ? wa.Left + wa.Width / 2
            : wa.Left + (int)(wa.Width * g.PositionRelX.Value);
        var frameTop = g.CenterVertically || g.PositionRelY is null
            ? wa.Top + (int)(wa.Height * 0.28)
            : wa.Top + (int)(wa.Height * g.PositionRelY.Value);

        var x = Math.Clamp(centreX - widthPx / 2, wa.Left - shadowPx, wa.Right - widthPx + shadowPx);
        var y = Math.Clamp(frameTop - shadowPx, wa.Top - shadowPx, wa.Bottom - shadowPx - 60);
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Drag the launcher by any part of the frame that isn't text or a result (the I-beam vs
    /// four-way cursor shows which); Shift+drag works anywhere. With "drag only with Shift" on,
    /// only Shift+drag moves it, like Launchy's option.
    /// </summary>
    private void Frame_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var g = _services.Settings.Current.General;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var source = e.OriginalSource as DependencyObject;
        var onContent = IsInside(source, Input) || IsInside(source, Results);
        if (!(shift || (!g.DragRequiresShift && !onContent))) return;

        e.Handled = true;
        _dragging = true;
        try { DragMove(); } catch (InvalidOperationException) { }
        finally { _dragging = false; }
        SaveDraggedPosition();
        Input.Focus();
    }

    private void SaveDraggedPosition()
    {
        if (!GetWindowRect(new WindowInteropHelper(this).Handle, out var r)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var shadowPx = (int)Math.Round(Root.Margin.Top * dpi.DpiScaleY);
        var wa = Monitors.WorkAreaFromRect(new PixelRect(r.Left, r.Top, r.Right, r.Bottom));
        if (wa.Width <= 0 || wa.Height <= 0) return;

        var g = _services.Settings.Current.General;
        g.PositionRelX = Math.Clamp((r.Left + r.Right) / 2.0 - wa.Left, 0, wa.Width) / wa.Width;
        g.PositionRelY = Math.Clamp(r.Top + shadowPx - wa.Top, 0, wa.Height) / (double)wa.Height;
        g.CenterHorizontally = false;
        g.CenterVertically = false;
        _workArea = wa;
        _services.Settings.Save();
    }

    private static bool IsInside(DependencyObject? node, DependencyObject container)
    {
        while (node is not null)
        {
            if (node == container) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    // ---------------------------------------------------------------- search

    private Query BuildQuery()
    {
        var text = Input.Text;
        if (_chips.Count == 0) return new Query(text);
        var args = _chips.Skip(1).Select(c => c.TypedText).ToList();
        if (text.Length > 0) args.Add(text);
        return new Query(text, _chips[0].Item, args);
    }

    private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _userPickedRow = false;
        if (Input.Text.Contains('\t'))
        {
            SplitPastedSegments();
            return;
        }
        UpdatePlaceholder();
        var delay = _services.Settings.Current.General.ResultsDelayMs;
        if (!_resultsAllowed && delay > 0 && !_resultsDelay.IsEnabled)
        {
            _resultsDelay.Interval = TimeSpan.FromMilliseconds(delay);
            _resultsDelay.Start();
        }
        RunSearch();
    }

    private void UpdatePlaceholder() =>
        Placeholder.Visibility = Input.Text.Length == 0 && _chips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Set when the user moves the highlight; cleared whenever the query text changes.</summary>
    private bool _userPickedRow;

    private async void RunSearch()
    {
        var version = ++_searchVersion;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        var query = BuildQuery();
        var max = Math.Max(1, _services.Settings.Current.General.MaxResults);

        if (!query.HasOwner && query.Text.Trim().Length == 0)
        {
            Display([]);
            return;
        }

        var catalog = query.HasOwner ? [] : _services.Search.SearchCatalog(query.Text, max);
        var pluginTask = _services.Search.SearchPlugins(query, ct);

        // Most plugins answer synchronously; merge in one pass to avoid a flicker.
        if (!pluginTask.IsCompleted)
            Display(Compose(query, catalog, [], max));

        try
        {
            var plugin = await pluginTask;
            if (version != _searchVersion) return;
            Display(Compose(query, catalog, plugin, max));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Log($"Search failed: {ex.Message}"); }
    }

    private static List<CatItem> Compose(Query query, List<CatItem> catalog, IReadOnlyList<CatItem> plugin, int max) =>
        query.Owner is { } owner ? SearchEngine.MergeOwner(owner, query, plugin) : SearchEngine.Merge(catalog, plugin, max);

    private void Display(IReadOnlyList<CatItem> items)
    {
        var previous = (Results.SelectedItem as ResultRow)?.Item.Key;
        _rows.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var row = new ResultRow(items[i], i);
            _rows.Add(row);
            LoadIcon(row);
        }

        // Results that arrive later (plugins, Everything) re-order the list: the highlight follows the
        // top result, unless the user picked a row with the arrow keys - then it stays on that row.
        var keep = previous is null || !_userPickedRow ? -1 : _rows.ToList().FindIndex(r => r.Item.Key == previous);
        Results.SelectedIndex = _rows.Count == 0 ? -1 : Math.Max(0, keep);
        if (Results.SelectedItem is not null) Results.ScrollIntoView(Results.SelectedItem);
        UpdateTopIcon();
        UpdateResultsVisibility();
    }

    private void LoadIcon(ResultRow row)
    {
        var path = row.Item.IconPath;
        if (string.IsNullOrEmpty(path)) return;
        var task = _services.Icons.GetAsync(path);
        if (task.IsCompletedSuccessfully) row.Icon = task.Result;
        else task.ContinueWith(t => { if (t.IsCompletedSuccessfully) row.Icon = t.Result; UpdateTopIcon(); },
                               TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdateTopIcon() => TopIcon.Source = (Results.SelectedItem as ResultRow)?.Icon;

    private void UpdateResultsVisibility()
    {
        var hasInput = Input.Text.Length > 0 || _chips.Count > 0;
        ResultsPanel.Visibility = _rows.Count > 0 && hasInput && _resultsAllowed ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- keyboard

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.Escape:
                HideLauncher();
                e.Handled = true;
                return;

            case Key.F5 when mods == ModifierKeys.Shift:
                _commands.ReloadSkin();
                e.Handled = true;
                return;

            case Key.F5:
                _commands.RebuildCatalog();
                e.Handled = true;
                return;

            case Key.OemComma when mods == ModifierKeys.Control:
                _commands.OpenOptions();
                e.Handled = true;
                return;

            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                return;

            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                return;

            case Key.PageDown:
                MoveSelection(+Math.Max(1, _services.Settings.Current.General.VisibleResults));
                e.Handled = true;
                return;

            case Key.PageUp:
                MoveSelection(-Math.Max(1, _services.Settings.Current.General.VisibleResults));
                e.Handled = true;
                return;

            case Key.Enter:
                LaunchSelected(ModeFor(mods));
                e.Handled = true;
                return;

            case Key.Tab:
                if (mods == ModifierKeys.None) Complete();
                e.Handled = true;
                return;

            case Key.Back when Input.CaretIndex == 0 && Input.SelectionLength == 0 && _chips.Count > 0:
                PopChip();
                e.Handled = true;
                return;

            case >= Key.D1 and <= Key.D9 when mods == ModifierKeys.Alt:
                var index = key - Key.D1;
                if (index < _rows.Count)
                {
                    Results.SelectedIndex = index;
                    LaunchSelected(LaunchMode.Default);
                }
                e.Handled = true;
                return;
        }
    }

    private static LaunchMode ModeFor(ModifierKeys mods) => mods switch
    {
        ModifierKeys.Control | ModifierKeys.Shift => LaunchMode.RunAsAdmin,
        ModifierKeys.Control => LaunchMode.OpenContainingFolder,
        ModifierKeys.Shift => LaunchMode.Alternate,
        _ => LaunchMode.Default,
    };

    private void MoveSelection(int delta)
    {
        _userPickedRow = true;
        if (_rows.Count == 0) return;
        _resultsAllowed = true;
        var i = Results.SelectedIndex + delta;
        if (Math.Abs(delta) == 1) i = (i + _rows.Count) % _rows.Count; // wrap with arrows
        else i = Math.Clamp(i, 0, _rows.Count - 1);
        Results.SelectedIndex = i;
        Results.ScrollIntoView(Results.SelectedItem);
        UpdateTopIcon();
        UpdateResultsVisibility();
    }

    /// <summary>
    /// Tab, Launchy-style: the highlighted item becomes the owner chip and further text is passed
    /// to it ("email ⇥ bob@x.com", "cmd ⇥ dir"). Under an owner, Tab completes a suggestion or
    /// starts another argument.
    /// </summary>
    private void Complete()
    {
        var selected = (Results.SelectedItem as ResultRow)?.Item;
        if (_chips.Count == 0)
        {
            if (selected is null) return;
            _chips.Add(new Chip(selected, Input.Text));
            SetInput("");
            return;
        }

        var owner = _chips[0].Item!;
        if (selected is not null && !string.Equals(selected.Key, owner.Key, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Input.Text, selected.Name, StringComparison.OrdinalIgnoreCase))
        {
            SetInput(selected.Name); // complete the suggestion
            return;
        }

        if (Input.Text.Length > 0)
        {
            _chips.Add(new Chip(null, Input.Text));
            SetInput("");
        }
    }

    /// <summary>
    /// Pasted text containing tabs ("email&lt;TAB&gt;bob@x.com") becomes chips as if Tab had been
    /// pressed between segments; the first segment resolves to its best catalog match.
    /// </summary>
    private void SplitPastedSegments()
    {
        var parts = Input.Text.Split('\t');
        var last = parts[^1];
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (_chips.Count == 0)
            {
                var owner = _services.Search.SearchCatalog(part, 1).FirstOrDefault();
                if (owner is null) { last = string.Join(' ', parts[i..]); break; }
                _chips.Add(new Chip(owner, part));
            }
            else if (part.Length > 0)
            {
                _chips.Add(new Chip(null, part));
            }
        }
        SetInput(last);
    }

    private void PopChip()
    {
        var chip = _chips[^1];
        _chips.RemoveAt(_chips.Count - 1);
        SetInput(chip.TypedText);
    }

    private void SetInput(string text)
    {
        _userPickedRow = false;
        Input.Text = text;
        Input.CaretIndex = text.Length;
        UpdatePlaceholder();
        RunSearch(); // TextChanged doesn't fire when the text is unchanged (e.g. "" → "") but chips changed
    }

    private void Results_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControlFromElement(e.OriginalSource as DependencyObject) is null) return;
        LaunchSelected(ModeFor(Keyboard.Modifiers));
    }

    private object? ItemsControlFromElement(DependencyObject? element) =>
        element is null ? null : Results.ContainerFromElement(element);

    private void LaunchSelected(LaunchMode mode)
    {
        if (Results.SelectedItem is not ResultRow row) return;
        var query = BuildQuery();
        var ownerTyped = _chips.Count > 0 ? _chips[0].TypedText : null;
        try
        {
            _services.Launcher.Launch(row.Item, query, mode, ownerTyped);
            HideLauncher();
        }
        catch (Exception ex)
        {
            Logger.Log($"Launch failed for {row.Item.Target}: {ex.Message}");
            // Cancelling a UAC prompt is not an error worth showing.
            if (ex is not System.ComponentModel.Win32Exception { NativeErrorCode: 1223 })
                MessageBox.Show(this, $"Could not launch {row.Item.Name}:\n{ex.Message}", "Kite", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------------------------------------------------------- native

    /// <summary>Windows refuses SetForegroundWindow unless we just received input; the hotkey counts, but be robust.</summary>
    private void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (GetForegroundWindow() == hwnd) return;
        var foreground = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var thisThread = GetCurrentThreadId();
        if (fgThread != thisThread) AttachThreadInput(fgThread, thisThread, true);
        SetForegroundWindow(hwnd);
        if (fgThread != thisThread) AttachThreadInput(fgThread, thisThread, false);
    }

    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
}
