using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Fable2Launcher;

public partial class MainWindow : Window
{
    public const string LauncherVersion = "2.0.0";
    // Project home: source code, releases and updates.
    public const string ProjectUrl = "https://github.com/deadtools/-Fable-2-Recomp";
    private const string UpdatesUrl = ProjectUrl + "/releases";
    private const string AuthorUrl = "https://github.com/deadtools";

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private string? _gameDirectory;
    private string? _executableDirectory;
    private bool _loading = true;
    private bool _diagnosticRunning;
    private bool _languageInitialized;
    private bool _languageUiLoaded;
    private bool _localizing;
    private bool _localizationDisabled;
    private bool _gameReady;
    private bool _forceClose;
    private string _savedSignature = "";
    private HeroDogTextureMode _loadedTextureMode = HeroDogTextureMode.DiagnosticOff;
    private Action? _afterUnsavedDecision;

    // fable2_config.toml values (advanced settings).
    private Fable2ConfigValues _advanced = new();

    // Keybind rows.
    private sealed class KeybindRow
    {
        public Border Root = null!;
        public Border KeyField = null!;
        public TextBlock KeyText = null!;
        public ComboBox ButtonCombo = null!;
        public string Key = "";
    }
    private readonly List<KeybindRow> _keyRows = new();
    private KeybindRow? _activeKeyRow;
    private bool _syncingMouseCombos;

