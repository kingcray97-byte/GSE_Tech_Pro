using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices.Abstractions;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace GseTechPro.App;

public sealed partial class MainWindow : Window
{
    private ResourceDictionary? _sunlightDictionary;
    private bool _suppressSliderSeek;
    private bool _suppressSunlightToggle;

    public MainWindow(
        DataServices dataServices,
        IDiagnosticAdapterFactory adapterFactory,
        IAiClient? aiClient = null,
        AiServiceLauncher? aiLauncher = null,
        IScopeDeviceFactory? scopeDeviceFactory = null,
        IOemModuleRegistry? oemModuleRegistry = null)
    {
        ViewModel = new MainShellViewModel(
            dataServices,
            adapterFactory,
            DispatcherQueue,
            aiClient,
            aiLauncher,
            aiStateService: null,
            scopeDeviceFactory: scopeDeviceFactory,
            oemModuleRegistry: oemModuleRegistry);
        InitializeComponent();

        RootGrid.DataContext = ViewModel;

        // Hand the view model the live XamlRoot once loaded so it can host confirmation
        // and error dialogs (e.g. Clear DTCs confirmation, Trigger Setup, Device Setup).
        RootGrid.Loaded += (_, _) =>
        {
            ViewModel.DialogXamlRoot = RootGrid.XamlRoot;
        };

        RootGrid.ActualThemeChanged += (_, _) => SyncThemeStateToViewModel();
        SyncThemeStateToViewModel();

        // Scope full screen (10D.1j): the view model hides the chrome; the window presenter follows it.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainShellViewModel.IsScopeFullScreen))
                ApplyScopeFullScreenPresenter(ViewModel.IsScopeFullScreen);
            else if (e.PropertyName == nameof(MainShellViewModel.CameraPreviewPath))
                _ = ShowCameraPreviewAsync(ViewModel.CameraPreviewPath);
        };
        ViewModel.PickPhotoFileAsync = PickPhotoFileAsync;
        ViewModel.PickManualFilesAsync = PickManualFilesAsync;
        ViewModel.PickManualFolderAsync = PickManualFolderAsync;
        ViewModel.ConfirmRemoveManualAsync = ConfirmRemoveManualAsync;
        ViewModel.ConfirmDeletePhotoAsync = ConfirmDeletePhotoAsync;
        HookAnnotationSurface();
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: false);

        Closed += OnMainWindowClosed;
    }

    /// <summary>Camera (7.1): loads the preview through a stream so the file is not held open (it may be moved on Save).</summary>
    private async Task ShowCameraPreviewAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            CameraPreviewImage.Source = null;
            return;
        }
        try
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await using (var fs = File.OpenRead(path))
                await bitmap.SetSourceAsync(fs.AsRandomAccessStream());
            if (ViewModel.CameraPreviewPath == path)
            {
                // The surface takes the photo's pixel size so marks (0–1 fractions) land on the right spot.
                if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
                {
                    CameraPhotoSurface.Width = bitmap.PixelWidth;
                    CameraPhotoSurface.Height = bitmap.PixelHeight;
                }
                CameraPreviewImage.Source = bitmap;
                RedrawAnnotations(lastOnly: false);
            }
        }
        catch
        {
            CameraPreviewImage.Source = null;
        }
    }

    /// <summary>Camera (7.1): file picker for Import (unpackaged apps must give the picker the window handle).</summary>
    private async Task<string?> PickPhotoFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
        };
        foreach (var ext in MainShellViewModel.ImportablePhotoExtensions)
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>Manual Search › Import PDFs…: one or more PDF files (opens in Documents).</summary>
    private async Task<IReadOnlyList<string>?> PickManualFilesAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        return files is null || files.Count == 0 ? null : files.Select(f => f.Path).ToList();
    }

    /// <summary>Manual Search › Import folder…: every PDF in a local folder (and its subfolders).</summary>
    private async Task<string?> PickManualFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async void OnRemoveManualClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ManualLibraryRowViewModel row)
            await ViewModel.RemoveManualAsync(row);
    }

    /// <summary>Manual library › Remove: confirm before the manual leaves search and Tech Chat.</summary>
    private async Task<bool> ConfirmRemoveManualAsync(ManualLibraryRowViewModel row)
    {
        var dialog = new ContentDialog
        {
            Title = "Remove this manual from the library?",
            Content = new TextBlock
            {
                Text = $"{row.Title}\n\nIts passages will no longer appear in Manual Search or in Tech Chat answers. "
                    + "The PDF copy on this tablet is kept, and you can import the PDF again to bring it back.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Keep it",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Camera: confirms deleting a saved photo from the work order (soft delete; the file is kept).</summary>
    private async Task<bool> ConfirmDeletePhotoAsync(WorkOrderPhotoRowViewModel row)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete this photo from the work order?",
            Content = new TextBlock
            {
                Text = $"{row.Caption}\n\nIt will no longer show on the Camera page or in Integrated Diagnostics, "
                    + "and its marks go with it. The photo file is kept on this tablet.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Keep it",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ApplyScopeFullScreenPresenter(bool fullScreen)
    {
        try
        {
            AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        }
        catch
        {
            // If the presenter cannot change, the chrome is still hidden; the toolbar button still exits.
        }
    }

    /// <summary>F11 toggles scope full screen; Esc leaves it (keys a text box or flyout handles first are ignored).</summary>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel.HandlePushToTalkKey(e.Key.ToString(), e.KeyStatus.WasKeyDown))
        {
            e.Handled = true; // 7.5c push-to-talk key (Settings > Voice)
        }
        else if (e.Key == VirtualKey.F11)
        {
            ViewModel.ToggleScopeFullScreen();
            e.Handled = ViewModel.IsScopeViewVisible || ViewModel.IsScopeFullScreen;
        }
        else if (e.Key == VirtualKey.Escape && ViewModel.ExitScopeFullScreen())
        {
            e.Handled = true;
        }
    }

    public MainShellViewModel ViewModel { get; }

    public Task InitializeShellAsync() => ViewModel.InitializeAsync();

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            await ViewModel.DisposeAsync();
        }
        catch
        {
            // Ignore shutdown disposal exceptions
        }
    }

    private void OnToolbarButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string actionKey })
        {
            ViewModel.SelectToolbarAction(actionKey);
        }
    }

    /// <summary>Menu items and in-page links (Tag = page key) open a module page through the same router as the module menu.</summary>
    private async void OnNavRailButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string navKey })
            return;

        try
        {
            var item = ViewModel.FindSidebarItem(navKey);
            if (item is not null)
                await ViewModel.SelectSidebarItemAsync(item);
            else
                await ViewModel.SelectNavRailItem(navKey);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Navigation / refresh failed", ex);
        }
    }

    /// <summary>RO Viewer / Integrated Diagnostics tabs on the work order document.</summary>
    private void OnWorkOrderDocumentTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is { Tag: string tag } && int.TryParse(tag, out var index))
            ViewModel.WorkOrderDocumentTabIndex = index;
    }

    private async void OnReloadDbMenuClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ReloadDatabaseRecordsAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Reload database failed", ex);
        }
    }

    private async void OnDiscoverDevicesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.DiscoverDevicesAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Discover devices failed", ex);
        }
    }

    private async void OnPlaybackSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSliderSeek || ViewModel.PlaybackState == PlaybackState.Live || ViewModel.PlaybackState == PlaybackState.Playing)
            return;

        long targetSeq = (long)Math.Round(e.NewValue);
        if (targetSeq == ViewModel.PlaybackSequence)
            return;

        try
        {
            _suppressSliderSeek = true;
            await ViewModel.SeekPlaybackToSequenceAsync(targetSeq, PlaybackState.Paused);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Playback seek failed", ex);
        }
        finally
        {
            _suppressSliderSeek = false;
        }
    }

    // =========================================================================
    // Trigger Setup Dialog (Matches Screenshot 18)
    // =========================================================================

    private async void OnOpenTriggerSetupClick(object sender, RoutedEventArgs e)
    {
        var kindCombo = new ComboBox
        {
            Header = "Trigger Mode (Manual / On-DTC / On-PID Threshold)",
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = ViewModel.TriggerKinds,
            SelectedItem = ViewModel.TriggerKind,
        };

        var dtcBox = new TextBox
        {
            Header = "Target DTC Code (OnDtc mode — leave blank for any DTC)",
            PlaceholderText = "e.g. SPN 94 FMI 3 or P0118",
            MinHeight = 44,
            Text = ViewModel.TriggerDtcCode,
        };

        var pidCombo = new ComboBox
        {
            Header = "Monitored Parameter (OnPidThreshold mode)",
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = ViewModel.AvailablePidNames,
            SelectedItem = ViewModel.TriggerPidName ?? ViewModel.AvailablePidNames.FirstOrDefault(),
        };

        var comparisonCombo = new ComboBox
        {
            Header = "Threshold Comparison",
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = ViewModel.ThresholdComparisons,
            SelectedItem = ViewModel.TriggerComparison,
        };

        var thresholdBox = new NumberBox
        {
            Header = "Threshold Value",
            MinHeight = 44,
            Value = ViewModel.TriggerThresholdValue,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };

        var unitBox = new TextBox
        {
            Header = "Threshold Unit (canonicalized via UnitConverter when known)",
            PlaceholderText = "e.g. °C, °F, psi, bar, kPa, V, rpm",
            MinHeight = 44,
            Text = ViewModel.TriggerThresholdUnit,
        };

        var preSlider = new Slider
        {
            Header = "Before Trigger Pre-Buffer Duration (seconds)",
            Minimum = 0,
            Maximum = 30,
            StepFrequency = 0.5,
            Value = ViewModel.PreTriggerSeconds,
        };

        var postSlider = new Slider
        {
            Header = "After Trigger Post-Capture Duration (seconds)",
            Minimum = 1,
            Maximum = 60,
            StepFrequency = 0.5,
            Value = ViewModel.PostTriggerSeconds,
        };

        var autoStopCheck = new CheckBox
        {
            Content = "Auto-stop recording when post-trigger duration elapses",
            MinHeight = 44,
            IsChecked = ViewModel.TriggerAutoStop,
        };

        var panel = new StackPanel
        {
            Spacing = 10,
            MinWidth = 460,
            Children =
            {
                kindCombo,
                dtcBox,
                pidCombo,
                comparisonCombo,
                thresholdBox,
                unitBox,
                preSlider,
                postSlider,
                autoStopCheck,
            },
        };

        var dialog = new ContentDialog
        {
            Title = "Trigger Setup — Live Data Recording (Screen 18)",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            PrimaryButtonText = "Accept",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (kindCombo.SelectedItem is TriggerKind selectedKind)
                ViewModel.TriggerKind = selectedKind;

            ViewModel.TriggerDtcCode = dtcBox.Text ?? "";
            ViewModel.TriggerPidName = pidCombo.SelectedItem as string;

            if (comparisonCombo.SelectedItem is ThresholdComparison selectedComp)
                ViewModel.TriggerComparison = selectedComp;

            if (!double.IsNaN(thresholdBox.Value))
                ViewModel.TriggerThresholdValue = thresholdBox.Value;

            ViewModel.TriggerThresholdUnit = string.IsNullOrWhiteSpace(unitBox.Text) ? "°C" : unitBox.Text.Trim();
            ViewModel.PreTriggerSeconds = preSlider.Value;
            ViewModel.PostTriggerSeconds = postSlider.Value;
            ViewModel.TriggerAutoStop = autoStopCheck.IsChecked ?? true;

            ViewModel.RecordingStatusLabel = $"Trigger Ready ({ViewModel.TriggerKind} · Pre {ViewModel.PreTriggerSeconds:F1}s / Post {ViewModel.PostTriggerSeconds:F1}s)";
            ViewModel.StatusBannerMessage = $"Updated recording trigger: {ViewModel.TriggerKind} (Pre {ViewModel.PreTriggerSeconds:F1}s, Post {ViewModel.PostTriggerSeconds:F1}s).";
        }
    }

    // =========================================================================
    // Scan Tool / J2534 & RP1210 Device Setup Dialog (Matches Screenshots 26 & 28)
    // =========================================================================

    private async void OnOpenDeviceSetupDialogClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.DiscoverDevicesAsync();

        var deviceListView = new ListView
        {
            ItemsSource = ViewModel.AvailableDevices,
            SelectedItem = ViewModel.SelectedDevice,
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 160,
            MaxHeight = 240,
            BorderThickness = new Thickness(1),
        };

        var protocolCombo = new ComboBox
        {
            Header = "Target Protocol (J1939, J1708/J1587, OBD II)",
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = ViewModel.SimulatorProtocols,
            SelectedItem = ViewModel.SelectedProtocol,
        };

        var detailsBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };

        void UpdateDetails()
        {
            if (deviceListView.SelectedItem is SelectableDeviceItem item)
            {
                detailsBlock.Text =
                    $"Device ID: {item.Id}\n" +
                    $"Device Class: {item.KindLabel} ({(item.IsInProcessSimulator ? "In-Process Simulator" : "32-Bit Host Process IPC")})\n" +
                    $"Provenance: {item.ProvenanceBadge}\n" +
                    $"Protocols Supported: {item.ProtocolsSummary}";
            }
        }

        deviceListView.SelectionChanged += (_, _) => UpdateDetails();
        UpdateDetails();

        var panel = new StackPanel
        {
            Spacing = 12,
            MinWidth = 500,
            Children =
            {
                new TextBlock
                {
                    Text = "Select the vehicle/equipment interface adapter to use with J2534, RP1210 Heavy-Duty, or Global OBD II functions:",
                    TextWrapping = TextWrapping.Wrap,
                },
                deviceListView,
                protocolCombo,
                detailsBlock,
            },
        };

        var dialog = new ContentDialog
        {
            Title = "Scan Tool & J2534 / RP1210 Device Setup (Screens 26 & 28)",
            Content = panel,
            PrimaryButtonText = "Connect Selected Device",
            SecondaryButtonText = "Select Only",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result is ContentDialogResult.Primary or ContentDialogResult.Secondary)
        {
            if (deviceListView.SelectedItem is SelectableDeviceItem chosen)
                ViewModel.SelectedDevice = chosen;
            if (protocolCombo.SelectedItem is ProtocolType proto)
                ViewModel.SelectedProtocol = proto;

            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    await ViewModel.ConnectSimulatorAsync();
                }
                catch (Exception ex)
                {
                    ViewModel.ReportError("Device connection failed", ex);
                }
            }
        }
    }

    private ElementTheme SelectedTheme => ThemeRadioButtons?.SelectedIndex switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>Settings → Theme. Picking a theme turns Sunlight mode off.</summary>
    private void OnThemeRadioSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RootGrid is null || SunlightToggleSwitch is null)
            return;

        if (SunlightToggleSwitch.IsOn)
        {
            SetSunlightMode(false);
            return;
        }

        RootGrid.RequestedTheme = SelectedTheme;
        SyncThemeStateToViewModel();
    }

    private void OnSunlightToggleSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSunlightToggle)
            return;

        SetSunlightMode(SunlightToggleSwitch.IsOn);
    }

    private void SetSunlightMode(bool enabled)
    {
        _suppressSunlightToggle = true;
        SunlightToggleSwitch.IsOn = enabled;
        _suppressSunlightToggle = false;

        if (enabled)
        {
            if (_sunlightDictionary is null)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Themes", "SunlightTheme.xaml");
                _sunlightDictionary = (ResourceDictionary)Microsoft.UI.Xaml.Markup.XamlReader.Load(File.ReadAllText(path));
            }

            if (!RootGrid.Resources.MergedDictionaries.Contains(_sunlightDictionary))
            {
                RootGrid.Resources.MergedDictionaries.Add(_sunlightDictionary);
            }

            // Force WinUI to re-evaluate {ThemeResource} bindings against the newly merged Light ThemeDictionary
            RootGrid.RequestedTheme = ElementTheme.Dark;
            RootGrid.RequestedTheme = ElementTheme.Light;
        }
        else
        {
            if (_sunlightDictionary is not null)
            {
                RootGrid.Resources.MergedDictionaries.Remove(_sunlightDictionary);
            }

            // Flip through the opposite theme so {ThemeResource} bindings re-evaluate, then land on the chosen theme
            var target = SelectedTheme;
            RootGrid.RequestedTheme = target == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            RootGrid.RequestedTheme = target;
        }

        SyncThemeStateToViewModel();
    }

    private void SyncThemeStateToViewModel()
    {
        var isSunlight = SunlightToggleSwitch?.IsOn ?? false;
        var isDark = !isSunlight && RootGrid.ActualTheme == ElementTheme.Dark;
        ViewModel.UpdateThemeState(isDark, isSunlight);
    }

    private async void OnAboutMenuClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "GSE Tech Pro — Offline Diagnostic Suite",
            Content = "Ground Support Equipment & Heavy-Duty Fleet Diagnostics.\n" +
                      "by Commercial Truck and Auto\n\n" +
                      "Phases 1–3: WinUI 3 (Windows App SDK), Local SQLite, DTC Library Enrichment, Multi-View Live Data, VCR Recording/Playback, 32-Bit Host IPC, Local AI Tech Chat, Guided Troubleshooting & Hybrid Manual RAG.",
            CloseButtonText = "Close",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    // =========================================================================
    // Phase 3: AI Tech Chat, Citations, Guided Troubleshooting & Manual RAG
    // =========================================================================

    private async void OnQuickPromptButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string prompt })
            return;

        try
        {
            await ViewModel.SendQuickPromptAsync(prompt);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("AI chat request failed", ex);
        }
    }

    private async void OnChatInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;
        try
        {
            await ViewModel.SendChatMessageAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("AI chat request failed", ex);
        }
    }

    private async void OnCitationButtonClick(object sender, RoutedEventArgs e)
    {
        var citation = (sender as FrameworkElement)?.Tag as CitationItemViewModel
                       ?? (sender as FrameworkElement)?.DataContext as CitationItemViewModel;
        if (citation is null)
            return;

        try
        {
            await ViewModel.OpenCitationAsync(citation);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Open citation failed", ex);
        }
    }

    private async void OnGuidedVerdictButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string verdictToken })
            return;

        try
        {
            await ViewModel.NextGuidedStepAsync(verdictToken);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Advance guided step failed", ex);
        }
    }

    private void OnSelectManualSearchResultClick(object sender, RoutedEventArgs e)
    {
        var hit = (sender as FrameworkElement)?.Tag as ManualSearchResultViewModel
                  ?? (sender as FrameworkElement)?.DataContext as ManualSearchResultViewModel;
        if (hit is not null)
        {
            ViewModel.SelectManualSearchResult(hit);
        }
    }

    private async void OnManualSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;
        try
        {
            await ViewModel.SearchManualsAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Manual search failed", ex);
        }
    }

    // =========================================================================
    // Phase 5: Repair Recommendations, Outcomes & Comeback Confirmation (ADR-010)
    // =========================================================================

    private async void OnRecommendationQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;
        try
        {
            await ViewModel.LoadRepairRecommendationsAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Repair history search failed", ex);
        }
    }

    private async void OnAcceptRecommendationButtonClick(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as RepairCandidateItemViewModel
                   ?? (sender as FrameworkElement)?.DataContext as RepairCandidateItemViewModel;
        if (item is null)
            return;

        try
        {
            await ViewModel.AcceptRecommendationAsync(item);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Accept recommendation failed", ex);
        }
    }

    // =========================================================================
    // Phase 6: Oscilloscope (PicoScope / Hantek / Simulator Workflow — ADR-011)
    // =========================================================================

    private async void OnLoadSavedWaveformButtonClick(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as SavedWaveformRowViewModel
                   ?? (sender as FrameworkElement)?.DataContext as SavedWaveformRowViewModel;
        if (item is null)
            return;

        try
        {
            await ViewModel.LoadSavedWaveformAsync(item);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError("Load saved waveform failed", ex);
        }
    }
}
