using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Runtime.InteropServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DeskSort
{

public partial class MainWindow : Window
{
    private readonly string _desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private readonly string _commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskSort", "settings.json");
    private readonly string _profilesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskSort", "profiles");
    private string _activeSettingsPath = string.Empty;
    private ConfigurationProfileCatalog _profileCatalog = new ConfigurationProfileCatalog();
    private string _activeProfileId = "default";
    private readonly ObservableCollection<CategoryModel> _categories = new ObservableCollection<CategoryModel>();
    private readonly ObservableCollection<CategoryModel> _shownCategories = new ObservableCollection<CategoryModel>();
    private readonly Dictionary<string, string> _assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _itemIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ItemIconTransformData> _itemIconTransforms = new Dictionary<string, ItemIconTransformData>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FenceWindow> _fenceWindows = new Dictionary<string, FenceWindow>();
    private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
    private string? _selectedCategory;
    private bool _loading;
    private IntPtr _desktopIconView;
    private bool _restoreDesktopIcons;
    private bool _workspaceReady;
    private bool _audioVisualizerEnabled;
    private SystemAudioMeter? _systemAudioMeter;
    private AudioVisualizerWindow? _audioVisualizerWindow;
    private readonly System.Windows.Threading.DispatcherTimer _audioVisualizerTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
    private double _smoothedAudioLevel;
    private double _audioVisualizerLeft = -1, _audioVisualizerTop = -1, _audioVisualizerWidth = 420, _audioVisualizerHeight = 120;
    private string _languageCode = "ja";
    private Forms.NotifyIcon? _trayIcon;
    private string _selectedMonitorDeviceName = string.Empty;
    private bool _shutdownRequested;
    private bool _restoringDesktopWindows;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowStyles.HideFromAltTab(this);
        InitializeProfiles();
        LoadSettings();
        Localization.SetLanguage(_languageCode);
        ApplyBuiltInCategoryNames();
        _audioVisualizerTimer.Tick += AudioVisualizerTimer_Tick;
        CategoryBoard.ItemsSource = _shownCategories;
        RefreshDesktop();
        foreach (var root in new[] { _desktop, _commonDesktop }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
            watcher.Created += Watcher_Changed; watcher.Deleted += Watcher_Changed; watcher.Renamed += Watcher_Renamed; watcher.Changed += Watcher_Changed;
            _watchers.Add(watcher);
        }
        Closed += (_, _) => { foreach (var watcher in _watchers) watcher.Dispose(); };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var area = GetSelectedWorkArea();
        Left = area.Left + 18; Top = area.Top + 18;
        MinWidth = Math.Min(710, Math.Max(360, area.Width - 36));
        Width = Math.Max(MinWidth, Math.Min(900, area.Width - 36));
        if (Width < 720) { SearchBox.Visibility = Visibility.Collapsed; FileCount.Visibility = Visibility.Collapsed; }
        Activate();
        _desktopIconView = FindDesktopIconView();
        if (_desktopIconView != IntPtr.Zero && IsWindowVisible(_desktopIconView))
        {
            ShowWindow(_desktopIconView, 0);
            _restoreDesktopIcons = true;
        }
        _workspaceReady = true;
        AutoStartCheck.IsChecked = IsAutoStartEnabled();
        InitializeTrayIcon();
        UpdateProfileTooltip();
        SyncAudioVisualizerButton();
        if (_audioVisualizerEnabled) StartAudioVisualizer();
        Dispatcher.BeginInvoke(new Action(() => WindowStyles.HideFromAltTab(this)), System.Windows.Threading.DispatcherPriority.Loaded);
        Subtitle.Text = Localization.Get("Subtitle");
        RefreshBoard();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (!_workspaceReady || _restoringDesktopWindows || WindowState != WindowState.Minimized) return;
        KeepDesktopWidgetsVisible();
    }

    private void KeepDesktopWidgetsVisible()
    {
        if (_restoringDesktopWindows) return;
        _restoringDesktopWindows = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                WindowStyles.SendToDesktopLayer(this);
                foreach (var fence in _fenceWindows.Values) fence.KeepVisibleOnDesktop();
                _audioVisualizerWindow?.KeepVisibleOnDesktop();
            }
            finally { _restoringDesktopWindows = false; }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void SetDesktopIconsVisible(bool visible)
    {
        if (!_restoreDesktopIcons || _desktopIconView == IntPtr.Zero) return;
        ShowWindow(_desktopIconView, visible ? 5 : 0);
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
        _audioVisualizerTimer.Stop();
        _systemAudioMeter?.Dispose();
        _systemAudioMeter = null;
        _audioVisualizerWindow?.Close();
        _audioVisualizerWindow = null;
        foreach (var fence in _fenceWindows.Values.ToList()) fence.Close();
        _fenceWindows.Clear();
        if (_restoreDesktopIcons && _desktopIconView != IntPtr.Zero) ShowWindow(_desktopIconView, 5);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownRequested) return;
        e.Cancel = true;
        Hide();
    }

    private void Close_Click(object sender, RoutedEventArgs e) { Hide(); }
    private void AudioVisualizerButton_Click(object sender, RoutedEventArgs e) { ToggleAudioVisualizer(); }

    private void InitializeTrayIcon()
    {
        if (_trayIcon != null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) => PopulateTrayMenu(menu);
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "your Aemeath",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            if (IsVisible) Hide();
            else ShowDesktopMenu();
        };
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var executable = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(executable);
                if (icon != null) return icon;
            }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void ShowDesktopMenu()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        WindowStyles.SendToDesktopLayer(this);
    }

    private void PopulateTrayMenu(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var toolbar = new Forms.ToolStripMenuItem(IsVisible ? Localization.Get("HideDesktopMenu") : Localization.Get("ShowDesktopMenu"), null, (_, _) =>
        {
            if (IsVisible) Hide(); else ShowDesktopMenu();
        }) { Checked = IsVisible, CheckOnClick = false };
        menu.Items.Add(toolbar);

        var search = new Forms.ToolStripTextBox { ToolTipText = Localization.Get("SearchDesktop"), Text = SearchBox.Text, AutoSize = false, Width = 210 };
        search.TextChanged += (_, _) => { SearchBox.Text = search.Text; };
        search.KeyDown += (_, e) => { if (e.KeyCode == Forms.Keys.Enter) { ShowDesktopMenu(); SearchBox.Focus(); e.Handled = true; } };
        menu.Items.Add(search);
        menu.Items.Add(new Forms.ToolStripMenuItem(Localization.Get("AddCategory"), null, (_, _) => AddCategory_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new Forms.ToolStripMenuItem(_audioVisualizerEnabled ? Localization.Get("AudioStop") : Localization.Get("AudioStart"), null, (_, _) => ToggleAudioVisualizer()) { Checked = _audioVisualizerEnabled, CheckOnClick = false });

        var categories = new Forms.ToolStripMenuItem(Localization.Get("Categories"));
        foreach (var category in _categories)
        {
            var categoryItem = new Forms.ToolStripMenuItem(category.Name) { Checked = category.Visible, CheckOnClick = true };
            categoryItem.Click += (_, _) => { category.Visible = categoryItem.Checked; RefreshBoard(); };
            categories.DropDownItems.Add(categoryItem);
        }
        menu.Items.Add(categories);

        var profiles = new Forms.ToolStripMenuItem(Localization.Get("Profiles"));
        foreach (var profile in _profileCatalog.Profiles)
        {
            var profileItem = new Forms.ToolStripMenuItem(profile.Name) { Checked = profile.Id == _activeProfileId, CheckOnClick = false };
            profileItem.Click += (_, _) => SwitchProfile(profile.Id);
            profiles.DropDownItems.Add(profileItem);
        }
        profiles.DropDownItems.Add(new Forms.ToolStripSeparator());
        profiles.DropDownItems.Add(new Forms.ToolStripMenuItem(Localization.Get("CreateProfile"), null, (_, _) => CreateProfile()));
        menu.Items.Add(profiles);

        var languages = new Forms.ToolStripMenuItem(Localization.Get("Language"));
        foreach (var choice in new[] { ("ja", "日本語"), ("zh-CN", "简体中文"), ("en", "English") })
        {
            var language = new Forms.ToolStripMenuItem(choice.Item2) { Checked = _languageCode == choice.Item1, CheckOnClick = false };
            language.Click += (_, _) => SetLanguage(choice.Item1);
            languages.DropDownItems.Add(language);
        }
        menu.Items.Add(languages);

        var startup = new Forms.ToolStripMenuItem(Localization.Get("AutoStart")) { Checked = IsAutoStartEnabled(), CheckOnClick = true };
        startup.Click += (_, _) => { AutoStartCheck.IsChecked = startup.Checked; AutoStart_Click(this, new RoutedEventArgs()); };
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripMenuItem(Localization.Get("Refresh"), null, (_, _) => Refresh_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new Forms.ToolStripMenuItem(Localization.Get("ShowAll"), null, (_, _) => ShowHidden_Click(this, new RoutedEventArgs())));

        var monitors = new Forms.ToolStripMenuItem(Localization.Get("DisplayMonitor"));
        var screens = Forms.Screen.AllScreens;
        monitors.Enabled = screens.Length > 1;
        if (screens.Length <= 1)
            monitors.DropDownItems.Add(new Forms.ToolStripMenuItem(Localization.Get("OnlyOneDisplay")) { Enabled = false });
        foreach (var screen in screens)
        {
            var label = screen.DeviceName + " · " + screen.Bounds.Width + "×" + screen.Bounds.Height + (screen.Primary ? " · " + Localization.Get("PrimaryDisplay") : string.Empty);
            var display = new Forms.ToolStripMenuItem(label) { Checked = string.Equals(screen.DeviceName, _selectedMonitorDeviceName, StringComparison.OrdinalIgnoreCase), CheckOnClick = false };
            display.Click += (_, _) => SelectMonitor(screen.DeviceName);
            monitors.DropDownItems.Add(display);
        }
        menu.Items.Add(monitors);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem(Localization.Get("ExitRestore"), null, (_, _) =>
        {
            _shutdownRequested = true;
            Close();
        }));
    }

    private Rect GetSelectedWorkArea()
    {
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s => string.Equals(s.DeviceName, _selectedMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? Forms.Screen.PrimaryScreen
            ?? Forms.Screen.AllScreens.FirstOrDefault();
        if (screen == null) return SystemParameters.WorkArea;
        _selectedMonitorDeviceName = screen.DeviceName;
        var bounds = screen.WorkingArea;
        return new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    internal Rect DesktopWorkArea => GetSelectedWorkArea();

    private void SelectMonitor(string deviceName)
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length <= 1 || string.Equals(deviceName, _selectedMonitorDeviceName, StringComparison.OrdinalIgnoreCase)) return;
        var previousArea = GetSelectedWorkArea();
        _selectedMonitorDeviceName = screens.Any(s => string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            ? deviceName : Forms.Screen.PrimaryScreen?.DeviceName ?? string.Empty;
        var nextArea = GetSelectedWorkArea();
        var dx = nextArea.Left - previousArea.Left;
        var dy = nextArea.Top - previousArea.Top;
        _audioVisualizerLeft = Clamp(_audioVisualizerLeft < 0 ? nextArea.Right - _audioVisualizerWidth - 24 : _audioVisualizerLeft + dx, nextArea.Left, nextArea.Right - _audioVisualizerWidth);
        _audioVisualizerTop = Clamp(_audioVisualizerTop < 0 ? nextArea.Bottom - _audioVisualizerHeight - 24 : _audioVisualizerTop + dy, nextArea.Top, nextArea.Bottom - _audioVisualizerHeight);
        foreach (var category in _categories)
        {
            category.X = Clamp(category.X < 0 ? 12 : category.X, 0, Math.Max(0, nextArea.Width - category.PanelWidth));
            category.Y = Clamp(category.Y < 0 ? 100 : category.Y, 0, Math.Max(0, nextArea.Height - Math.Max(180, category.PanelHeight)));
        }
        Left = Clamp(Left + dx, nextArea.Left, nextArea.Right - Width);
        Top = Clamp(Top + dy, nextArea.Top, nextArea.Bottom - Height);
        foreach (var category in _categories)
            if (_fenceWindows.TryGetValue(category.Id, out var fence)) fence.MoveToSelectedMonitor();
        _audioVisualizerWindow?.MoveToSelectedMonitor(_audioVisualizerLeft, _audioVisualizerTop);
        SaveSettings();
    }

    private static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(value, Math.Max(minimum, maximum)));
    private void ProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var profile in _profileCatalog.Profiles)
        {
            var item = new MenuItem { Header = profile.Name, IsCheckable = true, IsChecked = profile.Id == _activeProfileId, Tag = profile.Id };
            item.Click += (_, _) => SwitchProfile((string)item.Tag);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var create = new MenuItem { Header = Localization.Get("CreateProfile") };
        create.Click += (_, _) => CreateProfile();
        menu.Items.Add(create);
        menu.PlacementTarget = ProfilesButton;
        menu.IsOpen = true;
    }

    private void CreateProfile()
    {
        var name = AskText(Localization.Get("CreateProfile"), Localization.Get("ProfileName"));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (_profileCatalog.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        { MessageBox.Show(Localization.Get("ProfileExists"), Localization.Get("AppTitle")); return; }
        SaveSettings();
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_profilesDirectory, id + ".json");
        try
        {
            File.Copy(_activeSettingsPath, path, false);
            _profileCatalog.Profiles.Add(new ConfigurationProfile { Id = id, Name = name });
            _activeProfileId = id;
            _activeSettingsPath = path;
            _profileCatalog.CurrentProfileId = id;
            SaveProfileCatalog();
            UpdateProfileTooltip();
        }
        catch (Exception ex) { MessageBox.Show(Localization.Get("ProfileError") + ex.Message, Localization.Get("AppTitle")); }
    }

    private void SwitchProfile(string id)
    {
        if (id == _activeProfileId || !_profileCatalog.Profiles.Any(p => p.Id == id)) return;
        SaveSettings();
        _audioVisualizerTimer.Stop(); _systemAudioMeter?.Dispose(); _systemAudioMeter = null;
        _audioVisualizerWindow?.Close(); _audioVisualizerWindow = null;
        foreach (var fence in _fenceWindows.Values.ToList()) fence.Close();
        _fenceWindows.Clear();
        foreach (var category in _categories) category.StopSlideshow();
        _categories.Clear(); _shownCategories.Clear(); _assignments.Clear(); _itemIcons.Clear(); _itemIconTransforms.Clear();
        _activeProfileId = id;
        _activeSettingsPath = ProfilePath(id);
        _profileCatalog.CurrentProfileId = id;
        SaveProfileCatalog();
        LoadSettings(); Localization.SetLanguage(_languageCode); ApplyBuiltInCategoryNames();
        Subtitle.Text = Localization.Get("Subtitle");
        var selectedArea = GetSelectedWorkArea();
        Left = selectedArea.Left + 18; Top = selectedArea.Top + 18;
        RefreshDesktop(); SyncAudioVisualizerButton();
        if (_workspaceReady && _audioVisualizerEnabled) StartAudioVisualizer();
        UpdateProfileTooltip();
    }

    private void UpdateProfileTooltip() => ProfilesButton.ToolTip = Localization.Get("Profiles") + ": " + (_profileCatalog.Profiles.FirstOrDefault(p => p.Id == _activeProfileId)?.Name ?? _activeProfileId);

    private string ProfilePath(string id)
    {
        if (id != "default" && !Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("Invalid profile id");
        return Path.Combine(_profilesDirectory, id + ".json");
    }

    private void InitializeProfiles()
    {
        Directory.CreateDirectory(_profilesDirectory);
        var catalogPath = Path.Combine(_profilesDirectory, "profiles.json");
        try
        {
            if (File.Exists(catalogPath)) _profileCatalog = JsonSerializer.Deserialize<ConfigurationProfileCatalog>(File.ReadAllText(catalogPath)) ?? new ConfigurationProfileCatalog();
        }
        catch { _profileCatalog = new ConfigurationProfileCatalog(); }
        if (_profileCatalog.Profiles == null) _profileCatalog.Profiles = new List<ConfigurationProfile>();
        if (_profileCatalog.Profiles.Count == 0)
        {
            var defaultPath = ProfilePath("default");
            if (!File.Exists(defaultPath) && File.Exists(_settingsPath)) File.Copy(_settingsPath, defaultPath, false);
            _profileCatalog.Profiles.Add(new ConfigurationProfile { Id = "default", Name = Localization.Get("DefaultProfile") });
            _profileCatalog.CurrentProfileId = "default";
            SaveProfileCatalog();
        }
        var current = _profileCatalog.Profiles.FirstOrDefault(p => p.Id == _profileCatalog.CurrentProfileId)
            ?? _profileCatalog.Profiles[0];
        _activeProfileId = current.Id;
        _activeSettingsPath = ProfilePath(_activeProfileId);
        if (!File.Exists(_activeSettingsPath) && File.Exists(_settingsPath)) File.Copy(_settingsPath, _activeSettingsPath, false);
        _profileCatalog.CurrentProfileId = _activeProfileId;
        SaveProfileCatalog();
    }

    private void SaveProfileCatalog()
    {
        try { Directory.CreateDirectory(_profilesDirectory); File.WriteAllText(Path.Combine(_profilesDirectory, "profiles.json"), JsonSerializer.Serialize(_profileCatalog, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var choices = new[] { ("ja", "日本語"), ("zh-CN", "简体中文"), ("en", "English") };
        foreach (var choice in choices)
        {
            var item = new MenuItem { Header = choice.Item2, IsCheckable = true, IsChecked = _languageCode == choice.Item1 };
            item.Click += (_, _) => SetLanguage(choice.Item1);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = LanguageButton;
        menu.IsOpen = true;
    }

    private void SetLanguage(string languageCode)
    {
        _languageCode = languageCode;
        Localization.SetLanguage(languageCode);
        ApplyBuiltInCategoryNames();
        foreach (var item in _categories.SelectMany(c => c.Items)) item.RefreshLocalizedText();
        Subtitle.Text = Localization.Get("Subtitle");
        SyncAudioVisualizerButton();
        SaveSettings();
    }

    private void ApplyBuiltInCategoryNames()
    {
        var keys = new Dictionary<string, string> { ["docs"] = "Docs", ["images"] = "Images", ["sheets"] = "Sheets", ["installers"] = "Installers", ["other"] = "Other" };
        foreach (var category in _categories)
        {
            if (!keys.TryGetValue(category.Id, out var key)) continue;
            var isBuiltInName = Enumerable.Range(0, 3).Any(i => category.Name == Localization.Get(key, new[] { "ja", "zh-CN", "en" }[i]));
            if (isBuiltInName) category.Name = Localization.Get(key);
        }
    }

    private void ToggleAudioVisualizer()
    {
        if (_audioVisualizerEnabled)
        {
            _audioVisualizerEnabled = false;
            _audioVisualizerTimer.Stop();
            _systemAudioMeter?.Dispose();
            _systemAudioMeter = null;
            _audioVisualizerWindow?.Close();
            _audioVisualizerWindow = null;
        }
        else
        {
            try
            {
                _systemAudioMeter = new SystemAudioMeter();
                _audioVisualizerEnabled = true;
                _smoothedAudioLevel = 0;
                CreateAudioVisualizerWindow();
                _audioVisualizerTimer.Start();
            }
            catch (Exception ex)
            {
                _systemAudioMeter?.Dispose();
                _systemAudioMeter = null;
                _audioVisualizerWindow?.Close();
                _audioVisualizerWindow = null;
                _audioVisualizerEnabled = false;
                MessageBox.Show(Localization.Get("AudioError") + ex.Message, Localization.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        SyncAudioVisualizerButton();
        SaveSettings();
    }

    private void AudioVisualizerTimer_Tick(object? sender, EventArgs e)
    {
        var peak = _systemAudioMeter?.ReadPeak() ?? 0;
        _smoothedAudioLevel += (peak - _smoothedAudioLevel) * (peak > _smoothedAudioLevel ? 0.72 : 0.22);
        _audioVisualizerWindow?.UpdateAudioBars(_smoothedAudioLevel);
    }

    private void StartAudioVisualizer()
    {
        try
        {
            _systemAudioMeter = new SystemAudioMeter();
            CreateAudioVisualizerWindow();
            _audioVisualizerTimer.Start();
        }
        catch
        {
            _audioVisualizerEnabled = false;
            SyncAudioVisualizerButton();
            SaveSettings();
        }
    }

    private void CreateAudioVisualizerWindow()
    {
        _audioVisualizerWindow?.Close();
        _audioVisualizerWindow = new AudioVisualizerWindow(this, _audioVisualizerLeft, _audioVisualizerTop, _audioVisualizerWidth, _audioVisualizerHeight);
        _audioVisualizerWindow.Show();
    }

    internal void DisableAudioVisualizer()
    {
        if (_audioVisualizerEnabled) ToggleAudioVisualizer();
    }

    internal void SaveAudioVisualizerBounds(double left, double top, double width, double height)
    {
        _audioVisualizerLeft = left; _audioVisualizerTop = top;
        _audioVisualizerWidth = width; _audioVisualizerHeight = height;
        SaveSettings();
    }

    private void SyncAudioVisualizerButton()
    {
        AudioVisualizerButton.ToolTip = Localization.Get(_audioVisualizerEnabled ? "AudioStop" : "AudioStart");
        AudioVisualizerButton.Background = _audioVisualizerEnabled
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x38, 0xEB, 0x13, 0xC0))
            : Brushes.Transparent;
    }

    private const string StartupValueName = "DeskSort";
    private bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        return key?.GetValue(StartupValueName) is string;
    }
    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
                ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")
                ?? throw new InvalidOperationException("自動起動設定を開けません。");
            if (AutoStartCheck.IsChecked == true)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exe)) throw new InvalidOperationException("実行ファイルの場所を取得できません。");
                key.SetValue(StartupValueName, "\"" + exe + "\"");
            }
            else key.DeleteValue(StartupValueName, false);
        }
        catch (Exception ex) { AutoStartCheck.IsChecked = IsAutoStartEnabled(); MessageBox.Show(Localization.Get("StartupError") + ex.Message, Localization.Get("AppTitle")); }
    }

    private static IntPtr FindDesktopIconView()
    {
        var shell = FindWindow("Progman", null);
        var view = FindWindowEx(shell, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero) return view;
        IntPtr found = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            var candidate = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (candidate == IntPtr.Zero) return true;
            found = candidate;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);

    private void LoadSettings()
    {
        var firstRun = !File.Exists(_activeSettingsPath);
        try
        {
            if (File.Exists(_activeSettingsPath))
            {
                var state = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(_activeSettingsPath));
                if (state != null)
                {
                    _audioVisualizerEnabled = state.AudioVisualizerEnabled;
                    _selectedMonitorDeviceName = state.SelectedMonitorDeviceName ?? string.Empty;
                    _languageCode = string.IsNullOrWhiteSpace(state.LanguageCode) ? "ja" : state.LanguageCode;
                    _audioVisualizerLeft = state.AudioVisualizerLeft;
                    _audioVisualizerTop = state.AudioVisualizerTop;
                    _audioVisualizerWidth = state.AudioVisualizerWidth > 0 ? state.AudioVisualizerWidth : 420;
                    _audioVisualizerHeight = state.AudioVisualizerHeight > 0 ? state.AudioVisualizerHeight : 120;
                    foreach (var c in state.Categories ?? new List<CategoryData>())
                    {
                        var name = c.Name switch { "文档" => "書類", "图片" => "画像", "表格" => "表計算", "应用与安装包" => "アプリ・インストーラー", "其他" => "その他", _ => c.Name };
                        _categories.Add(new CategoryModel(c.Id, name, c.Color, c.Visible, c.Icon, c.IconImagePath, c.BackgroundColor, c.IsExpanded, c.X, c.Y, c.BackgroundImagePath, c.BackgroundImageScale, c.BackgroundImageFit, c.BackgroundImagePositionX, c.BackgroundImagePositionY, c.PanelWidth, c.PanelHeight, c.SlideshowFolderPath));
                    }
                    foreach (var kv in state.Assignments ?? new Dictionary<string, string>()) _assignments[kv.Key] = kv.Value;
                    foreach (var kv in state.ItemIcons ?? new Dictionary<string, string>()) _itemIcons[kv.Key] = kv.Value;
                    foreach (var kv in state.ItemIconTransforms ?? new Dictionary<string, ItemIconTransformData>()) _itemIconTransforms[kv.Key] = kv.Value;
                }
            }
        }
        catch { }
        if (_categories.Count == 0)
        {
            if (firstRun)
            {
                _categories.Add(new CategoryModel("game", "ゲーム", "#8E7CA7", true, "🎮", backgroundImagePath: InstallBundledDefaultBackground("game-aemeath.jpg")));
                _categories.Add(new CategoryModel("tools", "ツール", "#788A9E", true, "🛠", backgroundImagePath: InstallBundledDefaultBackground("tools-aemeath.png")));
                _categories.Add(new CategoryModel("sns", "SNS", "#B47F73", true, "✦", backgroundImagePath: InstallBundledDefaultBackground("sns-aemeath.png")));
                _categories.Add(new CategoryModel("aemeath", "エイメス", "#748C70", true, "🌸", backgroundImagePath: InstallBundledDefaultBackground("aemeath-garden.png")));
            }
            else
            {
                _categories.Add(new CategoryModel("docs", "書類", "#788A9E", true, "📄"));
                _categories.Add(new CategoryModel("images", "画像", "#B47F73", true, "🖼"));
                _categories.Add(new CategoryModel("sheets", "表計算", "#748C70", true, "▦"));
                _categories.Add(new CategoryModel("installers", "アプリ・インストーラー", "#A78C5C", true, "📦"));
                _categories.Add(new CategoryModel("other", "その他", "#8E7CA7", true, "✦"));
            }
        }
    }

    private static string? InstallBundledDefaultBackground(string fileName)
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "assets", "DefaultBackgrounds", fileName);
        if (!File.Exists(bundledPath)) return null;
        var destinationFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskSort", "default-backgrounds");
        var destination = Path.Combine(destinationFolder, fileName);
        try
        {
            Directory.CreateDirectory(destinationFolder);
            if (!File.Exists(destination)) File.Copy(bundledPath, destination, false);
            return destination;
        }
        catch { return bundledPath; }
    }

    internal void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_activeSettingsPath)!);
            var state = new SettingsData { LanguageCode = _languageCode, SelectedMonitorDeviceName = _selectedMonitorDeviceName, AudioVisualizerEnabled = _audioVisualizerEnabled, AudioVisualizerLeft = _audioVisualizerLeft, AudioVisualizerTop = _audioVisualizerTop, AudioVisualizerWidth = _audioVisualizerWidth, AudioVisualizerHeight = _audioVisualizerHeight, Categories = _categories.Select(c => new CategoryData(c.Id, c.Name, c.Color, c.Visible, c.Icon, c.IconImagePath, c.BackgroundColor, c.IsExpanded, c.X, c.Y, c.BackgroundImagePath, c.BackgroundImageScale, c.BackgroundImageFit, c.BackgroundImagePositionX, c.BackgroundImagePositionY, c.PanelWidth, double.IsNaN(c.PanelHeight) ? 0 : c.PanelHeight, c.SlideshowFolderPath)).ToList(), Assignments = new Dictionary<string, string>(_assignments, StringComparer.OrdinalIgnoreCase), ItemIcons = new Dictionary<string, string>(_itemIcons, StringComparer.OrdinalIgnoreCase), ItemIconTransforms = _itemIconTransforms.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase) };
            File.WriteAllText(_activeSettingsPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Subtitle.Text = Localization.Get("SaveSettingsError") + ex.Message; }
    }

    private void RefreshDesktop()
    {
        if (!Directory.Exists(_desktop) && !Directory.Exists(_commonDesktop)) { Subtitle.Text = Localization.Get("DesktopNotFound"); return; }
        _loading = true;
        foreach (var c in _categories) c.Items.Clear();
        try
        {
            var desktopEntries = new[] { _desktop, _commonDesktop }.Where(Directory.Exists).SelectMany(root => Directory.EnumerateFileSystemEntries(root)).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var path in desktopEntries.OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
            {
                var name = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(name)) continue;
                var category = FindCategory(path) ?? AutoCategory(path);
                _assignments[path] = category.Id;
                var isDir = Directory.Exists(path);
                long size = 0;
                try { if (!isDir) size = new FileInfo(path).Length; } catch { }
                var item = new DesktopItem(path, name, isDir, size);
                if (_itemIcons.TryGetValue(path, out var iconPath)) item.IconImagePath = iconPath;
                if (_itemIconTransforms.TryGetValue(path, out var iconTransform)) item.SetIconTransform(iconTransform.Scale, iconTransform.PositionX, iconTransform.PositionY);
                category.Items.Add(item);
            }
        }
        catch (Exception ex) { Subtitle.Text = Localization.Get("DesktopLoadError") + ex.Message; }
        _loading = false;
        RefreshBoard();
        var itemCount = _categories.Sum(c => c.Items.Count);
        FileCount.Text = itemCount.ToString();
        StorageText.Text = itemCount == 0 ? Localization.Get("EmptyDesktop") : itemCount + Localization.Get("LoadedItems");
    }

    private CategoryModel AutoCategory(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var isReleasePreset = _categories.Any(c => c.Id == "game" || c.Id == "tools" || c.Id == "sns" || c.Id == "aemeath");
        var target = isReleasePreset ? ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".heic" => "aemeath",
            ".xls" or ".xlsx" or ".csv" or ".tsv" or ".doc" or ".docx" or ".pdf" or ".txt" or ".md" or ".rtf" or ".ppt" or ".pptx" => "tools",
            ".exe" => "game",
            ".url" => "sns",
            ".msi" or ".zip" or ".7z" or ".rar" or ".appx" => "tools",
            _ => "tools"
        } : ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".heic" => "images",
            ".xls" or ".xlsx" or ".csv" or ".tsv" => "sheets",
            ".doc" or ".docx" or ".pdf" or ".txt" or ".md" or ".rtf" or ".ppt" or ".pptx" => "docs",
            ".exe" or ".msi" or ".zip" or ".7z" or ".rar" or ".appx" => "installers",
            _ => "other"
        };
        return _categories.FirstOrDefault(c => c.Id == target) ?? _categories.FirstOrDefault(c => c.Id == "other") ?? _categories.Last();
    }

    private CategoryModel? FindCategory(string path)
    {
        if (_assignments.TryGetValue(path, out var id)) return _categories.FirstOrDefault(c => c.Id == id);
        return null;
    }

    private void RefreshBoard()
    {
        var workArea = GetSelectedWorkArea();
        var areaWidth = workArea.Width - 56;
        var columns = Math.Max(1, (int)Math.Floor((areaWidth + 20) / 440));
        for (var i = 0; i < _categories.Count; i++)
        {
            if (_categories[i].X < 0 || _categories[i].Y < 0)
            {
                _categories[i].X = 12 + (i % columns) * 440;
                _categories[i].Y = 100 + (i / columns) * 265;
            }
            else if (_categories[i].Y < 92) _categories[i].Y = 100;
            _categories[i].X = Clamp(_categories[i].X, 0, Math.Max(0, workArea.Width - _categories[i].PanelWidth));
            _categories[i].Y = Clamp(_categories[i].Y, 0, Math.Max(0, workArea.Height - Math.Max(180, _categories[i].PanelHeight)));
        }
        _shownCategories.Clear();
        foreach (var c in _categories.Where(c => c.Visible && (_selectedCategory == null || c.Id == _selectedCategory))) _shownCategories.Add(c);
        if (_workspaceReady)
        {
            var visible = new HashSet<string>(_shownCategories.Select(c => c.Id));
            foreach (var category in _shownCategories)
            {
                if (!_fenceWindows.ContainsKey(category.Id))
                {
                    var fence = new FenceWindow(this, category);
                    _fenceWindows[category.Id] = fence;
                    fence.Show();
                }
            }
            foreach (var id in _fenceWindows.Keys.Where(id => !visible.Contains(id)).ToList())
            {
                _fenceWindows[id].Close();
                _fenceWindows.Remove(id);
            }
        }
        if (!_loading) SaveSettings();
    }

    private void CategoryMove_Start(object sender, MouseButtonEventArgs e)
    {
        var category = (sender as FrameworkElement)?.DataContext as CategoryModel;
        if (category == null || e.LeftButton != MouseButtonState.Pressed || !_fenceWindows.TryGetValue(category.Id, out var fence)) return;
        try
        {
            fence.DragMove();
            var work = GetSelectedWorkArea();
            fence.Left = Math.Max(work.Left, Math.Min(fence.Left, work.Right - fence.ActualWidth));
            fence.Top = Math.Max(work.Top, Math.Min(fence.Top, work.Bottom - fence.ActualHeight));
            category.X = Math.Max(0, fence.Left - work.Left);
            category.Y = Math.Max(0, fence.Top - work.Top);
            SaveSettings();
        }
        catch { }
        e.Handled = true;
    }

    private void CategoryToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string id)
        {
            var category = _categories.FirstOrDefault(c => c.Id == id);
            if (category != null) { category.IsExpanded = !category.IsExpanded; if (_fenceWindows.TryGetValue(id, out var fence)) fence.RefreshSize(); SaveSettings(); }
        }
    }

    private void CategoryHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border header && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            header.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 24, 24);
    }

    private void CategoryResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is Thumb thumb && thumb.Tag is string id && _fenceWindows.TryGetValue(id, out var fence))
            fence.ResizeBy(e.HorizontalChange, e.VerticalChange);
    }

    private void CategoryPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border panel && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            panel.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 18, 18);
    }

    private void File_RightClick(object sender, MouseButtonEventArgs e)
    {
        var card = sender as FrameworkElement;
        var item = card == null ? null : card.DataContext as DesktopItem;
        if (card == null || item == null) return;
        var menu = new ContextMenu();
        var setIcon = new MenuItem { Header = Localization.Get("SelectItemIcon") };
        setIcon.Click += (_, _) => SelectItemIcon(item);
        menu.Items.Add(setIcon);
        var adjust = new MenuItem { Header = Localization.Get("AdjustItemIcon"), IsEnabled = !string.IsNullOrWhiteSpace(item.IconImagePath) };
        adjust.Click += (_, _) => ShowItemIconSettings(item);
        menu.Items.Add(adjust);
        var reset = new MenuItem { Header = Localization.Get("ResetItemIcon"), IsEnabled = !string.IsNullOrWhiteSpace(item.IconImagePath) };
        reset.Click += (_, _) => { _itemIcons.Remove(item.FullPath); _itemIconTransforms.Remove(item.FullPath); item.IconImagePath = null; item.SetIconTransform(1, 0.5, 0.5); SaveSettings(); };
        menu.Items.Add(reset);
        menu.PlacementTarget = card;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void SelectItemIcon(DesktopItem item)
    {
        var picker = new OpenFileDialog { Title = Localization.Get("SelectItemIconTitle"), Filter = Localization.Get("ImageFiles") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|" + Localization.Get("AllFiles") + "|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var iconFolder = Path.Combine(Path.GetDirectoryName(_settingsPath)!, "icons", "items");
            Directory.CreateDirectory(iconFolder);
            var key = Convert.ToBase64String(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(item.FullPath.ToUpperInvariant()))).Replace("+", "").Replace("/", "").TrimEnd('=');
            var destination = Path.Combine(iconFolder, key + Path.GetExtension(picker.FileName).ToLowerInvariant());
            File.Copy(picker.FileName, destination, true);
            _itemIcons[item.FullPath] = destination;
            item.IconImagePath = destination;
            SaveSettings();
        }
        catch (Exception ex) { MessageBox.Show(Localization.Get("ItemIconLoadError") + ex.Message, Localization.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ShowItemIconSettings(DesktopItem item)
    {
        if (item.IconImage == null) return;
        var oldScale = item.IconScale; var oldX = item.IconPositionX; var oldY = item.IconPositionY;
        var dialog = new Window { Title = Localization.Get("ItemIconDialogTitle"), Width = 350, Height = 435, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = (Brush)FindResource("CardBrush") };
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = item.Name + " · " + Localization.Get("CategoryIconTitle"), FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 12) });
        var preview = new Border { Width = 100, Height = 100, CornerRadius = new CornerRadius(14), Background = item.TileBrush, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
        var previewImage = new Image { Source = item.IconImage, Stretch = Stretch.UniformToFill, RenderTransformOrigin = new Point(0.5, 0.5) };
        var transform = new TransformGroup(); var scaleTransform = new ScaleTransform(item.IconScale, item.IconScale); var translateTransform = new TranslateTransform(item.IconOffsetX, item.IconOffsetY); transform.Children.Add(scaleTransform); transform.Children.Add(translateTransform); previewImage.RenderTransform = transform; preview.Child = previewImage; root.Children.Add(preview);
        var scaleLine = new DockPanel { Margin = new Thickness(0, 16, 0, 3) }; scaleLine.Children.Add(new TextBlock { Text = Localization.Get("Scale") }); var scaleText = new TextBlock { Text = item.IconScale.ToString("0.00") + "×" }; DockPanel.SetDock(scaleText, Dock.Right); scaleLine.Children.Add(scaleText); root.Children.Add(scaleLine);
        var scale = new Slider { Minimum = 0.5, Maximum = 3.0, Value = item.IconScale, TickFrequency = 0.05 }; root.Children.Add(scale);
        root.Children.Add(new TextBlock { Text = Localization.Get("HorizontalPosition"), Margin = new Thickness(0, 10, 0, 3) }); var horizontal = new Slider { Minimum = 0, Maximum = 1, Value = item.IconPositionX, TickFrequency = 0.01 }; root.Children.Add(horizontal);
        root.Children.Add(new TextBlock { Text = Localization.Get("VerticalPosition"), Margin = new Thickness(0, 8, 0, 3) }); var vertical = new Slider { Minimum = 0, Maximum = 1, Value = item.IconPositionY, TickFrequency = 0.01 }; root.Children.Add(vertical);
        void UpdatePreview() { scaleText.Text = item.IconScale.ToString("0.00") + "×"; scaleTransform.ScaleX = scaleTransform.ScaleY = item.IconScale; translateTransform.X = item.IconOffsetX * 3.2; translateTransform.Y = item.IconOffsetY * 3.2; }
        scale.ValueChanged += (_, _) => { item.IconScale = scale.Value; UpdatePreview(); };
        horizontal.ValueChanged += (_, _) => { item.IconPositionX = horizontal.Value; UpdatePreview(); };
        vertical.ValueChanged += (_, _) => { item.IconPositionY = vertical.Value; UpdatePreview(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = Localization.Get("Cancel"), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        var save = new Button { Content = Localization.Get("Apply"), Padding = new Thickness(16, 7, 16, 7), Background = (Brush)FindResource("AccentBrush"), Foreground = Brushes.White }; save.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(save); root.Children.Add(buttons); dialog.Content = root;
        if (dialog.ShowDialog() == true) { _itemIconTransforms[item.FullPath] = new ItemIconTransformData(item.IconScale, item.IconPositionX, item.IconPositionY); SaveSettings(); }
        else item.SetIconTransform(oldScale, oldX, oldY);
    }

    private async void Watcher_Changed(object sender, FileSystemEventArgs e)
    {
        await Task.Delay(250);
        await Dispatcher.InvokeAsync(RefreshDesktop);
    }
    private async void Watcher_Renamed(object sender, RenamedEventArgs e) { _assignments.Remove(e.OldFullPath); await Task.Delay(250); await Dispatcher.InvokeAsync(RefreshDesktop); }
    private void Refresh_Click(object sender, RoutedEventArgs e) { RefreshDesktop(); Subtitle.Text = Localization.Get("RefreshDone"); }
    private void All_Click(object sender, RoutedEventArgs e) { _selectedCategory = null; RefreshBoard(); }
    private void ShowHidden_Click(object sender, RoutedEventArgs e) { foreach (var c in _categories) c.Visible = true; RefreshBoard(); }
    private void SidebarCategory_Click(object sender, RoutedEventArgs e) { if (sender is Button b && b.Tag is string id) { _selectedCategory = id; RefreshBoard(); } }
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CategoryBoard == null) return;
        var q = SearchBox.Text.Trim();
        foreach (var c in _categories)
            foreach (var item in c.Items) item.IsSearchMatch = q.Length == 0 || item.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        foreach (var c in _categories) c.RaiseItemsChanged();
    }

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var name = AskText(Localization.Get("SelectCategoryName"), Localization.Get("CategoryName"));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (_categories.Any(c => c.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))) { MessageBox.Show(Localization.Get("CategoryExists"), Localization.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var colors = new[] { "#748C70", "#788A9E", "#B47F73", "#A78C5C", "#8E7CA7" };
        var color = colors[_categories.Count % colors.Length];
        _categories.Add(new CategoryModel(Guid.NewGuid().ToString("N"), name, color, true, "✦"));
        RefreshBoard();
    }

    private void CategoryName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is CategoryModel category)
        {
            var value = box.Text.Trim();
            if (string.IsNullOrWhiteSpace(value)) { box.Text = category.Name; return; }
            if (_categories.Any(c => c != category && c.Name.Equals(value, StringComparison.CurrentCultureIgnoreCase))) { box.Text = category.Name; MessageBox.Show(Localization.Get("CategoryNameDuplicate"), Localization.Get("AppTitle")); return; }
            category.Name = value;
            SaveSettings();
        }
    }

    private void CategoryPicture_Click(object sender, MouseButtonEventArgs e)
    {
        var category = (sender as FrameworkElement)?.DataContext as CategoryModel;
        if (category == null) return;
        SelectCategoryImage(category);
        e.Handled = true;
    }

    private void SelectCategoryImage(CategoryModel category)
    {
        var picker = new OpenFileDialog { Title = Localization.Get("SelectCategoryIconTitle"), Filter = Localization.Get("ImageFiles") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|" + Localization.Get("AllFiles") + "|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var iconFolder = Path.Combine(Path.GetDirectoryName(_settingsPath)!, "icons");
            Directory.CreateDirectory(iconFolder);
            var ext = Path.GetExtension(picker.FileName).ToLowerInvariant();
            var destination = Path.Combine(iconFolder, category.Id + ext);
            File.Copy(picker.FileName, destination, true);
            category.IconImagePath = destination; SaveSettings();
        }
        catch (Exception ex) { MessageBox.Show(Localization.Get("CategoryImageLoadError") + ex.Message, Localization.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void SelectCategoryBackground(CategoryModel category)
    {
        var picker = new OpenFileDialog { Title = Localization.Get("SelectCategoryBackgroundTitle"), Filter = Localization.Get("ImageFiles") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|" + Localization.Get("AllFiles") + "|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(_settingsPath)!, "backgrounds");
            Directory.CreateDirectory(folder);
            // 上書き時に古い画像キャッシュが使われないよう、毎回異なる保存先を使用します。
            var destination = Path.Combine(folder, category.Id + "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(picker.FileName).ToLowerInvariant());
            File.Copy(picker.FileName, destination, true);
            category.SlideshowFolderPath = null;
            category.BackgroundImagePath = destination;
            SaveSettings();
            ShowBackgroundImageSettings(category);
        }
        catch (Exception ex) { MessageBox.Show(Localization.Get("BackgroundLoadError") + ex.Message, Localization.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void SelectSlideshowFolder(CategoryModel category)
    {
        var owner = new WindowHandleOwner(new WindowInteropHelper(this).Handle);
        using var picker = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Localization.Get("SlideshowDescription"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (picker.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(picker.SelectedPath)) return;
        var images = CategoryModel.GetSlideshowImages(picker.SelectedPath);
        if (images.Count == 0)
        {
            MessageBox.Show(Localization.Get("NoImages"), Localization.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        category.SlideshowFolderPath = picker.SelectedPath;
        category.StartSlideshow();
        SaveSettings();
    }

    private sealed class WindowHandleOwner : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; }
        public WindowHandleOwner(IntPtr handle) { Handle = handle; }
    }

    private void ShowBackgroundImageSettings(CategoryModel category)
    {
        if (string.IsNullOrWhiteSpace(category.BackgroundImagePath)) return;
        var oldScale = category.BackgroundImageScale;
        var oldFit = category.BackgroundImageFit;
        var oldPositionX = category.BackgroundImagePositionX;
        var oldPositionY = category.BackgroundImagePositionY;
        category.BackgroundImageScale = Math.Max(1, category.BackgroundImageScale);
        var dialog = new Window { Title = Localization.Get("BackgroundDialogTitle"), Width = 390, Height = 420, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = (Brush)FindResource("CardBrush") };
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = category.Name + " · " + Localization.Get("BackgroundImageSuffix"), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(new TextBlock { Text = Localization.Get("DisplayMode"), FontSize = 10, Margin = new Thickness(0, 0, 0, 5) });
        var fit = new ComboBox { Height = 30 };
        fit.Items.Add(new ComboBoxItem { Content = Localization.Get("FitWhole"), Tag = "Fit" });
        fit.Items.Add(new ComboBoxItem { Content = Localization.Get("CropToFrame"), Tag = "Cover" });
        fit.Items.Add(new ComboBoxItem { Content = Localization.Get("StretchToFrame"), Tag = "Fill" });
        fit.SelectedIndex = category.BackgroundImageFit == "Fit" ? 0 : category.BackgroundImageFit == "Fill" ? 2 : 1;
        root.Children.Add(fit);
        var scaleLine = new DockPanel { Margin = new Thickness(0, 14, 0, 5) };
        scaleLine.Children.Add(new TextBlock { Text = Localization.Get("Scale"), FontSize = 10 });
        var scaleText = new TextBlock { Text = category.BackgroundImageScale.ToString("0.00") + "×", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(scaleText, Dock.Right); scaleLine.Children.Add(scaleText); root.Children.Add(scaleLine);
        var slider = new Slider { Minimum = 1.0, Maximum = 5.0, Value = Math.Max(1.0, Math.Min(5.0, category.BackgroundImageScale)), TickFrequency = 0.05, IsSnapToTickEnabled = false };
        root.Children.Add(slider);
        root.Children.Add(new TextBlock { Text = Localization.Get("PreviewHint"), FontSize = 10, Margin = new Thickness(0, 10, 0, 5) });
        var preview = new Border { Width = 320, Height = 145, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("LineBrush"), Background = category.PanelBackgroundBrush, HorizontalAlignment = HorizontalAlignment.Center, Focusable = true, Cursor = Cursors.SizeAll };
        root.Children.Add(preview);
        Point? dragStart = null; double dragX = 0, dragY = 0;
        preview.MouseLeftButtonDown += (_, e) => { dragStart = e.GetPosition(preview); dragX = category.BackgroundImagePositionX; dragY = category.BackgroundImagePositionY; preview.CaptureMouse(); e.Handled = true; };
        preview.MouseMove += (_, e) =>
        {
            if (dragStart == null || !preview.IsMouseCaptured) return;
            var current = e.GetPosition(preview); var span = Math.Max(0.001, 1 - 1 / category.BackgroundImageScale);
            category.BackgroundImagePositionX = dragX - (current.X - dragStart.Value.X) / Math.Max(1, preview.ActualWidth) / span;
            category.BackgroundImagePositionY = dragY - (current.Y - dragStart.Value.Y) / Math.Max(1, preview.ActualHeight) / span;
            preview.Background = category.PanelBackgroundBrush;
        };
        preview.MouseLeftButtonUp += (_, _) => { dragStart = null; preview.ReleaseMouseCapture(); };
        preview.MouseWheel += (_, e) =>
        {
            var point = e.GetPosition(preview); var u = Math.Max(0, Math.Min(1, point.X / Math.Max(1, preview.ActualWidth))); var v = Math.Max(0, Math.Min(1, point.Y / Math.Max(1, preview.ActualHeight)));
            var wheelScale = category.BackgroundImageScale; var oldSpan = Math.Max(0, 1 - 1 / wheelScale); var oldView = 1 / wheelScale;
            var imageX = category.BackgroundImagePositionX * oldSpan + u * oldView; var imageY = category.BackgroundImagePositionY * oldSpan + v * oldView;
            category.BackgroundImageScale = Math.Max(1, Math.Min(5, wheelScale * (e.Delta > 0 ? 1.1 : 0.9)));
            var newSpan = Math.Max(0.001, 1 - 1 / category.BackgroundImageScale); var newView = 1 / category.BackgroundImageScale;
            category.BackgroundImagePositionX = (imageX - u * newView) / newSpan; category.BackgroundImagePositionY = (imageY - v * newView) / newSpan;
            slider.Value = category.BackgroundImageScale; scaleText.Text = category.BackgroundImageScale.ToString("0.00") + "×"; preview.Background = category.PanelBackgroundBrush; e.Handled = true;
        };
        fit.SelectionChanged += (_, _) =>
        {
            if (fit.SelectedItem is ComboBoxItem selected && selected.Tag is string tag)
            { category.BackgroundImageFit = tag; preview.Background = category.PanelBackgroundBrush; }
        };
        slider.ValueChanged += (_, _) =>
        {
            category.BackgroundImageScale = slider.Value; scaleText.Text = slider.Value.ToString("0.00") + "×"; preview.Background = category.PanelBackgroundBrush;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = Localization.Get("Cancel"), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 7, 0) };
        cancel.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        var save = new Button { Content = Localization.Get("Apply"), Background = (Brush)FindResource("AccentBrush"), Foreground = Brushes.White, Padding = new Thickness(16, 7, 16, 7) };
        save.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(save); root.Children.Add(buttons);
        dialog.Content = root;
        if (dialog.ShowDialog() == true) SaveSettings();
        else { category.BackgroundImageScale = oldScale; category.BackgroundImageFit = oldFit; category.BackgroundImagePositionX = oldPositionX; category.BackgroundImagePositionY = oldPositionY; }
    }

    private void CategoryMenu_Click(object sender, RoutedEventArgs e)
    {
        var b = sender as Button; var id = b == null ? null : b.Tag as string;
        if (b == null || id == null) return;
        var category = _categories.FirstOrDefault(c => c.Id == id); if (category == null) return;
        var menu = new ContextMenu();
        var toggle = new MenuItem { Header = Localization.Get(category.Visible ? "HideCategory" : "ShowCategory") };
        toggle.Click += (_, _) => { category.Visible = !category.Visible; if (_selectedCategory == id && !category.Visible) _selectedCategory = null; RefreshBoard(); };
        menu.Items.Add(toggle);
        var imageIcon = new MenuItem { Header = Localization.Get("SelectImage") };
        imageIcon.Click += (_, _) => SelectCategoryImage(category);
        menu.Items.Add(imageIcon);
        var backgroundImage = new MenuItem { Header = Localization.Get("SelectBackground") };
        backgroundImage.Click += (_, _) => SelectCategoryBackground(category);
        menu.Items.Add(backgroundImage);
        var slideshowFolder = new MenuItem { Header = Localization.Get("SelectSlideshowFolder") };
        slideshowFolder.Click += (_, _) => SelectSlideshowFolder(category);
        menu.Items.Add(slideshowFolder);
        var clearSlideshow = new MenuItem { Header = Localization.Get("ClearSlideshow"), IsEnabled = !string.IsNullOrWhiteSpace(category.SlideshowFolderPath) };
        clearSlideshow.Click += (_, _) => { category.SlideshowFolderPath = null; SaveSettings(); };
        menu.Items.Add(clearSlideshow);
        var adjustBackground = new MenuItem { Header = Localization.Get("AdjustBackground"), IsEnabled = string.IsNullOrWhiteSpace(category.SlideshowFolderPath) && !string.IsNullOrWhiteSpace(category.BackgroundImagePath) };
        adjustBackground.Click += (_, _) => ShowBackgroundImageSettings(category);
        menu.Items.Add(adjustBackground);
        var resetBackground = new MenuItem { Header = Localization.Get("ResetBackground"), IsEnabled = !string.IsNullOrWhiteSpace(category.BackgroundImagePath) || !string.IsNullOrWhiteSpace(category.SlideshowFolderPath) };
        resetBackground.Click += (_, _) => { category.SlideshowFolderPath = null; category.BackgroundImagePath = null; category.BackgroundImageScale = 1.0; category.BackgroundImageFit = "Cover"; category.BackgroundImagePositionX = 0.5; category.BackgroundImagePositionY = 0.5; SaveSettings(); };
        menu.Items.Add(resetBackground);
        var remove = new MenuItem { Header = Localization.Get("DeleteCategory") };
        remove.Click += (_, _) =>
        {
            if (_categories.Count <= 1) return;
            var fallback = _categories.First(c => c != category);
            if (MessageBox.Show(Localization.Get("DeleteConfirm", category.Name, category.Items.Count, fallback.Name), Localization.Get("DeleteConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            foreach (var item in category.Items) _assignments[item.FullPath] = fallback.Id;
            _categories.Remove(category); if (_selectedCategory == id) _selectedCategory = null; RefreshDesktop();
        };
        menu.Items.Add(remove);
        menu.PlacementTarget = b; menu.IsOpen = true;
    }

    private void Category_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; }
    private void Category_Drop(object sender, DragEventArgs e)
    {
        var panel = sender as FrameworkElement; var id = panel == null ? null : panel.Tag as string;
        if (panel == null || id == null || !_categories.Any(c => c.Id == id) || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var path in paths) if (_categories.SelectMany(c => c.Items).Any(i => i.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))) _assignments[path] = id;
        RefreshDesktop();
    }
    private void File_MouseMove(object sender, MouseEventArgs e)
    {
        var card = sender as FrameworkElement; var path = card == null ? null : card.Tag as string;
        if (e.LeftButton != MouseButtonState.Pressed || card == null || path == null) return;
        DragDrop.DoDragDrop(card, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Move);
    }
    private void File_DragOver(object sender, DragEventArgs e) => Category_DragOver(sender, e);
    private void File_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement card && card.Tag is string destination && _assignments.TryGetValue(destination, out var categoryId))
        {
            var category = _categories.FirstOrDefault(c => c.Id == categoryId);
            var paths = e.Data.GetDataPresent(DataFormats.FileDrop) ? (string[])e.Data.GetData(DataFormats.FileDrop) : new string[0];
            foreach (var path in paths) if (_categories.SelectMany(c => c.Items).Any(i => i.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))) _assignments[path] = categoryId;
            if (category != null) RefreshDesktop();
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string path && (File.Exists(path) || Directory.Exists(path)))
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, Localization.Get("FileOpenError")); }
    }

    private string? AskText(string title, string label)
    {
        var dialog = new Window { Title = title, Width = 330, Height = 170, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = (Brush)FindResource("CardBrush") };
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 7) });
        var input = new TextBox { Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
        root.Children.Add(input);
        var save = new Button { Content = Localization.Get("Decision"), Background = (Brush)FindResource("AccentBrush"), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(17, 7, 17, 7) };
        save.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        root.Children.Add(save); dialog.Content = root; input.Focus();
        return dialog.ShowDialog() == true ? input.Text : null;
    }
}

