using System;
using System.IO;
using Eto.Forms;
using Eto.Drawing;
using OwnVST3Host;

namespace OwnVST3EtoDemo
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            new Application(Eto.Platforms.Wpf).Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private OwnVst3Wrapper? _plugin;
        private VstEditorPanel? _editorPanel;
        private TextBox _pluginPathTextBox = null!;
        private Label _statusLabel = null!;
        private Button _loadButton = null!;
        private Button _openEditorButton = null!;
        private TextArea _infoTextArea = null!;

        public MainForm()
        {
            Title = "OwnVST3 Eto.Forms Demo";
            ClientSize = new Size(600, 400);

            InitializeUI();
        }

        private void InitializeUI()
        {
            // Plugin path input
            _pluginPathTextBox = new TextBox
            {
                PlaceholderText = "Enter VST3 plugin path or browse..."
            };

            var browseButton = new Button { Text = "Browse..." };
            browseButton.Click += BrowseButton_Click;

            // Load button
            _loadButton = new Button { Text = "Load Plugin" };
            _loadButton.Click += LoadButton_Click;

            // Open editor button
            _openEditorButton = new Button
            {
                Text = "Open Editor",
                Enabled = false
            };
            _openEditorButton.Click += OpenEditorButton_Click;

            // Status label
            _statusLabel = new Label
            {
                Text = "No plugin loaded",
                TextColor = Colors.Gray
            };

            // Plugin info text area
            _infoTextArea = new TextArea
            {
                ReadOnly = true,
                Height = 200
            };

            // Layout
            Content = new TableLayout
            {
                Padding = 10,
                Spacing = new Size(5, 5),
                Rows =
                {
                    new TableRow(
                        new Label { Text = "Plugin Path:", VerticalAlignment = VerticalAlignment.Center },
                        new TableCell(_pluginPathTextBox, true),
                        browseButton
                    ),
                    new TableRow(
                        new TableCell(new StackLayout
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 5,
                            Items = { _loadButton, _openEditorButton }
                        })
                    ),
                    new TableRow(_statusLabel),
                    new TableRow(
                        new Label { Text = "Plugin Info:" }
                    ),
                    new TableRow(
                        new TableCell(_infoTextArea, true)
                    )
                }
            };
        }

        private void BrowseButton_Click(object? sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Select VST3 Plugin",
                Filters =
                {
                    new FileFilter("VST3 Plugins", ".vst3")
                }
            };

            if (dialog.ShowDialog(this) == DialogResult.Ok)
            {
                _pluginPathTextBox.Text = dialog.FileName;
            }
        }

        private void LoadButton_Click(object? sender, EventArgs e)
        {
            var pluginPath = _pluginPathTextBox.Text;

            if (string.IsNullOrWhiteSpace(pluginPath))
            {
                MessageBox.Show(this, "Please enter a plugin path", "Error", MessageBoxType.Error);
                return;
            }

            if (!File.Exists(pluginPath) && !Directory.Exists(pluginPath))
            {
                MessageBox.Show(this, "Plugin file or directory not found", "Error", MessageBoxType.Error);
                return;
            }

            try
            {
                // Dispose previous plugin
                _plugin?.Dispose();

                // Load new plugin
                _plugin = new OwnVst3Wrapper();

                if (!_plugin.LoadPlugin(pluginPath))
                {
                    MessageBox.Show(this, "Failed to load plugin", "Error", MessageBoxType.Error);
                    _plugin?.Dispose();
                    _plugin = null;
                    return;
                }

                // Initialize plugin
                _plugin.Initialize(44100, 512);

                // Get plugin info
                var pluginName = _plugin.Name;
                var vendor = _plugin.Vendor;
                var version = _plugin.Version;
                var info = _plugin.PluginInfo;

                _infoTextArea.Text = info;

                // Check if plugin has editor
                bool hasEditor = _plugin.GetEditorSize(out _, out _);

                _openEditorButton.Enabled = hasEditor;
                _statusLabel.Text = hasEditor
                    ? $"Loaded: {pluginName}"
                    : $"Loaded: {pluginName} (no editor available)";
                _statusLabel.TextColor = Colors.Green;

                Console.WriteLine($"Plugin loaded: {pluginName}");
                Console.WriteLine($"Vendor: {vendor}");
                Console.WriteLine($"Version: {version}");
                Console.WriteLine($"Has Editor: {hasEditor}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error loading plugin: {ex.Message}", "Error", MessageBoxType.Error);
                _statusLabel.Text = "Error loading plugin";
                _statusLabel.TextColor = Colors.Red;
            }
        }

        private void OpenEditorButton_Click(object? sender, EventArgs e)
        {
            if (_plugin == null)
            {
                MessageBox.Show(this, "No plugin loaded", "Error", MessageBoxType.Error);
                return;
            }

            try
            {
                // Create editor window
                var editorWindow = new Form
                {
                    Title = $"{_plugin.Name} - Editor",
                    Resizable = false
                };

                // Create VST editor panel
                _editorPanel = new VstEditorPanel();

                _editorPanel.EditorAttached += (s, ev) =>
                {
                    Console.WriteLine("Editor attached successfully!");
                };

                _editorPanel.EditorError += (s, error) =>
                {
                    Application.Instance.Invoke(() =>
                    {
                        MessageBox.Show(this, $"Editor error: {error}", "Error", MessageBoxType.Error);
                        editorWindow.Close();
                    });
                };

                editorWindow.Content = _editorPanel;

                // Attach plugin to panel
                _editorPanel.AttachPlugin(_plugin);

                // Adjust window size to editor panel
                editorWindow.ClientSize = _editorPanel.Size;

                // Show editor window
                editorWindow.Show();

                editorWindow.Closed += (s, ev) =>
                {
                    _editorPanel?.DetachPlugin();
                    _editorPanel = null;
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error opening editor: {ex.Message}", "Error", MessageBoxType.Error);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _editorPanel?.DetachPlugin();
            _plugin?.Dispose();
            base.OnClosed(e);
        }
    }
}
