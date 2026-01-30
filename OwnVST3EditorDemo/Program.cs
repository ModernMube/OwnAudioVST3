using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using OwnVST3Editor;
using OwnVST3Editor.Controls;
using OwnVST3Editor.Extensions;
using OwnVST3Host;

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
    private OwnVst3Wrapper? _currentPlugin;
    private readonly ListBox _pluginList;
    private readonly TextBlock _statusText;
    private readonly Button _openEditorButton;
    private readonly StackPanel _pluginInfoPanel;
    private VstEditorWindow? _editorWindow;

    public MainWindow()
    {
        Title = "VST3 Editor Demo";
        Width = 600;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Create UI
        var mainPanel = new DockPanel
        {
            Margin = new Thickness(10)
        };

        // Top panel with title
        var titleBlock = new TextBlock
        {
            Text = "VST3 Plugin Browser",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(titleBlock, Dock.Top);
        mainPanel.Children.Add(titleBlock);

        // Status bar at bottom
        _statusText = new TextBlock
        {
            Text = "Ready",
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(_statusText, Dock.Bottom);
        mainPanel.Children.Add(_statusText);

        // Right panel with plugin info and button
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

        var refreshButton = new Button
        {
            Content = "Refresh Plugin List",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        refreshButton.Click += (s, e) => RefreshPluginList();
        _pluginInfoPanel.Children.Add(refreshButton);

        // Plugin info display
        _pluginInfoPanel.Children.Add(new TextBlock
        {
            Text = "Plugin Info:",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 10, 0, 5)
        });

        DockPanel.SetDock(_pluginInfoPanel, Dock.Right);
        mainPanel.Children.Add(_pluginInfoPanel);

        // Plugin list
        _pluginList = new ListBox
        {
            SelectionMode = SelectionMode.Single
        };
        _pluginList.SelectionChanged += OnPluginSelected;
        mainPanel.Children.Add(_pluginList);

        Content = mainPanel;

        // Load plugins on startup
        Opened += (s, e) => RefreshPluginList();
    }

    private void RefreshPluginList()
    {
        _statusText.Text = "Scanning for VST3 plugins...";
        _pluginList.ItemsSource = null;

        try
        {
            // Only use x64 plugin directory on 64-bit systems to avoid architecture mismatch
            var directories = new List<string>();
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string vst3Path = Path.Combine(programFiles, "Common Files", "VST3");
            if (Directory.Exists(vst3Path))
            {
                directories.Add(vst3Path);
            }

            var plugins = OwnVst3Wrapper.FindVst3Plugins(directories.ToArray());
            _pluginList.ItemsSource = plugins.Select(p => new PluginItem
            {
                Path = p,
                Name = Path.GetFileNameWithoutExtension(p)
            }).ToList();

            _statusText.Text = $"Found {plugins.Count} VST3 plugin(s) in x64 directory";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Error: {ex.Message}";
        }
    }

    private void OnPluginSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_pluginList.SelectedItem is not PluginItem item)
        {
            _openEditorButton.IsEnabled = false;
            return;
        }

        try
        {
            // Dispose previous plugin
            _currentPlugin?.Dispose();
            _currentPlugin = null;

            _statusText.Text = $"Loading {item.Name}...";

            // Load new plugin
            _currentPlugin = new OwnVst3Wrapper();
            if (_currentPlugin.LoadPlugin(item.Path))
            {
                // Check if plugin was actually loaded (name should not be empty)
                string? pluginName = _currentPlugin.Name;
                if (string.IsNullOrEmpty(pluginName))
                {
                    _statusText.Text = "Plugin load failed - architecture mismatch? (x86 vs x64)";
                    _openEditorButton.IsEnabled = false;
                    return;
                }

                _currentPlugin.Initialize(44100, 512);

                // Update info panel
                UpdatePluginInfo();

                bool hasEditor = _currentPlugin.HasEditor();
                _openEditorButton.IsEnabled = hasEditor;
                _statusText.Text = hasEditor
                    ? $"Loaded: {pluginName}"
                    : $"Loaded: {pluginName} (no editor available)";
            }
            else
            {
                _statusText.Text = "Failed to load plugin";
                _openEditorButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Error: {ex.Message}";
            _openEditorButton.IsEnabled = false;
        }
    }

    private void UpdatePluginInfo()
    {
        if (_currentPlugin == null)
            return;

        // Remove old info (keep buttons)
        while (_pluginInfoPanel.Children.Count > 4)
        {
            _pluginInfoPanel.Children.RemoveAt(4);
        }

        // Add plugin info
        AddInfoLine("Name", _currentPlugin.Name);
        AddInfoLine("Vendor", _currentPlugin.Vendor);
        AddInfoLine("Version", _currentPlugin.Version);
        AddInfoLine("Type", _currentPlugin.IsInstrument ? "Instrument" : (_currentPlugin.IsEffect ? "Effect" : "Unknown"));
        AddInfoLine("Parameters", _currentPlugin.GetParameterCount().ToString());

        var size = _currentPlugin.GetPreferredEditorSize();
        if (size.HasValue)
        {
            AddInfoLine("Editor Size", $"{size.Value.Width} x {size.Value.Height}");
        }
    }

    private void AddInfoLine(string label, string? value)
    {
        _pluginInfoPanel.Children.Add(new TextBlock
        {
            Text = $"{label}: {value ?? "N/A"}",
            TextWrapping = TextWrapping.Wrap
        });
    }

    private void OnOpenEditorClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_currentPlugin == null)
            return;

        try
        {
            // Close existing editor if open
            _editorWindow?.Close();

            // Open new editor
            _editorWindow = _currentPlugin.ShowEditor(this);
            _editorWindow.EditorError += (s, args) =>
            {
                _statusText.Text = $"Editor error: {args.Message}";
            };
            _editorWindow.Closed += (s, args) =>
            {
                _editorWindow = null;
                _statusText.Text = "Editor closed";
            };

            _statusText.Text = "Editor opened";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Error opening editor: {ex.Message}";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _editorWindow?.Close();
        _currentPlugin?.Dispose();
        base.OnClosed(e);
    }
}

public class PluginItem
{
    public required string Path { get; set; }
    public required string Name { get; set; }

    public override string ToString() => Name;
}