public sealed class CategoryModel : INotifyPropertyChanged
{
    private string _name; private string _color; private bool _visible; private string _icon; private bool _isExpanded; private string _backgroundColor; private double _x; private double _y; private double _panelWidth; private string? _iconImagePath; private BitmapImage? _iconImage; private string? _backgroundImagePath; private BitmapImage? _backgroundImage; private double _backgroundImageScale; private string _backgroundImageFit; private double _backgroundImagePositionX; private double _backgroundImagePositionY;
    private string? _slideshowFolderPath;
    private List<string> _slideshowImages = new List<string>();
    private int _slideshowIndex = -1;
    private bool _slideshowRunning;
    private readonly System.Windows.Threading.DispatcherTimer _slideshowTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
    public string Id { get; }
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string Color { get => _color; set { _color = value; OnPropertyChanged(); OnPropertyChanged(nameof(DotBrush)); } }
    public bool Visible { get => _visible; set { _visible = value; OnPropertyChanged(); } }
    public string Icon { get => _icon; set { _icon = value; OnPropertyChanged(); } }
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(CategoryPanelHeight)); OnPropertyChanged(nameof(ShowEmptyHint)); } }
    public double ItemsMaxHeight => Math.Max(180, SystemParameters.WorkArea.Height - 170);
    public double X { get => _x; set { _x = value; OnPropertyChanged(); } }
    public double Y { get => _y; set { _y = value; OnPropertyChanged(); } }
    public double PanelWidth { get => _panelWidth; set { _panelWidth = Math.Max(300, Math.Min(1000, value)); OnPropertyChanged(); } }
    private double _panelHeight = double.NaN;
    public double PanelHeight { get => _panelHeight; set { _panelHeight = double.IsNaN(value) ? double.NaN : Math.Max(180, Math.Min(1600, value)); OnPropertyChanged(); OnPropertyChanged(nameof(CategoryPanelHeight)); } }
    public double CategoryPanelHeight => IsExpanded ? PanelHeight : double.NaN;
    public bool ShowEmptyHint => IsExpanded && string.IsNullOrWhiteSpace(BackgroundImagePath);
    public string BackgroundColor { get => _backgroundColor; set { _backgroundColor = value; OnPropertyChanged(); OnPropertyChanged(nameof(PanelBrush)); } }
    public string? IconImagePath
    {
        get => _iconImagePath;
        set { _iconImagePath = value; _iconImage = LoadImage(value); OnPropertyChanged(); OnPropertyChanged(nameof(IconImage)); }
    }
    public ImageSource? IconImage => _iconImage;
    public string? BackgroundImagePath
    {
        get => _backgroundImagePath;
        set { _backgroundImagePath = value; _backgroundImage = LoadImage(value, 0); OnPropertyChanged(); OnPropertyChanged(nameof(PanelBackgroundBrush)); OnPropertyChanged(nameof(ShowEmptyHint)); }
    }
    public string? SlideshowFolderPath
    {
        get => _slideshowFolderPath;
        set
        {
            _slideshowFolderPath = string.IsNullOrWhiteSpace(value) ? null : value;
            OnPropertyChanged();
            if (_slideshowRunning) StartSlideshow();
        }
    }
    public void StartSlideshow()
    {
        _slideshowRunning = true;
        _slideshowTimer.Stop();
        _slideshowImages = GetSlideshowImages(_slideshowFolderPath);
        if (_slideshowImages.Count == 0) return;
        _slideshowIndex = _slideshowImages.FindIndex(p => string.Equals(p, BackgroundImagePath, StringComparison.OrdinalIgnoreCase));
        if (_slideshowIndex < 0) _slideshowIndex = 0;
        ApplySlideshowImage(_slideshowImages[_slideshowIndex]);
        if (_slideshowImages.Count > 1)
        {
            _slideshowTimer.Tick -= SlideshowTimer_Tick;
            _slideshowTimer.Tick += SlideshowTimer_Tick;
            _slideshowTimer.Start();
        }
    }
    public void StopSlideshow()
    {
        _slideshowRunning = false;
        _slideshowTimer.Stop();
    }
    private void SlideshowTimer_Tick(object? sender, EventArgs e)
    {
        _slideshowImages = GetSlideshowImages(_slideshowFolderPath);
        if (_slideshowImages.Count == 0) { _slideshowTimer.Stop(); return; }
        _slideshowIndex = (_slideshowIndex + 1) % _slideshowImages.Count;
        ApplySlideshowImage(_slideshowImages[_slideshowIndex]);
    }
    private void ApplySlideshowImage(string path)
    {
        BackgroundImageFit = "Fit";
        BackgroundImageScale = 1;
        BackgroundImagePositionX = 0.5;
        BackgroundImagePositionY = 0.5;
        BackgroundImagePath = path;
    }
    public static List<string> GetSlideshowImages(string? folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new List<string>();
            var supported = new HashSet<string>(new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" }, StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(path => supported.Contains(Path.GetExtension(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }
    public double BackgroundImageScale { get => _backgroundImageScale; set { _backgroundImageScale = Math.Max(1.0, Math.Min(5.0, value)); OnPropertyChanged(); OnPropertyChanged(nameof(PanelBackgroundBrush)); } }
    public string BackgroundImageFit { get => _backgroundImageFit; set { _backgroundImageFit = value; OnPropertyChanged(); OnPropertyChanged(nameof(PanelBackgroundBrush)); } }
    public double BackgroundImagePositionX { get => _backgroundImagePositionX; set { _backgroundImagePositionX = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); OnPropertyChanged(nameof(PanelBackgroundBrush)); } }
    public double BackgroundImagePositionY { get => _backgroundImagePositionY; set { _backgroundImagePositionY = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); OnPropertyChanged(nameof(PanelBackgroundBrush)); } }
    public Brush PanelBackgroundBrush
    {
        get
        {
            if (_backgroundImage == null) return PanelBrush;
            var stretch = BackgroundImageFit == "Fit" ? Stretch.Uniform : BackgroundImageFit == "Fill" ? Stretch.Fill : Stretch.UniformToFill;
            var brush = new ImageBrush(_backgroundImage) { Stretch = stretch, Opacity = 1.0, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox };
            var viewSize = 1.0 / BackgroundImageScale;
            brush.Viewbox = new Rect((1 - viewSize) * BackgroundImagePositionX, (1 - viewSize) * BackgroundImagePositionY, viewSize, viewSize);
            return brush;
        }
    }
    public Brush PanelBrush
    {
        get
        {
            try { var color = (System.Windows.Media.Color)ColorConverter.ConvertFromString(BackgroundColor); return new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xD8, color.R, color.G, color.B)); }
            catch { return new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xD8, 236, 235, 228)); }
        }
    }
    public Brush DotBrush { get { try { return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(Color)); } catch { return Brushes.Gray; } } }
    public ObservableCollection<DesktopItem> Items { get; } = new ObservableCollection<DesktopItem>();
    public CategoryModel(string id, string name, string color, bool visible = true, string icon = "✦", string? iconImagePath = null, string backgroundColor = "#ECEBE4", bool isExpanded = true, double x = -1, double y = -1, string? backgroundImagePath = null, double backgroundImageScale = 1.0, string backgroundImageFit = "Cover", double backgroundImagePositionX = 0.5, double backgroundImagePositionY = 0.5, double panelWidth = 420, double panelHeight = double.NaN, string? slideshowFolderPath = null)
    {
        Id = id; _name = name; _color = color; _visible = visible; _icon = icon; _backgroundColor = backgroundColor; _isExpanded = isExpanded; _x = x; _y = y; _panelWidth = Math.Max(300, Math.Min(1000, panelWidth)); _backgroundImageScale = 1.0; _backgroundImageFit = backgroundImageFit; _backgroundImagePositionX = 0.5; _backgroundImagePositionY = 0.5; IconImagePath = iconImagePath; BackgroundImagePath = backgroundImagePath; BackgroundImageScale = backgroundImageScale; BackgroundImagePositionX = backgroundImagePositionX; BackgroundImagePositionY = backgroundImagePositionY; PanelHeight = panelHeight; _slideshowFolderPath = slideshowFolderPath;
        _slideshowTimer.Tick += SlideshowTimer_Tick;
    }
    private static BitmapImage? LoadImage(string? path, int decodePixelWidth = 96)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path, UriKind.Absolute); if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth; image.EndInit(); image.Freeze(); return image;
        }
        catch { return null; }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RaiseItemsChanged() => OnPropertyChanged(nameof(Items));
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class AudioBar : INotifyPropertyChanged
{
    private double _height = 2;
    public double Height { get => _height; set { _height = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Height))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AudioVisualizerWindow : Window
{
    private readonly MainWindow _controller;
    private readonly List<Border> _bars = new List<Border>();
    private readonly System.Windows.Threading.DispatcherTimer _saveBoundsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _readyToSave;
    private TextBlock? _titleText;
    private Button? _closeButton;
    private Thumb? _resizeGrip;

    public AudioVisualizerWindow(MainWindow controller, double left, double top, double width, double height)
    {
        _controller = controller;
        var area = controller.DesktopWorkArea;
        Width = Math.Max(300, Math.Min(area.Width - 24, width > 0 ? width : 420));
        Height = Math.Max(100, Math.Min(area.Height - 24, height > 0 ? height : 120));
        Left = left >= 0 ? left : area.Right - Width - 24;
        Top = top >= 0 ? top : area.Bottom - Height - 24;
        Left = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
        Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
        MinWidth = 300; MinHeight = 100;
        MaxWidth = Math.Max(MinWidth, area.Width - 24); MaxHeight = Math.Max(MinHeight, area.Height - 24);
        Title = Localization.Get("AudioTitle");
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = false; ShowActivated = false; ShowInTaskbar = false;
        SourceInitialized += (_, _) => WindowStyles.HideFromAltTab(this);
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => WindowStyles.HideFromAltTab(this)), System.Windows.Threading.DispatcherPriority.Loaded);
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Dispatcher.BeginInvoke(new Action(KeepVisibleOnDesktop), System.Windows.Threading.DispatcherPriority.ApplicationIdle); };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Margin = new Thickness(2, 0, 2, 5) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = "♫  " + Localization.Get("AudioTitle"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(72, 56, 70)), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.SizeAll, ToolTip = Localization.Get("AudioMove") };
        _titleText = title;
        title.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch { } SaveBounds(); e.Handled = true; } };
        header.Children.Add(title);
        var close = new Button { Content = "×", Width = 26, Height = 25, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(92, 77, 88)), FontSize = 16, Cursor = Cursors.Hand, ToolTip = Localization.Get("AudioClose") };
        _closeButton = close;
        close.Click += (_, _) => _controller.DisableAudioVisualizer();
        Grid.SetColumn(close, 1); header.Children.Add(close);
        layout.Children.Add(header);

        var stage = new Grid { Margin = new Thickness(2, 0, 2, 2), ClipToBounds = true };
        var barsPanel = new UniformGrid { Rows = 1, VerticalAlignment = VerticalAlignment.Stretch };
        for (var i = 0; i < 40; i++)
        {
            var bar = new Border { Height = 2, MinHeight = 2, Margin = new Thickness(2, 2, 2, 1), VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0xE8, 0xEB, 0x13, 0xC0)) };
            _bars.Add(bar); barsPanel.Children.Add(bar);
        }
        stage.Children.Add(barsPanel);
        var resizeGrip = new Thumb { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE, Background = Brushes.Transparent, ToolTip = Localization.Get("AudioResize") };
        _resizeGrip = resizeGrip;
        resizeGrip.DragDelta += (_, e) => { Width = Math.Max(MinWidth, Math.Min(MaxWidth, Width + e.HorizontalChange)); Height = Math.Max(MinHeight, Math.Min(MaxHeight, Height + e.VerticalChange)); ScheduleBoundsSave(); };
        stage.Children.Add(resizeGrip);
        Grid.SetRow(stage, 1); layout.Children.Add(stage);

        var panel = new Border { Background = new SolidColorBrush(Color.FromArgb(0xE8, 0xF8, 0xF4, 0xF7)), BorderBrush = new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(22), Padding = new Thickness(12, 9, 12, 8), Child = layout, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.16 } };
        Content = panel;
        _saveBoundsTimer.Tick += (_, _) => { _saveBoundsTimer.Stop(); SaveBounds(); };
        SizeChanged += (_, _) => ScheduleBoundsSave();
        LocationChanged += (_, _) => ScheduleBoundsSave();
        Loaded += (_, _) => _readyToSave = true;
        Closed += (_, _) => { _saveBoundsTimer.Stop(); SaveBounds(); Localization.LanguageChanged -= OnLanguageChanged; };
        Localization.LanguageChanged += OnLanguageChanged;
    }

    public void UpdateAudioBars(double level)
    {
        level = Math.Max(0, Math.Min(1, level));
        var time = Environment.TickCount / 180.0;
        for (var i = 0; i < _bars.Count; i++)
        {
            var wave = 0.16 + 0.84 * Math.Abs(Math.Sin(i * 0.63 + time * 1.8) * Math.Cos(i * 0.21 - time * 0.7));
            _bars[i].Height = 2 + 42 * Math.Pow(level, 0.72) * wave;
        }
    }

    internal void MoveToSelectedMonitor(double left, double top)
    {
        var area = _controller.DesktopWorkArea;
        Left = Math.Max(area.Left, Math.Min(left, area.Right - Width));
        Top = Math.Max(area.Top, Math.Min(top, area.Bottom - Height));
    }

    internal void KeepVisibleOnDesktop()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        WindowStyles.SendToDesktopLayer(this);
    }

    private void ScheduleBoundsSave()
    {
        if (!_readyToSave) return;
        _saveBoundsTimer.Stop(); _saveBoundsTimer.Start();
    }

    private void SaveBounds()
    {
        if (_readyToSave && WindowState == WindowState.Normal)
            _controller.SaveAudioVisualizerBounds(Left, Top, Width, Height);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Title = Localization.Get("AudioTitle");
        if (_titleText != null) _titleText.Text = "♫  " + Localization.Get("AudioTitle");
        if (_titleText != null) _titleText.ToolTip = Localization.Get("AudioMove");
        if (_closeButton != null) _closeButton.ToolTip = Localization.Get("AudioClose");
        if (_resizeGrip != null) _resizeGrip.ToolTip = Localization.Get("AudioResize");
    }
}