    private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(0xBD, 0xB0, 0x9C));
    private static readonly SolidColorBrush DangerBrush = new(Color.FromRgb(0xE0, 0x71, 0x6A));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromRgb(0xE3, 0xA8, 0x5E));
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(0x8F, 0xD1, 0x9A));
    private static readonly SolidColorBrush LightBrush = new(Color.FromRgb(0xF2, 0xE8, 0xDA));
    private static readonly SolidColorBrush GoldTextBrush = new(Color.FromRgb(0xE9, 0xC9, 0x8C));
    private static readonly SolidColorBrush RowBackground = new(Color.FromArgb(0xCC, 0x12, 0x0D, 0x0C));
    private static readonly SolidColorBrush RowBorder = new(Color.FromRgb(0x5A, 0x41, 0x28));
    private static readonly SolidColorBrush RowListening = new(Color.FromRgb(0x4A, 0x32, 0x20));
    private static readonly SolidColorBrush RowListeningBorder = new(Color.FromRgb(0xF0, 0xD4, 0x9A));

    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closing += WindowClosing;
        BuildStaticItems();
        SetDefaults();
        LauncherLanguage.SetCurrent(LauncherLanguage.Load(LauncherLanguage.StateFilePath));
        SelectByTag(LanguageCombo, LauncherLanguage.Current);
        DetectGameDirectory();
        _loading = false;
        _languageInitialized = true;
        RefreshSummary();
        // Localize only after the visual tree exists.
        Loaded += (_, _) =>
        {
            _languageUiLoaded = true;
            ApplyLocalizedTexts();
        };
    }

    private static string L(string original) => LauncherLanguage.T(original);

    // ---------------------------------------------------------------- window

    private void FitToScreen()
    {
        // Keep 16:9 and fit inside the work area on small screens.
        Rect area = SystemParameters.WorkArea;
        double width = Math.Min(1280, area.Width * 0.92);
        double height = width * 9.0 / 16.0;
        if (height > area.Height * 0.92)
        {
            height = area.Height * 0.92;
            width = height * 16.0 / 9.0;
        }
        Width = Math.Round(width);
        Height = Math.Round(height);
    }

    private void StageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Drag the borderless window from any empty area.
        if (e.ButtonState != MouseButtonState.Pressed) return;
        for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null;
             node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or ComboBox or Slider or TextBoxBase or ScrollBar or Thumb or Popup)
                return;
            if (ReferenceEquals(node, Stage)) break;
        }
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void MinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose) return;
        if (_diagnosticRunning && MessageBox.Show(this,
            L("Игра продолжит работать, но диагностический отчёт останется неполным. Закрыть лаунчер?"),
            L("Диагностический сеанс"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        if (IsDirty)
        {
            e.Cancel = true;
            AskAboutUnsaved(() => { _forceClose = true; Close(); });
        }
    }

    // ---------------------------------------------------------- localization

    private void ApplyLocalizedTexts()
    {
        if (!_languageInitialized || !_languageUiLoaded || _localizing || _localizationDisabled) return;
        _localizing = true;
        try
        {
            LauncherLocalization.Apply(this);
            UpdateGameSpeedLabel();
        }
        catch (Exception error)
        {
            // UI translation must never make the launcher unusable.
            _localizationDisabled = true;
            LauncherStartupDiagnostics.Report("EN/RU interface localization", error);
        }
        finally
        {
            _localizing = false;
        }
    }

    private void LanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_languageInitialized) return;
        string language = SelectedTag(LanguageCombo) ?? LauncherLanguage.English;
        LauncherLanguage.SetCurrent(language);
        ApplyLocalizedTexts();
        try { LauncherLanguage.Save(LauncherLanguage.StateFilePath, language); }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                (language == LauncherLanguage.Russian
                    ? "Не удалось сохранить язык интерфейса: "
                    : "Could not save interface language: ") + exception.Message,
                language == LauncherLanguage.Russian ? "Настройки языка" : "Language settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------ navigation

    private void OpenSettingsClicked(object sender, RoutedEventArgs e) => ShowSettings(null);

    private void ShowSettings(string? section)
    {
        HomePage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        AchievementsButton.Visibility = Visibility.Collapsed;
        if (section is not null) SelectNav(section);
        ApplyLocalizedTexts();
    }

    private void ShowHome()
    {
        CancelKeyCapture();
        SettingsPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Visible;
        AchievementsButton.Visibility = Visibility.Visible;
        UpdateGameStatus();
        ApplyLocalizedTexts();
    }

    private void BackClicked(object sender, RoutedEventArgs e)
    {
        if (IsDirty) AskAboutUnsaved(ShowHome);
        else ShowHome();
    }

    private void SelectNav(string section)
    {
        foreach (RadioButton nav in new[] { NavGraphics, NavControls, NavGame, NavOptimization, NavFiles })
            if ((string)nav.Tag == section) nav.IsChecked = true;
    }

    private void NavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton nav || SectionGraphics is null) return;
        CancelKeyCapture();
        string section = (string)nav.Tag;
        SectionGraphics.Visibility = section == "graphics" ? Visibility.Visible : Visibility.Collapsed;
        SectionControls.Visibility = section == "controls" ? Visibility.Visible : Visibility.Collapsed;
        SectionGame.Visibility = section == "game" ? Visibility.Visible : Visibility.Collapsed;
        SectionOptimization.Visibility = section == "optimization" ? Visibility.Visible : Visibility.Collapsed;
        SectionFiles.Visibility = section == "files" ? Visibility.Visible : Visibility.Collapsed;
        (SectionTitle.Text, SectionSubtitle.Text) = section switch
        {
            "controls" => ("Управление", "Переназначение клавиш, мышь и экранные подсказки геймпадов"),
            "game" => ("Игра", "Скорость игры, профили качества и игровые патчи"),
            "optimization" => ("Оптимизация", "Совместимость, чтение текстур и параметры исполнения"),
            "files" => ("Файлы игры", "Расположение, извлечение ISO и локальная диагностика"),
            _ => ("Графика", "Разрешение экрана, качество изображения и плавность"),
        };
        if (section == "files") UpdateGameStatus();
        if (section == "controls") RefreshPromptResourceText();
        ApplyLocalizedTexts();
    }

    private void ControlTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton tab || ControlsKeyboard is null) return;
        CancelKeyCapture();
        string name = (string)tab.Tag;
        ControlsKeyboard.Visibility = name == "keyboard" ? Visibility.Visible : Visibility.Collapsed;
        ControlsMouse.Visibility = name == "mouse" ? Visibility.Visible : Visibility.Collapsed;
        ControlsPlayStation.Visibility = name == "playstation" ? Visibility.Visible : Visibility.Collapsed;
        ControlsXbox.Visibility = name == "xbox" ? Visibility.Visible : Visibility.Collapsed;
        if (name == "mouse") RefreshMouseButtonCombos();
        if (name == "playstation") RefreshPromptResourceText();
    }

    private void GameTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton tab || GameSpeedTab is null) return;
        bool speed = (string)tab.Tag == "speed";
        GameSpeedTab.Visibility = speed ? Visibility.Visible : Visibility.Collapsed;
        GamePatchesTab.Visibility = speed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OptTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton tab || OptCompatTab is null) return;
        string name = (string)tab.Tag;
        OptCompatTab.Visibility = name == "compat" ? Visibility.Visible : Visibility.Collapsed;
        OptPerfTab.Visibility = name == "perf" ? Visibility.Visible : Visibility.Collapsed;
        OptExperimentalTab.Visibility = name == "experimental" ? Visibility.Visible : Visibility.Collapsed;
    }

    // --------------------------------------------------------------- dialogs

    private void WhatsNewClicked(object sender, RoutedEventArgs e)
    {
        WhatsNewOverlay.Visibility = Visibility.Visible;
        ApplyLocalizedTexts();
    }

    private void CloseWhatsNewClicked(object sender, RoutedEventArgs e) =>
        WhatsNewOverlay.Visibility = Visibility.Collapsed;

    private void OverlayBackgroundClicked(object sender, MouseButtonEventArgs e)
    {
        WhatsNewOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void CheckUpdatesClicked(object sender, RoutedEventArgs e) => OpenUrl(UpdatesUrl);

    private void DeadtoolsLinkClicked(object sender, RoutedEventArgs e) => OpenUrl(AuthorUrl);

    private void ProjectLinkClicked(object sender, RoutedEventArgs e) => OpenUrl(ProjectUrl);

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, L("Не удалось открыть GitHub: ") + ex.Message,
                L("Ссылка на автора доработок"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AskAboutUnsaved(Action afterDecision)
    {
        _afterUnsavedDecision = afterDecision;
        UnsavedOverlay.Visibility = Visibility.Visible;
        ApplyLocalizedTexts();
    }

    private void UnsavedDiscardClicked(object sender, RoutedEventArgs e)
    {
        UnsavedOverlay.Visibility = Visibility.Collapsed;
        ReloadAllFromDisk();
        RunAfterUnsaved();
    }

    private void UnsavedCancelClicked(object sender, RoutedEventArgs e)
    {
        UnsavedOverlay.Visibility = Visibility.Collapsed;
        _afterUnsavedDecision = null;
    }

    private void UnsavedSaveClicked(object sender, RoutedEventArgs e)
    {
        UnsavedOverlay.Visibility = Visibility.Collapsed;
        if (SaveAll(interactive: true)) RunAfterUnsaved();
        else _afterUnsavedDecision = null;
    }

    private void RunAfterUnsaved()
    {
        Action? next = _afterUnsavedDecision;
        _afterUnsavedDecision = null;
        next?.Invoke();
    }

    // -------------------------------------------------------- initial values

    private void BuildStaticItems()
    {
        foreach (var option in Constants.GraphicsOptions.FrameLimits)
            FrameLimitCombo.Items.Add(new ComboBoxItem { Tag = option.Value, Content = option.Label });

        TextureModeCombo.Items.Add(new ComboBoxItem { Tag = HeroDogTextureMode.GpuRealtime,
            Content = "Морфинг на GPU в реальном времени — экспериментальный" });
        TextureModeCombo.Items.Add(new ComboBoxItem { Tag = HeroDogTextureMode.CompatibilityReadback,
            Content = "Совместимый Readback — альтернативный режим" });
        TextureModeCombo.Items.Add(new ComboBoxItem { Tag = HeroDogTextureMode.DiagnosticOff,
            Content = "Стабильный режим v22 — без GPU Morphing / Forced Readback" });

        foreach (ComboBox combo in new[] { MouseLmbCombo, MouseMmbCombo, MouseRmbCombo })
        {
            combo.Items.Add(new ComboBoxItem { Tag = "", Content = "— не назначено —" });
            foreach (string name in Fable2ConfigFile.ButtonNames)
                combo.Items.Add(new ComboBoxItem { Tag = name, Content = DescribeGamepadButton(name) });
        }
    }

    private static string DescribeGamepadButton(string name) => name switch
    {
        "A" => "A — основное действие",
        "B" => "B — дополнительно",
        "X" => "X — действие",
        "Y" => "Y — альтернативно",
        _ => name,
    };

    private void SetDefaults()
    {
        GameSpeedSlider.Value = 10;
        UpdateGameSpeedLabel();
        SelectByTag(ResolutionCombo, "1080p");
        SelectByTag(RenderScaleCombo, "2");
        SelectByTag(AnisotropicCombo, "5");
        SelectByTag(AntiAliasingCombo, "none");
        SelectByTag(DisplayModeCombo, "borderless");
        SelectByTag(FrameLimitCombo, "0");
        VsyncCheck.IsChecked = true;
    }

    private void DetectGameDirectory()
    {
        string launcherDirectory = AppContext.BaseDirectory;
        string? configuredPath = LauncherState.LoadGameDirectory();
        if (!string.IsNullOrWhiteSpace(configuredPath) &&
            File.Exists(Path.Combine(configuredPath, "default.xex")) &&
            (File.Exists(Path.Combine(launcherDirectory, "fable_2.exe")) ||
             File.Exists(Path.Combine(configuredPath, "fable_2.exe"))))
        {
            SetGameDirectory(configuredPath);
            return;
        }
        // No saved (or no longer valid) folder: use the launcher's own folder.
        SetGameDirectory(launcherDirectory);
    }

    private void SetGameDirectory(string directory)
    {
        _gameDirectory = Path.GetFullPath(directory);
        _executableDirectory = File.Exists(Path.Combine(AppContext.BaseDirectory, "fable_2.exe"))
            ? AppContext.BaseDirectory : _gameDirectory;
        GamePathText.Text = _gameDirectory;
        LauncherState.SaveGameDirectory(_gameDirectory);
        try
        {
            bool recovered = StabilityRecovery.TryRestoreCompatibilityReadback(AdvancedConfigPath(), out string recoveryMessage);
            if (recovered) ShowStatus(recoveryMessage);
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message);
        }
        ReloadAllFromDisk();
        UpdateGameStatus();
        ApplyLocalizedTexts();
    }

    /// <summary>Reads fable_2.toml / launcher-settings.toml and fable2_config.toml into the UI.</summary>
    private void ReloadAllFromDisk()
    {
        try { LoadConfiguration(); }
        catch (Exception exception) { ShowStatus(exception.Message); }
        try { LoadAdvanced(); }
        catch (Exception exception)
        {
            ShowStatus(L("Не удалось прочитать расширенные настройки") + ": " + exception.Message);
        }
        _savedSignature = Signature();
        UpdateDirty();
    }

    private void LoadConfiguration()
    {
        if (_gameDirectory is null) return;
        _loading = true;
        try
        {
            _values.Clear();
            SetDefaults();
            foreach ((string key, string value) in LauncherConfigFile.ReadLauncherValues(_executableDirectory!))
                _values[key] = value;

            string outputResolution = (GetValue("window_width", ""), GetValue("window_height", "")) switch
            {
                ("1280", "720") => "720p",
                ("1920", "1080") => "1080p",
                ("2560", "1440") => "1440p",
                ("3840", "2160") => "4k",
                _ => Unquote(GetValue("resolution", "1080p"))
            };
            SelectByTag(ResolutionCombo, outputResolution);
            SelectByTag(RenderScaleCombo, GetValue("resolution_scale", "2"));
            SelectByTag(AnisotropicCombo, GetValue("anisotropic_override", "5"));
            SelectByTag(AntiAliasingCombo, Unquote(GetValue("swap_post_effect", "none")));
            SelectByTag(FrameLimitCombo, GetValue("frame_limit", "0"));
            bool fullscreen = ParseBool(GetValue("fullscreen", "true"), true);
            bool exclusive = ParseBool(GetValue("fullscreen_exclusive", "false"), false);
            SelectByTag(DisplayModeCombo, !fullscreen ? "windowed" : exclusive ? "exclusive" : "borderless");
            VsyncCheck.IsChecked = ParseBool(GetValue("vsync", "true"), true);
        }
        finally { _loading = false; }
        RefreshSummary();
    }

    private void LoadAdvanced()
    {
        _advanced = Fable2ConfigFile.Read(AdvancedConfigPath());
        _loading = true;
        try
        {
            GameSpeedSlider.Value = GameSpeedSettings.ToStep(_advanced.GameSpeed);
            UpdateGameSpeedLabel();
            MotionBlurCheck.IsChecked = _advanced.DisableMotionBlur;
            MouseLookCheck.IsChecked = _advanced.MouseLook;
            MouseSensitivitySlider.Value = Math.Clamp(_advanced.MouseLookScale, 1, 4096);
            MouseSensitivityBox.Text = _advanced.MouseLookScale.ToString(CultureInfo.InvariantCulture);
            HotFuncYieldSlider.Value = Math.Clamp(_advanced.HotFuncYieldEvery, 0, 16);
            HotFuncYieldBox.Text = _advanced.HotFuncYieldEvery.ToString(CultureInfo.InvariantCulture);
            BuildKeybindRows(Fable2ConfigFile.ParseKeybinds(_advanced.KeyboardGamepadMap));
            SelectByTag(ControllerModeCombo, ControllerPromptSettings.Load() switch
            {
                ControllerPromptMode.PlayStation => "playstation",
                ControllerPromptMode.KeyboardMouse => "keyboard",
                _ => "xbox",
            });
            UnlockWebsiteCheck.IsChecked = _advanced.UnlockWebsite;
            UnlockCeCheck.IsChecked = _advanced.UnlockCe;
            SkipIntroCheck.IsChecked = _advanced.SkipIntroVideos;
            // HF ticks require the base high tick rate.
            HighTickCheck.IsChecked = _advanced.HighTickRate || _advanced.HigherHfTickRate;
            HfTickCheck.IsChecked = _advanced.HigherHfTickRate;
            _loadedTextureMode = TextureModeSettings.Get(_advanced);
            SelectByTag(TextureModeCombo, _loadedTextureMode);
            ReadbackOverrideCheck.IsChecked = false;
        }
        finally { _loading = false; }
        UpdateControllerModeUi();
        UpdateHfTickAvailability();
        UpdateTextureModeHelp();
        RefreshMouseButtonCombos();
    }

    private string GetValue(string key, string fallback) =>
        _values.TryGetValue(key, out string? value) ? value : fallback;

    private static string Unquote(string value) => value.Trim().Trim('"', '\'');

    private static bool ParseBool(string value, bool fallback) =>
        bool.TryParse(Unquote(value), out bool parsed) ? parsed : fallback;

    private static string? SelectedTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static void SelectByTag(ComboBox combo, object tag)
    {
        string wanted = tag.ToString() ?? "";
        foreach (object item in combo.Items)
        {
            if (item is ComboBoxItem comboItem &&
                string.Equals(comboItem.Tag?.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = comboItem;
                return;
            }
        }
        // An unknown value must not silently select the first (lowest) option.
    }

    // ----------------------------------------------------------- game status

    private void UpdateGameStatus()
    {
        if (_gameDirectory is null || _executableDirectory is null) return;
        bool hasXex = File.Exists(Path.Combine(_gameDirectory, "default.xex"));
        GameCompatibility? compatibility = null;
        string? problem = null;
        try
        {
            compatibility = GameCompatibilityInspector.Inspect(_gameDirectory);
            GameLaunchPlanner.Create(_executableDirectory, _gameDirectory, compatibility);
        }
        catch (Exception exception) { problem = exception.Message; }
        _gameReady = hasXex && problem is null;

        // Home bar
        NotFoundOverlay.Visibility = hasXex ? Visibility.Collapsed : Visibility.Visible;
        GameMissingPill.Visibility = hasXex ? Visibility.Collapsed : Visibility.Visible;
        GameFoundPanel.Visibility = hasXex ? Visibility.Visible : Visibility.Collapsed;
        HomeLinks.Visibility = hasXex ? Visibility.Visible : Visibility.Collapsed;
        HomeNotFoundHint.Visibility = hasXex ? Visibility.Collapsed : Visibility.Visible;
        HomeVersionText.Text = hasXex ? "ВЕРСИЯ ЛАУНЧЕРА " + LauncherVersion : "ВЕРСИЯ " + LauncherVersion;
        LaunchButton.IsEnabled = _gameReady && !_diagnosticRunning;

        string profileName = compatibility?.Label ?? "";
        const string FoundPrefix = "Обнаружена ";
        if (profileName.StartsWith(FoundPrefix, StringComparison.Ordinal))
            profileName = profileName[FoundPrefix.Length..];

        if (_gameReady)
        {
            GameStatusDot.Fill = SuccessBrush;
            GameStatusTitle.Text = "Игра найдена";
            GameStatusSubtitle.Text = profileName.ToUpperInvariant();
            GameFoundPanel.ToolTip = compatibility?.Message;
        }
        else if (hasXex)
        {
            GameStatusDot.Fill = WarningBrush;
            GameStatusTitle.Text = "Запуск недоступен";
            GameStatusSubtitle.Text = compatibility?.Label ?? "";
            GameFoundPanel.ToolTip = problem;
        }

        // Files section
        FilesStatusDot.Fill = _gameReady ? SuccessBrush : hasXex ? WarningBrush : DangerBrush;
        FilesStatusText.Text = _gameReady ? "Найдена " + profileName
            : hasXex ? (compatibility?.Label ?? "Запуск недоступен") : "Файлы игры не найдены";
        FilesStatusDetail.Text = _gameReady ? compatibility?.Message ?? "" : problem ?? "";
        _achievementProfile = hasXex && profileName.Length > 0 ? profileName.ToUpperInvariant() : "";
        RefreshAchievements();
        RefreshPromptResourceText();
        ApplyLocalizedTexts();
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusBorder.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        ApplyLocalizedTexts();
    }

    // ---------------------------------------------------------- dirty state

    private bool IsDirty => !_loading && _savedSignature.Length > 0 && Signature() != _savedSignature;

    /// <summary>Every persisted setting in one string; per-session toggles are excluded.</summary>
    private string Signature()
    {
        var parts = new List<string>
        {
            SelectedTag(ResolutionCombo) ?? "", SelectedTag(RenderScaleCombo) ?? "",
            SelectedTag(AnisotropicCombo) ?? "", SelectedTag(AntiAliasingCombo) ?? "",
            SelectedTag(DisplayModeCombo) ?? "", SelectedTag(FrameLimitCombo) ?? "",
            (VsyncCheck.IsChecked == true).ToString(), (MotionBlurCheck.IsChecked == true).ToString(),
            GameSpeedSlider.Value.ToString(CultureInfo.InvariantCulture),
            SelectedTag(ControllerModeCombo) ?? "",
            (MouseLookCheck.IsChecked == true).ToString(),
            ((int)MouseSensitivitySlider.Value).ToString(CultureInfo.InvariantCulture),
            HotFuncYieldBox.Text.Trim(),
            CollectKeybinds(),
            (UnlockWebsiteCheck.IsChecked == true).ToString(), (UnlockCeCheck.IsChecked == true).ToString(),
            (SkipIntroCheck.IsChecked == true).ToString(), (HighTickCheck.IsChecked == true).ToString(),
            (HfTickCheck.IsChecked == true).ToString(), SelectedTextureMode().ToString(),
        };
        return string.Join("\u001f", parts);
    }

    private void UpdateDirty()
    {
        if (UnsavedIndicator is null || SaveButton is null) return;
        bool dirty = IsDirty;
        UnsavedIndicator.Text = "●  Есть несохранённые изменения";
        UnsavedIndicator.Foreground = WarningBrush;
        UnsavedIndicator.Visibility = dirty ? Visibility.Visible : Visibility.Hidden;
        SaveButton.Content = dirty ? "СОХРАНИТЬ *" : "СОХРАНИТЬ";
        UpdatePresetIndicator();
        ApplyLocalizedTexts();
    }

    private void ShowSavedIndicator()
    {
        UnsavedIndicator.Text = "✓  Настройки сохранены";
        UnsavedIndicator.Foreground = SuccessBrush;
        UnsavedIndicator.Visibility = Visibility.Visible;
        ApplyLocalizedTexts();
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshSummary();
        UpdateDirty();
    }

    private void SettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        RefreshSummary();
        UpdateDirty();
    }

    // ----------------------------------------------------------------- save

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (SaveAll(interactive: true)) ShowSavedIndicator();
    }

    /// <summary>
    /// One save for everything: fable2_config.toml (patches, controls, speed,
    /// texture mode, HOTFUNC), the controller prompt mode, then the graphics
    /// transaction for fable_2.toml / launcher-settings.toml.
    /// </summary>
    private bool SaveAll(bool interactive)
    {
        if (_gameDirectory is null || _executableDirectory is null ||
            !File.Exists(Path.Combine(_executableDirectory, "fable_2.exe")))
        {
            if (interactive)
                MessageBox.Show(this, L("Сначала выберите папку, содержащую fable_2.exe."),
                    L("Игра не найдена"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        CommitNumberBoxes();
        HeroDogTextureMode mode = SelectedTextureMode();
        if (interactive && mode == HeroDogTextureMode.DiagnosticOff && _loadedTextureMode != mode &&
            MessageBox.Show(this, L("Отключить оба исправления текстур? Герой и собака могут отображаться чёрными."),
                L("Только диагностический режим"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;

        _advanced.GameSpeed = SelectedGameSpeed();
        _advanced.DisableMotionBlur = MotionBlurCheck.IsChecked == true;
        _advanced.MouseLook = MouseLookCheck.IsChecked == true;
        _advanced.MouseLookScale = (int)MouseSensitivitySlider.Value;
        _advanced.HotFuncYieldEvery = int.TryParse(HotFuncYieldBox.Text, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int yieldEvery)
            ? Math.Clamp(yieldEvery, 0, 4096) : (int)HotFuncYieldSlider.Value;
        _advanced.KeyboardGamepadMap = CollectKeybinds();
        _advanced.UnlockWebsite = UnlockWebsiteCheck.IsChecked == true;
        _advanced.UnlockCe = UnlockCeCheck.IsChecked == true;
        _advanced.SkipIntroVideos = SkipIntroCheck.IsChecked == true;
        _advanced.HighTickRate = HighTickCheck.IsChecked == true;
        _advanced.HigherHfTickRate = HighTickCheck.IsChecked == true && HfTickCheck.IsChecked == true;
        TextureModeSettings.Apply(_advanced, mode);

        Dictionary<string, string> graphics = CollectSettings();
        try
        {
            Fable2ConfigFile.Write(AdvancedConfigPath(), _advanced);
            ControllerPromptSettings.Save(SelectedControllerMode());
            GraphicsConfigurationTransaction.Save(_executableDirectory,
                graphics, GraphicsSettings.ManagedKeys, SelectedGameSpeed());
            _values.Clear();
            foreach ((string key, string value) in graphics) _values[key] = value;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, L(exception.Message), L("Не удалось сохранить настройки"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        _loadedTextureMode = mode;
        _savedSignature = Signature();
        UpdateDirty();
        UpdateTextureModeHelp();
        return true;
    }

    private Dictionary<string, string> CollectSettings()
    {
        return GraphicsSettings.Create(SelectedTag(ResolutionCombo) ?? "1080p",
            SelectedTag(RenderScaleCombo) ?? "2", SelectedTag(AnisotropicCombo) ?? "5",
            SelectedTag(AntiAliasingCombo) ?? "none", SelectedTag(DisplayModeCombo) ?? "borderless",
            VsyncCheck.IsChecked == true, SelectedTag(FrameLimitCombo) ?? "0");
    }

    private void CommitNumberBoxes()
    {
        MouseSensitivityBoxLostFocus(MouseSensitivityBox, new RoutedEventArgs());
        HotFuncYieldBoxLostFocus(HotFuncYieldBox, new RoutedEventArgs());
    }

    // ---------------------------------------------------------------- launch

    private void LaunchClicked(object sender, RoutedEventArgs e) => LaunchGame();

    private async void LaunchGame()
    {
        if (_extracting || _diagnosticRunning) return;
        if (_gameDirectory is null || _executableDirectory is null) return;
        LaunchDiagnostics? session = null;
        try
        {
            var plan = GameLaunchPlanner.Create(_executableDirectory, _gameDirectory,
                GameCompatibilityInspector.Inspect(_gameDirectory));
            if (SelectedGameSpeed() != GameSpeedSettings.Default &&
                !GameSpeedSettings.SupportsRuntime(plan.Executable))
                throw new InvalidOperationException(L("Для скорости отличной от 1,0× нужен новый fable_2.exe.\n\nЗакройте игру и запустите BUILD-GAME-SPEED-LOCAL.cmd из папки исходников. Одной пересборки лаунчера недостаточно."));
            // Graphics keys are always rewritten before launch (as in 1.6.2).
            if (!SaveAll(interactive: IsDirty)) return;
            var saved = Fable2ConfigFile.Read(AdvancedConfigPath());
            var startInfo = new ProcessStartInfo
            {
                FileName = plan.Executable,
                WorkingDirectory = plan.WorkingDirectory,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--game_data_root=" + plan.GameRoot);
            if (ReadbackOverrideCheck.IsChecked == true &&
                TextureModeSettings.Get(saved) == HeroDogTextureMode.CompatibilityReadback)
                TextureModeSettings.AddReadbackOverride(startInfo, saved);

            ControllerPromptMode controllerMode = ControllerPromptSettings.Load();
            string controllerStatus = controllerMode switch
            {
                ControllerPromptMode.PlayStation => DualShockPromptPatcher.Apply(_gameDirectory, true),
                ControllerPromptMode.KeyboardMouse => DualShockPromptPatcher.ApplyKeyboardMouse(
                    _gameDirectory, saved.KeyboardGamepadMap),
                _ => DualShockPromptPatcher.Apply(_gameDirectory, false),
            };
            // PubGames use their own SWF symbols, not the common GameFace BNKs.
            string pubGamesStatus = PubGamePromptPatcher.Apply(_gameDirectory,
                controllerMode, saved.KeyboardGamepadMap, saved.MouseLook);
            ShowStatus(controllerStatus + " " + pubGamesStatus);
            bool diagnostic = DiagnosticCheck.IsChecked == true;
            if (diagnostic)
            {
                _diagnosticRunning = true;
                SetInteractive(false);
                ShowStatus("Подготовка локального диагностического отчёта…");
                session = await Task.Run(() => LaunchDiagnostics.Begin(plan, startInfo, controllerMode, controllerStatus, pubGamesStatus));
            }
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException(L("Не удалось запустить процесс игры."));
            ShowStatus("Fable II запущена.");
            if (!diagnostic) { _forceClose = true; Close(); return; }
            session!.MarkStarted(process.Id);
            WindowState = WindowState.Minimized;
            await process.WaitForExitAsync();
            int exitCode = process.ExitCode;
            await Task.Run(() => session.Complete(exitCode));
            _diagnosticRunning = false;
            WindowState = WindowState.Normal;
            Activate();
            ShowStatus($"Игра завершилась с кодом {LaunchDiagnostics.FormatExitCode(exitCode)}. Отчёт: {session.ResultPath}");
            bool russian = LauncherLanguage.Current == LauncherLanguage.Russian;
            string zipNote = session.ArchivePath is not null
                ? (russian
                    ? "\n\nАрхив ZIP создан автоматически. Для отправки выберите этот .zip; папка также сохранена."
                    : "\n\nThe ZIP archive was created automatically. Share this .zip file; the source folder is also retained.")
                : (russian ? "\n\nОшибка ZIP: " : "\n\nZIP creation failed: ") + session.ArchiveError;
            string dumpNote = exitCode == 0 ? "" : session.CrashDumpDetected
                ? (russian
                    ? "\n\nДамп WER найден. См. crash-dump-status.txt. Дамп НЕ включён в ZIP, поскольку может содержать личные данные."
                    : "\n\nWER dump located; see crash-dump-status.txt. It is NOT included in the ZIP because it may contain private data.")
                : session.WerRegistryConfigured
                    ? (russian
                        ? "\n\nДамп пока не найден, хотя WER настроен. Проверь crash-dump-status.txt."
                        : "\n\nNo dump was found despite WER registry settings. Check crash-dump-status.txt.")
                    : (russian
                        ? "\n\nДампы WER не настроены. Запусти ENABLE-FABLE2-WER-DUMPS.cmd от имени администратора для следующего теста."
                        : "\n\nWER dumps are not configured. Run ENABLE-FABLE2-WER-DUMPS.cmd as administrator before the next test.");
            MessageBox.Show(this, L($"Код завершения процесса: {LaunchDiagnostics.FormatExitCode(exitCode)}\n\nЛокальный отчёт сохранён:\n{session.ResultPath}\n\nНикакие файлы не отправлялись. Перед передачей отчёта проверьте пути и настройки. Один код завершения не определяет причину сбоя.") + zipNote + dumpNote,
                L("Диагностический сеанс завершён"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            if (session is not null)
            {
                try { await Task.Run(() => session.Complete(null, exception.Message)); }
                catch { /* Keep the original error. The prepared report remains on disk. */ }
            }
            WindowState = WindowState.Normal;
            MessageBox.Show(this, L(exception.Message), L("Ошибка запуска или диагностики"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _diagnosticRunning = false;
            SetInteractive(true);
            UpdateGameStatus();
        }
    }

    private void SetInteractive(bool enabled)
    {
        HomeSettingsButton.IsEnabled = enabled;
        SettingsPage.IsEnabled = enabled;
        LaunchButton.IsEnabled = enabled && _gameReady;
    }

    // -------------------------------------------------------------- graphics

    private void RefreshSummary()
    {
        int scale = int.TryParse(SelectedTag(RenderScaleCombo), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int parsedScale) ? parsedScale : 1;
        string output = SelectedTag(ResolutionCombo) switch
        {
            "720p" => "1280 × 720",
            "1080p" => "1920 × 1080",
            "1440p" => "2560 × 1440",
            "4k" => "3840 × 2160",
            _ => "Автоматически"
        };
        QualitySummaryText.Text = $"{1280 * scale} × {720 * scale}  →  {output}";
        ApplyLocalizedTexts();
    }

    private void PresetClicked(object sender, RoutedEventArgs e)
    {
        string preset = (sender as FrameworkElement)?.Tag?.ToString() ?? "balanced";
        _loading = true;
        try
        {
            switch (preset)
            {
                case "original":
                    SelectByTag(ResolutionCombo, "720p");
                    SelectByTag(RenderScaleCombo, "1");
                    SelectByTag(AnisotropicCombo, "-1");
                    break;
                case "balanced":
                    SelectByTag(ResolutionCombo, "1080p");
                    SelectByTag(RenderScaleCombo, "2");
                    SelectByTag(AnisotropicCombo, "4");
                    break;
                case "enhanced":
                    SelectByTag(ResolutionCombo, "4k");
                    SelectByTag(RenderScaleCombo, "3");
                    SelectByTag(AnisotropicCombo, "5");
                    break;
                case "ultra":
                    SelectByTag(ResolutionCombo, "4k");
                    SelectByTag(RenderScaleCombo, "4");
                    SelectByTag(AnisotropicCombo, "5");
                    break;
            }
            SelectByTag(DisplayModeCombo, "borderless");
            SelectByTag(AntiAliasingCombo, "none");
            VsyncCheck.IsChecked = true;
        }
        finally { _loading = false; }
        RefreshSummary();
        UpdateDirty();
    }

    /// <summary>Highlights the preset that matches the current graphics values.</summary>
    private void UpdatePresetIndicator()
    {
        if (PresetOriginal is null) return;
        string key = string.Join("|", SelectedTag(ResolutionCombo), SelectedTag(RenderScaleCombo),
            SelectedTag(AnisotropicCombo), SelectedTag(DisplayModeCombo), SelectedTag(AntiAliasingCombo),
            VsyncCheck.IsChecked == true);
        string? match = key switch
        {
            "720p|1|-1|borderless|none|True" => "original",
            "1080p|2|4|borderless|none|True" => "balanced",
            "4k|3|5|borderless|none|True" => "enhanced",
            "4k|4|5|borderless|none|True" => "ultra",
            _ => null,
        };
        foreach (RadioButton preset in new[] { PresetOriginal, PresetBalanced, PresetEnhanced, PresetUltra })
            preset.IsChecked = (string)preset.Tag == match;
        string state = match switch
        {
            "original" => "Текущий профиль: Оригинал.",
            "balanced" => "Текущий профиль: Сбалансированный.",
            "enhanced" => "Текущий профиль: Улучшенный.",
            "ultra" => "Текущий профиль: Ультра (экспериментально, очень высокий расход видеопамяти).",
            _ => "Текущий профиль: Пользовательский.",
        };
        PresetDescription.Text = state + " " + "Пресет меняет несколько параметров графики. После ручной настройки состояние — «Пользовательский».";
    }

    // ------------------------------------------------------------ game speed

    private double SelectedGameSpeed() => GameSpeedSettings.FromStep(GameSpeedSlider.Value);

    private void UpdateGameSpeedLabel()
    {
        bool russian = LauncherLanguage.Current == LauncherLanguage.Russian;
        if (GameSpeedValueText is not null && GameSpeedSlider is not null)
            GameSpeedValueText.Text = SelectedGameSpeed().ToString("0.0",
                CultureInfo.GetCultureInfo(russian ? "ru-RU" : "en-US")) + "×";
        if (GameSpeedMinText is not null) GameSpeedMinText.Text = russian ? "0,1×" : "0.1×";
        if (GameSpeedMaxText is not null) GameSpeedMaxText.Text = russian ? "2,0×" : "2.0×";
    }

    private void GameSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateGameSpeedLabel();
        if (!_loading) UpdateDirty();
    }

    private void ResetGameSpeedClicked(object sender, RoutedEventArgs e) => GameSpeedSlider.Value = 10;

    // ------------------------------------------------------------- game files

    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = L("Выберите папку оригинальной игры (default.xex и data)"),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!File.Exists(Path.Combine(dialog.FolderName, "default.xex")))
        {
            MessageBox.Show(this, L("В этой папке нет файла default.xex."),
                L("Игра не найдена"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (IsDirty) AskAboutUnsaved(() => SetGameDirectory(dialog.FolderName));
        else SetGameDirectory(dialog.FolderName);
    }

    private bool _extracting;
    private volatile bool _stopRequested;
    // Non-null while the "unrecognized XEX" prompt is on screen.
    private TaskCompletionSource<bool>? _hashDecision;
    private readonly Dictionary<string, CheckBox> _rootChecks = new(StringComparer.OrdinalIgnoreCase);
    private long _lastUiPush;
    private double _lastPushedPercent = -1;
    private static readonly SolidColorBrush DoneBrush = new(Color.FromRgb(0x8F, 0xD1, 0x9A));

    private void ExtractClicked(object sender, RoutedEventArgs e)
    {
        if (_extracting) return;

        var isoDialog = new OpenFileDialog
        {
            Title = L("Выберите ISO-образ"),
            Filter = "ISO (*.iso)|*.iso",
            CheckFileExists = true
        };
        if (isoDialog.ShowDialog(this) != true) return;
        string isoPath = isoDialog.FileName;

        // Extract straight into the launcher folder, beside fable_2.exe.
        string outDir = AppContext.BaseDirectory;

        _extracting = true;
        _stopRequested = false;
        ExtractButton.IsEnabled = false;
        LaunchButton.IsEnabled = false;
        StopButton.IsEnabled = true;

        ExtractionOverlay.Visibility = Visibility.Visible;
        ExtractionProgressBar.IsIndeterminate = true;
        ExtractionPercentText.Text = "…";
        ExtractionBytesText.Text = "Сканирование…";
        ExtractionPhaseText.Text = "Сканирование диска…";
        ExtractionFileText.Text = "Поиск заголовка GDFX…";
        ExtractionFolderList.Children.Clear();
        _rootChecks.Clear();
        HashPrompt.Visibility = Visibility.Collapsed;
        ApplyLocalizedTexts();

        Task.Run(async () =>
        {
            string? error = null;
            int files = 0;
            long bytes = 0;
            bool cancelled = false;
            try
            {
                using DiscExtraction disc = DiscExtraction.Open(isoPath, _ => { });
                List<RootItem> plan = disc.Plan.ToList();

                var progress = new ExtractionProgress(plan, () => _stopRequested);
                progress.Changed += p => Dispatcher.BeginInvoke(() => PushExtractionUi(p));
                progress.RootCompleted += name =>
                    Dispatcher.BeginInvoke(new Action(() => MarkRootDone(name)));

                if (_stopRequested)
                    throw new ExtractionCancelledException();

                Dispatcher.Invoke(() =>
                {
                    BuildFolderChecklist(plan);
                    ResetExtractionUi(progress);
                });

                // Extract the XEX under a temporary name so a failed hash check
                // never clobbers an existing default.xex.
                const string xexNewName = "default_new.xex";
                string xexNew = Path.Combine(outDir, xexNewName);
                string hash = disc.ExtractXex(outDir, _ => { }, progress, xexNewName);
                progress.SetPhase("Проверка SHA-256 файла default.xex…");
                (bool ok, string message) = GameCompatibilityInspector.CheckHash(hash);
                if (!ok)
                {
                    var decision = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _hashDecision = decision;
                    Dispatcher.Invoke(() => ShowHashPrompt(message));
                    bool extractAnyways = await decision.Task;
                    if (ReferenceEquals(_hashDecision, decision))
                        _hashDecision = null;
                    if (!extractAnyways)
                    {
                        try { File.Delete(xexNew); }
                        catch { /* best effort */ }
                        throw new ExtractionCancelledException();
                    }
                }

                try
                {
                    File.Move(xexNew, Path.Combine(outDir, "default.xex"), overwrite: true);
                }
                catch (Exception moveEx)
                {
                    throw new InvalidDataException(
                        "Не удалось сохранить default.xex: " + moveEx.Message);
                }

                (files, bytes) = disc.ExtractRemaining(outDir, _ => { }, progress);
                progress.SetPhase("Завершение…");
            }
            catch (ExtractionCancelledException)
            {
                cancelled = true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }

            Dispatcher.Invoke(() =>
            {
                _extracting = false;
                _stopRequested = false;
                ExtractButton.IsEnabled = true;
                LaunchButton.IsEnabled = _gameReady;
                if (cancelled)
                {
                    ExtractionOverlay.Visibility = Visibility.Collapsed;
                    ExtractionStatusText.Visibility = Visibility.Visible;
                    ExtractionStatusText.Text = "Извлечение остановлено.";
                    ApplyLocalizedTexts();
                    return;
                }
                if (error is not null)
                {
                    ExtractionOverlay.Visibility = Visibility.Collapsed;
                    ExtractionStatusText.Visibility = Visibility.Visible;
                    ExtractionStatusText.Text = "Ошибка извлечения.";
                    ApplyLocalizedTexts();
                    MessageBox.Show(this, L(error), L("Не удалось извлечь ISO"),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                ExtractionProgressBar.Value = 100;
                ExtractionPercentText.Text = "100%";
                ExtractionPhaseText.Text = "Готово";
                ExtractionStatusText.Visibility = Visibility.Visible;
                ExtractionStatusText.Text =
                    $"Извлечено файлов: {files:N0} ({DiscExtraction.FormatSize(bytes)}) из {Path.GetFileName(isoPath)}.";
                SetGameDirectory(outDir);
                HideOverlayAfter(900);
                ApplyLocalizedTexts();
            });
        });
    }

    private void StopClicked(object sender, RoutedEventArgs e)
    {
        if (!_extracting) return;
        if (_hashDecision is not null)
        {
            DeclineHashDecision();
            return;
        }
        _stopRequested = true;
        StopButton.IsEnabled = false;
        ExtractionPhaseText.Text = "Остановка…";
        ApplyLocalizedTexts();
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => DeclineHashDecision();

    private void DeclineHashDecision()
    {
        if (_hashDecision is null) return;
        _hashDecision.TrySetResult(false);
        StopButton.IsEnabled = false;
        HashPrompt.Visibility = Visibility.Collapsed;
        ExtractionPhaseText.Text = "Остановка…";
        ApplyLocalizedTexts();
    }

    private void ExtractAnywaysClicked(object sender, RoutedEventArgs e)
    {
        if (_hashDecision is null) return;
        _hashDecision.TrySetResult(true);
        HashPrompt.Visibility = Visibility.Collapsed;
        ExtractionPhaseText.Text = "Извлечение…";
        ApplyLocalizedTexts();
    }

    private void ShowHashPrompt(string message)
    {
        HashPromptText.Text = message;
        ExtractionPhaseText.Text = "Неизвестный default.xex";
        ApplyLocalizedTexts();
        StopButton.IsEnabled = true;
        HashPrompt.Visibility = Visibility.Visible;
    }

    private void BuildFolderChecklist(List<RootItem> plan)
    {
        ExtractionFolderList.Children.Clear();
        _rootChecks.Clear();
        foreach (RootItem item in plan)
        {
            var box = new CheckBox
            {
                Content = item.Name,
                IsChecked = false,
                IsHitTestVisible = false,   // display-only progress checkbox
                FontSize = 17,
                Width = 300,
                FontWeight = item.IsDirectory ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = LightBrush,
                Margin = new Thickness(0, 4, 12, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            ExtractionFolderList.Children.Add(box);
            _rootChecks[item.Name] = box;
        }
    }

    private void ResetExtractionUi(ExtractionProgress progress)
    {
        _lastPushedPercent = -1;
        ExtractionProgressBar.IsIndeterminate = false;
        ExtractionProgressBar.Value = 0;
        ExtractionPercentText.Text = "0%";
        ExtractionBytesText.Text =
            $"{DiscExtraction.FormatSize(0)} / {DiscExtraction.FormatSize(progress.TotalBytes)}";
        ExtractionPhaseText.Text = "Чтение диска…";
        ExtractionFileText.Text = "Чтение диска…";
        ApplyLocalizedTexts();
    }

    private void PushExtractionUi(ExtractionProgress progress)
    {
        long now = Environment.TickCount64;
        double pct = progress.Percent;
        bool force = pct >= 100.0 || now - _lastUiPush >= 80 || pct - _lastPushedPercent >= 0.25;
        if (!force) return;
        _lastUiPush = now;
        _lastPushedPercent = pct;

        ExtractionProgressBar.Value = pct;
        ExtractionPercentText.Text = $"{pct:0}%";
        ExtractionBytesText.Text =
            $"{DiscExtraction.FormatSize(progress.CompletedBytes + progress.CurrentFileWritten)}"
            + $" / {DiscExtraction.FormatSize(progress.TotalBytes)}";
        ExtractionPhaseText.Text = progress.Phase;
        ExtractionFileText.Text = progress.CurrentFile ?? "…";
        ApplyLocalizedTexts();
    }

    private void MarkRootDone(string name)
    {
        if (!_rootChecks.TryGetValue(name, out CheckBox? box)) return;
        box.IsChecked = true;
        box.Foreground = DoneBrush;
    }

    private void HideOverlayAfter(int delayMs)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_extracting) ExtractionOverlay.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    private string AdvancedConfigPath() =>
        Path.Combine(_executableDirectory ?? AppContext.BaseDirectory, "fable2_config.toml");

    // ------------------------------------------------------ controller mode

    private ControllerPromptMode SelectedControllerMode() => SelectedTag(ControllerModeCombo) switch
    {
        "playstation" => ControllerPromptMode.PlayStation,
        "keyboard" => ControllerPromptMode.KeyboardMouse,
        _ => ControllerPromptMode.Xbox360,
    };

    private void ControllerModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateControllerModeUi();
        if (_loading) return;
        UpdateDirty();
    }

    private void UpdateControllerModeUi()
    {
        if (PsPromptsCheck is null || XboxPromptsCheck is null) return;
        ControllerPromptMode mode = SelectedControllerMode();
        PsPromptsCheck.IsChecked = mode == ControllerPromptMode.PlayStation;
        XboxPromptsCheck.IsChecked = mode == ControllerPromptMode.Xbox360;
        ControllerModeHelpText.Text = mode switch
        {
            ControllerPromptMode.PlayStation =>
                "Сейчас выбраны значки PlayStation: ✕ ○ □ △ и L1/L2/R1/R2. Внутренняя раскладка Xbox остаётся прежней.",
            ControllerPromptMode.KeyboardMouse =>
                "Сейчас выбраны значки Клавиатура/Мышь: круглые значки берутся из назначений клавиш.",
            _ => "Сейчас выбраны оригинальные значки Xbox 360.",
        };
        ApplyLocalizedTexts();
    }

    private void PsPromptsClicked(object sender, RoutedEventArgs e)
    {
        SelectByTag(ControllerModeCombo, PsPromptsCheck.IsChecked == true ? "playstation" : "xbox");
        UpdateControllerModeUi();
    }

    private void XboxPromptsClicked(object sender, RoutedEventArgs e)
    {
        // Turning Xbox prompts "off" has no single meaning; pick PS or K/M instead.
        if (XboxPromptsCheck.IsChecked == true) SelectByTag(ControllerModeCombo, "xbox");
        UpdateControllerModeUi();
    }

    private void RefreshPromptResourceText()
    {
        if (PromptResourceText is null || _gameDirectory is null) return;
        string bank = Path.Combine(_gameDirectory, DualShockPromptPatcher.RelativeBankPath.Replace('/', Path.DirectorySeparatorChar));
        string header = Path.Combine(_gameDirectory, DualShockPromptPatcher.RelativeHeaderBankPath.Replace('/', Path.DirectorySeparatorChar));
        bool found = File.Exists(bank) && File.Exists(header);
        bool backup = File.Exists(bank + DualShockPromptPatcher.BackupSuffix) &&
                      File.Exists(header + DualShockPromptPatcher.BackupSuffix);
        PromptResourceText.Text = !found
            ? "Файлы подсказок не найдены — проверьте папку игры"
            : backup
                ? "Оригинальные подсказки найдены · Резервная копия доступна"
                : "Оригинальные подсказки найдены · Резервная копия будет создана при первой замене";
        PromptResourceText.Foreground = found ? LightBrush : DangerBrush;
        ApplyLocalizedTexts();
    }

    // -------------------------------------------------------------- keybinds

    private void BuildKeybindRows(List<KeybindEntry> entries)
    {
        _keyRows.Clear();
        _activeKeyRow = null;
        foreach (KeybindEntry entry in entries)
            _keyRows.Add(CreateKeybindRow(entry.Key, entry.Button));
        LayoutKeybindRows();
    }

    /// <summary>Fills the left column first, then the right one (as in the design).</summary>
    private void LayoutKeybindRows()
    {
        KeybindListLeft.Children.Clear();
        KeybindListRight.Children.Clear();
        int half = (_keyRows.Count + 1) / 2;
        for (int i = 0; i < _keyRows.Count; i++)
            (i < half ? KeybindListLeft : KeybindListRight).Children.Add(_keyRows[i].Root);
    }

    private KeybindRow CreateKeybindRow(string key, string button)
    {
        var row = new KeybindRow { Key = key };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

        row.KeyText = new TextBlock
        {
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = LightBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.KeyField = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(10, 0, 8, 0),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Child = row.KeyText,
            ToolTip = "Кликните, затем нажмите клавишу или кнопку мыши",
        };
        SetKeyText(row);
        // First LMB click starts listening; while listening any mouse button sets it.
        row.KeyField.MouseDown += (_, e) =>
        {
            string? mouse = e.ChangedButton switch
            {
                MouseButton.Left => "LMB",
                MouseButton.Right => "RMB",
                MouseButton.Middle => "MMB",
                MouseButton.XButton1 => "XMB1",
                MouseButton.XButton2 => "XMB2",
                _ => null
            };
            if (mouse is null) return;
            e.Handled = true;
            if (mouse == "LMB" && !ReferenceEquals(_activeKeyRow, row))
            {
                BeginKeyCapture(row);
                return;
            }
            if (ReferenceEquals(_activeKeyRow, row))
                SetKeyCaptureValue(row, mouse);
        };

        row.ButtonCombo = new ComboBox { Style = (Style)FindResource("InlineCombo") };
        foreach (string name in Fable2ConfigFile.ButtonNames)
            row.ButtonCombo.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        row.ButtonCombo.SelectedItem = FindTag(row.ButtonCombo, button) ?? row.ButtonCombo.Items[0];
        row.ButtonCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            UpdateDirty();
        };

        var remove = new Button
        {
            Content = "×",
            Style = (Style)FindResource("LinkButton"),
            FontSize = 22,
            Foreground = MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Удалить это назначение",
        };
        remove.Click += (_, _) =>
        {
            if (ReferenceEquals(_activeKeyRow, row)) _activeKeyRow = null;
            _keyRows.Remove(row);
            LayoutKeybindRows();
            RefreshMouseButtonCombos();
            UpdateDirty();
        };

        Grid.SetColumn(row.KeyField, 0);
        Grid.SetColumn(row.ButtonCombo, 1);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(row.KeyField);
        grid.Children.Add(row.ButtonCombo);
        grid.Children.Add(remove);

        row.Root = new Border
        {
            Background = RowBackground,
            BorderBrush = RowBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2),
            Margin = new Thickness(0, 0, 0, 7),
            Child = grid,
        };
        return row;
    }

    private void SetKeyText(KeybindRow row)
    {
        row.KeyText.Text = string.IsNullOrWhiteSpace(row.Key) ? "нажмите клавишу…" : row.Key;
        row.KeyText.Foreground = string.IsNullOrWhiteSpace(row.Key) ? MutedBrush : LightBrush;
    }

    private static ComboBoxItem? FindTag(ComboBox combo, string tag)
    {
        foreach (object item in combo.Items)
            if (item is ComboBoxItem comboItem &&
                string.Equals(comboItem.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                return comboItem;
        return null;
    }

    private void BeginKeyCapture(KeybindRow row)
    {
        CancelKeyCapture();
        _activeKeyRow = row;
        row.KeyField.Background = RowListening;
        row.KeyField.BorderBrush = RowListeningBorder;
        row.KeyText.Text = "ожидание ввода…";
        row.KeyText.Foreground = GoldTextBrush;
        ApplyLocalizedTexts();
    }

    private void CancelKeyCapture()
    {
        if (_activeKeyRow is null) return;
        KeybindRow row = _activeKeyRow;
        _activeKeyRow = null;
        row.KeyField.Background = Brushes.Transparent;
        row.KeyField.BorderBrush = Brushes.Transparent;
        SetKeyText(row);
        ApplyLocalizedTexts();
    }

    private void SetKeyCaptureValue(KeybindRow row, string name)
    {
        row.Key = name;
        _activeKeyRow = null;
        row.KeyField.Background = Brushes.Transparent;
        row.KeyField.BorderBrush = Brushes.Transparent;
        SetKeyText(row);
        RefreshMouseButtonCombos();
        UpdateDirty();
    }

    // Map a WPF Key to the host-key name rex::ui::ParseVirtualKey expects.
    private static string? KeyToName(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key >= Key.F1 && key <= Key.F24) return "F" + (int)(key - Key.F1 + 1);
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Numpad" + (int)(key - Key.NumPad0);
        return key switch
        {
            Key.Add => "NumpadPlus",
            Key.Subtract => "NumpadMinus",
            Key.Multiply => "NumpadStar",
            Key.Divide => "NumpadSlash",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Back => "Backspace",
            Key.Return => "Return",
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.LeftShift => "Shift",
            Key.RightShift => "Shift",
            Key.LeftCtrl => "Control",
            Key.RightCtrl => "Control",
            Key.LeftAlt => "Alt",
            Key.RightAlt => "Alt",
            Key.PrintScreen => "PrintScreen",
            Key.Pause => "Pause",
            Key.CapsLock => "CapsLock",
            Key.NumLock => "NumLock",
            Key.Scroll => "ScrollLock",
            Key.OemTilde => "Backtick",
            Key.OemMinus => "Minus",
            Key.OemPlus => "Plus",
            Key.OemComma => "Comma",
            Key.OemPeriod => "Period",
            Key.OemSemicolon => "Semicolon",
            Key.OemQuestion => "Slash",
            Key.OemBackslash => "Backslash",
            Key.OemOpenBrackets => "LBracket",
            Key.OemCloseBrackets => "RBracket",
            Key.OemQuotes => "Quote",
            _ => null,
        };
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_activeKeyRow is null)
        {
            if (e.Key == Key.Escape)
            {
                if (AchievementsPage.Visibility == Visibility.Visible) { CloseAchievements(); e.Handled = true; }
                else if (UnsavedOverlay.Visibility == Visibility.Visible) { UnsavedCancelClicked(this, e); e.Handled = true; }
                else if (WhatsNewOverlay.Visibility == Visibility.Visible) { WhatsNewOverlay.Visibility = Visibility.Collapsed; e.Handled = true; }
            }
            return;
        }
        if (e.Key == Key.Escape)
        {
            CancelKeyCapture();
            e.Handled = true;
            return;
        }
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        string? name = KeyToName(key);
        if (name != null)
        {
            SetKeyCaptureValue(_activeKeyRow, name);
            e.Handled = true;
        }
    }

    private void AddKeybindClicked(object sender, RoutedEventArgs e)
    {
        KeybindRow row = CreateKeybindRow("", Fable2ConfigFile.ButtonNames[0]);
        _keyRows.Add(row);
        LayoutKeybindRows();
        BeginKeyCapture(row);
        row.Root.BringIntoView();
        UpdateDirty();
    }

    // Restore the built-in default map. Save still has to be pressed.
    private void ResetKeybindsClicked(object sender, RoutedEventArgs e)
    {
        BuildKeybindRows(Fable2ConfigFile.ParseKeybinds(Fable2ConfigValues.DefaultKeyboardGamepadMap));
        RefreshMouseButtonCombos();
        UpdateDirty();
        ApplyLocalizedTexts();
    }

    private string CollectKeybinds()
    {
        var entries = new List<KeybindEntry>();
        foreach (KeybindRow row in _keyRows)
        {
            string button = (row.ButtonCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(row.Key) || !string.IsNullOrWhiteSpace(button))
                entries.Add(new KeybindEntry { Key = row.Key, Button = button });
        }
        return Fable2ConfigFile.BuildKeybinds(entries);
    }

    // ---- mouse button shortcuts: edit the LMB / MMB / RMB keybind rows

    private void RefreshMouseButtonCombos()
    {
        if (MouseLmbCombo is null) return;
        _syncingMouseCombos = true;
        try
        {
            foreach (ComboBox combo in new[] { MouseLmbCombo, MouseMmbCombo, MouseRmbCombo })
            {
                KeybindRow? row = _keyRows.FirstOrDefault(r =>
                    string.Equals(r.Key, (string)combo.Tag, StringComparison.OrdinalIgnoreCase));
                string button = row is null ? "" :
                    (row.ButtonCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                combo.SelectedItem = FindTag(combo, button) ?? combo.Items[0];
            }
        }
        finally { _syncingMouseCombos = false; }
    }

    private void MouseButtonComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMouseCombos || _loading || sender is not ComboBox combo) return;
        string mouseKey = (string)combo.Tag;
        string button = SelectedTag(combo) ?? "";
        KeybindRow? row = _keyRows.FirstOrDefault(r =>
            string.Equals(r.Key, mouseKey, StringComparison.OrdinalIgnoreCase));
        if (button.Length == 0)
        {
            if (row is not null) { _keyRows.Remove(row); LayoutKeybindRows(); }
        }
        else if (row is null)
        {
            _keyRows.Insert(0, CreateKeybindRow(mouseKey, button));
            LayoutKeybindRows();
        }
        else
        {
            row.ButtonCombo.SelectedItem = FindTag(row.ButtonCombo, button) ?? row.ButtonCombo.SelectedItem;
        }
        UpdateDirty();
    }

    // ------------------------------------------------------- mouse sensitivity

    private void MouseSensitivitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MouseSensitivityBox is not null)
            MouseSensitivityBox.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
        if (!_loading) UpdateDirty();
    }

    private void MouseSensitivityBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(MouseSensitivityBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int value))
        {
            value = Math.Clamp(value, 1, 4096);
            MouseSensitivitySlider.Value = value;
            MouseSensitivityBox.Text = value.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            MouseSensitivityBox.Text = ((int)MouseSensitivitySlider.Value).ToString(CultureInfo.InvariantCulture);
        }
        if (!_loading) UpdateDirty();
    }

    // ------------------------------------------------------------- HOTFUNC

    private void HotFuncYieldSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HotFuncYieldBox is not null)
            HotFuncYieldBox.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
        if (!_loading) UpdateDirty();
    }

    private void HotFuncYieldBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(HotFuncYieldBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int value))
        {
            value = Math.Clamp(value, 0, 4096);
            bool previous = _loading;
            _loading = true;   // keep a typed value above 16 instead of the slider's clamp
            HotFuncYieldSlider.Value = Math.Clamp(value, 0, 16);
            _loading = previous;
            HotFuncYieldBox.Text = value.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            HotFuncYieldBox.Text = ((int)HotFuncYieldSlider.Value).ToString(CultureInfo.InvariantCulture);
        }
        if (!_loading) UpdateDirty();
    }

    // ------------------------------------------------------- experimental

    private void HighTickChanged(object sender, RoutedEventArgs e)
    {
        if (HighTickCheck.IsChecked != true) HfTickCheck.IsChecked = false;
        UpdateHfTickAvailability();
        if (!_loading) UpdateDirty();
    }

    private void UpdateHfTickAvailability()
    {
        if (HfTickCheck is null) return;
        bool available = HighTickCheck.IsChecked == true;
        HfTickCheck.IsEnabled = available;
        HfTickHelpText.Text = available
            ? "Нестабильно · HF-тики 30 → 60 Гц (режим Xenia «Higher HF Tick Rate»)"
            : "Недоступно: сначала включите «Повышенная частота тиков».";
        HfTickHelpText.Foreground = available ? DangerBrush : MutedBrush;
        ApplyLocalizedTexts();
    }

    // ------------------------------------------------------- texture mode

    private HeroDogTextureMode SelectedTextureMode() =>
        (TextureModeCombo.SelectedItem as ComboBoxItem)?.Tag is HeroDogTextureMode mode
            ? mode : TextureModeSettings.Get(_advanced);

    private void TextureModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTextureModeHelp();
        if (!_loading) UpdateDirty();
    }

    private void TechDetailsClicked(object sender, RoutedEventArgs e)
    {
        bool show = TextureInputSummaryText.Visibility != Visibility.Visible;
        TextureInputSummaryText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TechDetailsButton.Content = show
            ? "ДОПОЛНИТЕЛЬНЫЕ ТЕХНИЧЕСКИЕ ПАРАМЕТРЫ  ▴"
            : "ДОПОЛНИТЕЛЬНЫЕ ТЕХНИЧЕСКИЕ ПАРАМЕТРЫ  ▾";
        UpdateTextureModeHelp();
    }

    private void UpdateTextureModeHelp()
    {
        if (TextureModeHelpText is null || ReadbackOverrideCheck is null) return;
        HeroDogTextureMode mode = SelectedTextureMode();
        TextureModeHelpText.Text = mode switch
        {
            HeroDogTextureMode.GpuRealtime => "Экспериментальный GPU-режим. На текущей сборке он может затемнять изображение или нарушать отрисовку отдельных элементов интерфейса. Используйте только для проверки.",
            HeroDogTextureMode.CompatibilityReadback => "Отключает принудительный GPU-morphing; выполняет выборочное чтение результата через CPU.",
            _ => "Стабильный профиль v22: GPU realtime morphing и Forced Readback отключены. На отдельных сценах возможны прежние проблемы текстур героя/собаки."
        };
        TextureModeHelpText.Foreground = mode == HeroDogTextureMode.DiagnosticOff ? WarningBrush : MutedBrush;
        ReadbackOverrideCheck.IsEnabled = mode == HeroDogTextureMode.CompatibilityReadback;
        if (!ReadbackOverrideCheck.IsEnabled) ReadbackOverrideCheck.IsChecked = false;
        if (TextureInputSummaryText.Visibility == Visibility.Visible)
        {
            try
            {
                TextureInputSummaryText.Text = L("Загруженные / последние сохранённые настройки (сохраните, чтобы применить новый выбор):") + "\n" +
                    L(TextureModeSettings.DescribeRequest(_executableDirectory ?? AppContext.BaseDirectory, _advanced));
            }
            catch (Exception e) { TextureInputSummaryText.Text = L("Не удалось проверить настройки SDK: ") + e.Message; }
        }
        ApplyLocalizedTexts();
    }
}

internal static class LauncherState
{
    private static readonly string LocalStatePath = Path.Combine(AppContext.BaseDirectory,
        "launcher-game-path.txt");

    public static string? LoadGameDirectory()
    {
        try
        {
            if (File.Exists(LocalStatePath)) return File.ReadAllText(LocalStatePath).Trim();
            return null;
        }
        catch { return null; }
    }

    public static void SaveGameDirectory(string directory)
    {
        try
        {
            File.WriteAllText(LocalStatePath, directory);
        }
        catch
        {
            // Graphics saving reports write failures separately. No global
            // fallback: different installations must not inherit other paths.
        }
    }

    private static readonly string DualShockStatePath = Path.Combine(AppContext.BaseDirectory,
        "launcher-dualshock.txt");

    public static bool LoadDualShockEnabled()
    {
        try
        {
            if (!File.Exists(DualShockStatePath)) return false;
            string value = File.ReadAllText(DualShockStatePath).Trim();
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
        }
        catch { return false; }
    }

    public static void SaveDualShockEnabled(bool enabled)
    {
        File.WriteAllText(DualShockStatePath, enabled ? "true" : "false");
    }
}
