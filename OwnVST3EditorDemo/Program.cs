using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using OwnVST3Host;
using OwnVST3Host.NativeWindow;

namespace OwnVST3EditorDemo;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}

public class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public class MainWindow : Window
{
    // ThreadedVst3Wrapper runs all plugin operations on its own dedicated thread,
    // keeping the Avalonia UI thread completely free.
    private ThreadedVst3Wrapper? _plugin;
    private readonly ListBox _pluginList;
    private readonly TextBlock _statusText;
    private readonly Button _openEditorButton;
    private readonly Button _playButton;
    private readonly Button _stopButton;
    private readonly StackPanel _pluginInfoPanel;
    private VstEditorController? _editorController;
    private WhiteNoiseProcessor? _noiseProcessor;

    public MainWindow()
    {
        Title = "VST3 Editor Demo";
        Width = 600;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var mainPanel = new DockPanel { Margin = new Thickness(10) };

        var titleBlock = new TextBlock
        {
            Text = "VST3 Plugin Browser",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(titleBlock, Dock.Top);
        mainPanel.Children.Add(titleBlock);

        _statusText = new TextBlock { Text = "Ready", Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_statusText, Dock.Bottom);
        mainPanel.Children.Add(_statusText);

        _pluginInfoPanel = new StackPanel
        {
            Width = 250,
            Margin = new Thickness(10, 0, 0, 0),
            Spacing = 10
        };

        _openEditorButton = new Button
        {
            Content = "Open Editor",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false
        };
        _openEditorButton.Click += OnOpenEditorClick;
        _pluginInfoPanel.Children.Add(_openEditorButton);

        _playButton = new Button
        {
            Content = "▶ Play White Noise (60s)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false,
            Background = new SolidColorBrush(Color.FromRgb(0, 120, 215))
        };
        _playButton.Click += OnPlayClick;
        _pluginInfoPanel.Children.Add(_playButton);

        _stopButton = new Button
        {
            Content = "⏹ Stop",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false,
            Background = new SolidColorBrush(Color.FromRgb(200, 50, 50))
        };
        _stopButton.Click += OnStopClick;
        _pluginInfoPanel.Children.Add(_stopButton);

        var refreshButton = new Button
        {
            Content = "Refresh Plugin List",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        refreshButton.Click += (_, _) => RefreshPluginList();
        _pluginInfoPanel.Children.Add(refreshButton);

        _pluginInfoPanel.Children.Add(new TextBlock
        {
            Text = "Plugin Info:",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 10, 0, 5)
        });

        DockPanel.SetDock(_pluginInfoPanel, Dock.Right);
        mainPanel.Children.Add(_pluginInfoPanel);

        _pluginList = new ListBox { SelectionMode = SelectionMode.Single };
        _pluginList.SelectionChanged += OnPluginSelected;
        mainPanel.Children.Add(_pluginList);

        Content = mainPanel;
        Opened += (_, _) => RefreshPluginList();
    }

    private void RefreshPluginList()
    {
        _statusText.Text = "Scanning for VST3 plugins…";
        _pluginList.ItemsSource = null;

        try
        {
            var plugins = OwnVst3Wrapper.FindVst3Plugins();
            _pluginList.ItemsSource = plugins
                .Select(p => new PluginItem { Path = p, Name = Path.GetFileNameWithoutExtension(p) })
                .ToList();

            _statusText.Text = $"Found {plugins.Count} VST3 plugin(s)";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Error: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------------
    // Plugin selection – async so the UI thread is never blocked.
    // LoadPlugin and Initialize run on the dedicated VST plugin thread.
    // -------------------------------------------------------------------------

    private async void OnPluginSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_pluginList.SelectedItem is not PluginItem item)
        {
            _openEditorButton.IsEnabled = false;
            return;
        }

        // Clean up the previous plugin (this disposes the inner OwnVst3Wrapper too).
        CleanupCurrentPlugin();

        _statusText.Text = $"Loading {item.Name}…";
        _openEditorButton.IsEnabled = false;
        _playButton.IsEnabled = false;

        try
        {
            // Create the wrapper – this also starts the dedicated plugin thread.
            _plugin = new ThreadedVst3Wrapper();

            // LoadPlugin runs on the plugin thread; the UI thread is free during the await.
            bool loaded = await _plugin.LoadPluginAsync(item.Path);
            if (!loaded)
            {
                _statusText.Text = "Failed to load plugin.";
                return;
            }

            // Quick sanity-check: if the name is empty the library probably loaded a wrong arch.
            string name = await _plugin.GetNameAsync();
            if (string.IsNullOrEmpty(name))
            {
                _statusText.Text = "Plugin load failed – architecture mismatch? (x86 vs x64)";
                return;
            }

            // Initialize audio engine on the plugin thread.
            await _plugin.InitializeAsync(44100, 512);

            // Fetch info for the UI panel (all on the plugin thread, awaited).
            string vendor = await _plugin.GetVendorAsync();
            string? version = await _plugin.GetVersionAsync();
            bool isInstrument = await _plugin.GetIsInstrumentAsync();
            bool isEffect = await _plugin.GetIsEffectAsync();
            int paramCount = await _plugin.GetParameterCountAsync();
            EditorSize? editorSize = await _plugin.GetEditorSizeAsync();

            // Back on the UI thread: update controls.
            UpdatePluginInfo(name, vendor, version, isInstrument, isEffect, paramCount, editorSize);

            bool hasEditor = editorSize.HasValue;
            _openEditorButton.IsEnabled = hasEditor;
            _playButton.IsEnabled = true;
            _statusText.Text = hasEditor
                ? $"Loaded: {name}"
                : $"Loaded: {name} (no editor available)";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Error: {ex.Message}";
            _openEditorButton.IsEnabled = false;
            _playButton.IsEnabled = false;
        }
    }

    // -------------------------------------------------------------------------
    // Playback (audio runs on a dedicated audio thread inside WhiteNoiseProcessor)
    // -------------------------------------------------------------------------

    private void OnPlayClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_plugin == null) return;