public sealed class DesktopItem : INotifyPropertyChanged
{
    public string FullPath { get; }
    public string Name { get; }
    public long Size { get; }
    public bool IsDirectory { get; }
    public string Glyph { get; }
    public Brush GlyphBrush { get; }
    public Brush TileBrush { get; }
    public ImageSource? ShellIcon { get; }
    private string? _iconImagePath;
    private BitmapImage? _iconImage;
    private double _iconScale = 1.0, _iconPositionX = 0.5, _iconPositionY = 0.5;
    public string? IconImagePath { get => _iconImagePath; set { _iconImagePath = value; _iconImage = LoadImage(value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconImagePath))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconImage))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayIcon))); } }
    public ImageSource? IconImage => _iconImage;
    public ImageSource? DisplayIcon => _iconImage ?? ShellIcon;
    public double IconScale { get => _iconScale; set { _iconScale = Math.Max(0.5, Math.Min(3, value)); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconScale))); } }
    public double IconPositionX { get => _iconPositionX; set { _iconPositionX = Math.Max(0, Math.Min(1, value)); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPositionX))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconOffsetX))); } }
    public double IconPositionY { get => _iconPositionY; set { _iconPositionY = Math.Max(0, Math.Min(1, value)); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPositionY))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconOffsetY))); } }
    public double IconOffsetX => (IconPositionX - 0.5) * 30;
    public double IconOffsetY => (IconPositionY - 0.5) * 30;
    public void SetIconTransform(double scale, double x, double y) { IconScale = scale; IconPositionX = x; IconPositionY = y; }
    private bool _isSearchMatch = true;
    public bool IsSearchMatch { get { return _isSearchMatch; } set { _isSearchMatch = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSearchMatch))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLocalizedText() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
    public string Detail => IsDirectory ? Localization.Get("Folder") : Size < 1024 ? $"{Size} B" : Size < 1024 * 1024 ? $"{Size / 1024d:0.#} KB" : $"{Size / 1024d / 1024:0.#} MB";
    public DesktopItem(string path, string name, bool isDirectory, long size)
    {
        FullPath = path; Name = name; IsDirectory = isDirectory; Size = size;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!isDirectory && (ext == ".lnk" || ext == ".exe" || ext == ".url")) ShellIcon = LoadShellIcon(path);
        (Glyph, TileBrush, GlyphBrush) = isDirectory ? ("▰", BrushFrom("#EEF0E7"), BrushFrom("#8D987B")) : ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => ("▧", BrushFrom("#F4E9E5"), BrushFrom("#B47F73")),
            ".xlsx" or ".xls" or ".csv" => ("▦", BrushFrom("#EAF0E8"), BrushFrom("#748C70")),
            ".pdf" => ("PDF", BrushFrom("#F4E9E5"), BrushFrom("#B47F73")),
            ".exe" or ".msi" => ("◇", BrushFrom("#EEEAF4"), BrushFrom("#8E7CA7")),
            _ => ("▤", BrushFrom("#E9EEF1"), BrushFrom("#788A9E"))
        };
    }
    private static BitmapImage? LoadImage(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path, UriKind.Absolute); image.DecodePixelWidth = 96; image.EndInit(); image.Freeze(); return image;
        }
        catch { return null; }
    }
    private static ImageSource? LoadShellIcon(string path)
    {
        try
        {
            var info = new ShellFileInfo();
            var handle = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(ShellFileInfo)), 0x00000100);
            if (handle == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            try { var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(48, 48)); source.Freeze(); return source; }
            finally { DestroyIcon(info.Icon); }
        }
        catch { return null; }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ShellFileInfo
    {
        public IntPtr Icon; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo info, uint infoSize, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    private static Brush BrushFrom(string s) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));
}

internal sealed class SettingsData
{
    public string LanguageCode { get; set; } = "ja";
    public string? SelectedMonitorDeviceName { get; set; }
    public bool AudioVisualizerEnabled { get; set; }
    public double AudioVisualizerLeft { get; set; } = -1;
    public double AudioVisualizerTop { get; set; } = -1;
    public double AudioVisualizerWidth { get; set; } = 420;
    public double AudioVisualizerHeight { get; set; } = 120;
    public string? Theme { get; set; }
    public string? ThemePanel { get; set; }
    public List<CategoryData>? Categories { get; set; }
    public Dictionary<string, string>? Assignments { get; set; }
    public Dictionary<string, string>? ItemIcons { get; set; }
    public Dictionary<string, ItemIconTransformData>? ItemIconTransforms { get; set; }
}
internal sealed class ConfigurationProfileCatalog
{
    public string CurrentProfileId { get; set; } = "default";
    public List<ConfigurationProfile> Profiles { get; set; } = new List<ConfigurationProfile>();
}
internal sealed class ConfigurationProfile
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "Default";
}

internal static class WindowStyles
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;
    internal static void HideFromAltTab(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var style = GetWindowLong(hwnd, GwlExStyle);
            SetWindowLong(hwnd, GwlExStyle, (style | WsExToolWindow) & ~WsExAppWindow);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch { }
    }
    internal static void SendToDesktopLayer(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
            SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040 | 0x0200);
        }
        catch { }
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
}
internal sealed class ItemIconTransformData
{
    public double Scale { get; set; } = 1.0; public double PositionX { get; set; } = 0.5; public double PositionY { get; set; } = 0.5;
    public ItemIconTransformData() { }
    public ItemIconTransformData(double scale, double x, double y) { Scale = scale; PositionX = x; PositionY = y; }
}
internal sealed class CategoryData
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Color { get; set; }
    public bool Visible { get; set; }
    public string Icon { get; set; } = "✦";
    public string? IconImagePath { get; set; }
    public string BackgroundColor { get; set; } = "#ECEBE4";
    public string? BackgroundImagePath { get; set; }
    public string? SlideshowFolderPath { get; set; }
    public double BackgroundImageScale { get; set; } = 1.0;
    public string BackgroundImageFit { get; set; } = "Cover";
    public double BackgroundImagePositionX { get; set; } = 0.5;
    public double BackgroundImagePositionY { get; set; } = 0.5;
    public bool IsExpanded { get; set; } = true;
    public double X { get; set; } = -1;
    public double Y { get; set; } = -1;
    public double PanelWidth { get; set; } = 420;
    public double PanelHeight { get; set; }
    public CategoryData() { Id = ""; Name = ""; Color = "#748C70"; }
    public CategoryData(string id, string name, string color, bool visible, string icon, string? iconImagePath, string backgroundColor, bool isExpanded, double x, double y, string? backgroundImagePath, double backgroundImageScale, string backgroundImageFit, double backgroundImagePositionX, double backgroundImagePositionY, double panelWidth, double panelHeight, string? slideshowFolderPath)
    {
        Id = id; Name = name; Color = color; Visible = visible; Icon = icon; IconImagePath = iconImagePath; BackgroundColor = backgroundColor; IsExpanded = isExpanded; X = x; Y = y; BackgroundImagePath = backgroundImagePath; SlideshowFolderPath = slideshowFolderPath; BackgroundImageScale = backgroundImageScale; BackgroundImageFit = backgroundImageFit; BackgroundImagePositionX = backgroundImagePositionX; BackgroundImagePositionY = backgroundImagePositionY; PanelWidth = panelWidth; PanelHeight = panelHeight;
    }
}