        try
        {
            _noiseProcessor?.Dispose();
            _noiseProcessor = new WhiteNoiseProcessor(_plugin);
            _noiseProcessor.Start();

            _playButton.IsEnabled = false;
            _stopButton.IsEnabled = true;
            _statusText.Text = "Processing white noise through VST plugin…";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Error starting playback: {ex.Message}";
        }
    }

    private void OnStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            _noiseProcessor?.Stop();
            _noiseProcessor?.Dispose();
            _noiseProcessor = null;

            _playButton.IsEnabled = _plugin != null;
            _stopButton.IsEnabled = false;
            _statusText.Text = "Processing stopped";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Error stopping playback: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------------
    // Editor (CreateEditor/CloseEditor stay on the UI thread per VST3 + macOS requirement)
    // -------------------------------------------------------------------------

    private async void OnOpenEditorClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_plugin == null) return;

        try
        {
            if (_editorController?.IsEditorOpen == true)
            {
                _editorController.CloseEditor();
                _openEditorButton.Content = "Open Editor";
                _statusText.Text = "Editor closed";
                return;
            }

            if (_editorController == null)
                _editorController = new VstEditorController(_plugin);

            string pluginName = await _plugin.GetNameAsync();

            // OpenEditorAsync fetches the editor size on the plugin thread (non-blocking),
            // then opens the window and calls CreateEditor on the UI thread.
            await _editorController.OpenEditorAsync(pluginName);

            _openEditorButton.Content = "Close Editor";
            _statusText.Text = "Editor opened in native window";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Error opening editor: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void UpdatePluginInfo(string name, string vendor, string? version,
        bool isInstrument, bool isEffect, int paramCount, EditorSize? editorSize)
    {
        // Keep the fixed buttons (indices 0-3) and replace everything after them.
        while (_pluginInfoPanel.Children.Count > 4)
            _pluginInfoPanel.Children.RemoveAt(4);

        AddInfoLine("Name", name);
        AddInfoLine("Vendor", vendor);
        AddInfoLine("Version", version);
        AddInfoLine("Type", isInstrument ? "Instrument" : (isEffect ? "Effect" : "Unknown"));
        AddInfoLine("Parameters", paramCount.ToString());

        if (editorSize.HasValue)
            AddInfoLine("Editor Size", $"{editorSize.Value.Width} x {editorSize.Value.Height}");
    }

    private void AddInfoLine(string label, string? value)
    {
        _pluginInfoPanel.Children.Add(new TextBlock
        {
            Text = $"{label}: {value ?? "N/A"}",
            TextWrapping = TextWrapping.Wrap
        });
    }

    private void CleanupCurrentPlugin()
    {
        _noiseProcessor?.Stop();
        _noiseProcessor?.Dispose();
        _noiseProcessor = null;

        _editorController?.CloseEditor();
        _editorController?.Dispose();
        _editorController = null;

        _plugin?.Dispose();
        _plugin = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        CleanupCurrentPlugin();
        base.OnClosed(e);
    }
}

public class PluginItem
{
    public required string Path { get; set; }
    public required string Name { get; set; }
    public override string ToString() => Name;
}