public sealed class FenceWindow : Window
{
    private readonly MainWindow _controller;
    private bool _loadedForResize;
    private readonly System.Windows.Threading.DispatcherTimer _resizeSaveTimer;
    public CategoryModel Model { get; }

    public FenceWindow(MainWindow controller, CategoryModel model)
    {
        _controller = controller; Model = model;
        _resizeSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _resizeSaveTimer.Tick += (_, _) => { _resizeSaveTimer.Stop(); _controller.SaveSettings(); };
        var area = controller.DesktopWorkArea;
        Title = model.Name;
        Width = model.PanelWidth;
        SizeToContent = model.IsExpanded && !double.IsNaN(model.PanelHeight) ? SizeToContent.Manual : SizeToContent.Height;
        if (SizeToContent == SizeToContent.Manual) Height = model.PanelHeight + 17;
        MaxHeight = Math.Max(260, area.Height - 24);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 300;
        MaxWidth = Math.Max(320, Math.Min(1000, area.Width));
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = false;
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + Math.Max(0, model.X);
        Top = area.Top + Math.Max(0, model.Y);
        var presenter = new ContentPresenter { Content = model, ContentTemplate = (DataTemplate)controller.FindResource("CategoryTemplate") };
        var roundedRoot = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(18), Child = presenter };
        void ClipRoundedWindow()
        {
            var width = roundedRoot.ActualWidth; var height = roundedRoot.ActualHeight;
            if (width > 0 && height > 0) roundedRoot.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 18, 18);
        }
        roundedRoot.SizeChanged += (_, _) => ClipRoundedWindow();
        Content = roundedRoot;
        DataContext = model;
        SourceInitialized += (_, _) => { ApplyNativeRoundedRegion(); WindowStyles.HideFromAltTab(this); };
        SizeChanged += (_, _) =>
        {
            ApplyNativeRoundedRegion();
            if (_loadedForResize && Math.Abs(Model.PanelWidth - ActualWidth) > 1)
            {
                Model.PanelWidth = ActualWidth;
            }
            if (_loadedForResize && Model.IsExpanded && SizeToContent == SizeToContent.Manual)
                Model.PanelHeight = Math.Max(180, ActualHeight - 17);
            if (_loadedForResize) { _resizeSaveTimer.Stop(); _resizeSaveTimer.Start(); }
        };
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => { ApplyNativeRoundedRegion(); WindowStyles.HideFromAltTab(this); }), System.Windows.Threading.DispatcherPriority.Loaded);
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Dispatcher.BeginInvoke(new Action(KeepVisibleOnDesktop), System.Windows.Threading.DispatcherPriority.ApplicationIdle); };
        Closed += (_, _) => { _resizeSaveTimer.Stop(); Model.StopSlideshow(); _controller.SaveSettings(); };
        Loaded += (_, _) =>
        {
            Model.StartSlideshow();
            if (Model.IsExpanded && double.IsNaN(Model.PanelHeight))
            {
                Model.PanelHeight = Math.Max(180, ActualHeight - 17);
                SizeToContent = SizeToContent.Manual;
                Height = Model.PanelHeight + 17;
            }
            _loadedForResize = true;
            if (Top + ActualHeight > area.Bottom) Top = Math.Max(area.Top, area.Bottom - ActualHeight);
        };
        model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(CategoryModel.Name)) Title = model.Name; };
    }

    internal void MoveToSelectedMonitor()
    {
        var area = _controller.DesktopWorkArea;
        Left = area.Left + Math.Max(0, Model.X);
        Top = area.Top + Math.Max(0, Model.Y);
    }

    internal void KeepVisibleOnDesktop()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        WindowStyles.SendToDesktopLayer(this);
    }

    private void ApplyNativeRoundedRegion()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var source = PresentationSource.FromVisual(this) as HwndSource;
            var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var width = (int)Math.Ceiling(ActualWidth * scale);
            var height = (int)Math.Ceiling(ActualHeight * scale);
            if (hwnd == IntPtr.Zero || width < 2 || height < 2) return;
            var diameter = Math.Max(2, (int)Math.Round(36 * scale));
            var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
            if (region == IntPtr.Zero) return;
            if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
        }
        catch { }
    }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    public void ResizeBy(double horizontalChange, double verticalChange)
    {
        if (!Model.IsExpanded) return;
        SizeToContent = SizeToContent.Manual;
        Width = Math.Max(MinWidth, Math.Min(MaxWidth, Width + horizontalChange));
        Height = Math.Max(MinHeight, Math.Min(MaxHeight, Height + verticalChange));
        Model.PanelWidth = Width;
        Model.PanelHeight = Math.Max(180, Height - 17);
        ApplyNativeRoundedRegion();
        _resizeSaveTimer.Stop(); _resizeSaveTimer.Start();
    }

    public void RefreshSize()
    {
        if (!Model.IsExpanded) SizeToContent = SizeToContent.Height;
        else if (!double.IsNaN(Model.PanelHeight)) { SizeToContent = SizeToContent.Manual; Height = Model.PanelHeight + 17; }
        else
        {
            SizeToContent = SizeToContent.Height;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Model.IsExpanded || SizeToContent != SizeToContent.Height) return;
                Model.PanelHeight = Math.Max(180, ActualHeight - 17);
                SizeToContent = SizeToContent.Manual;
                Height = Model.PanelHeight + 17;
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        InvalidateMeasure(); UpdateLayout();
    }
}
}
