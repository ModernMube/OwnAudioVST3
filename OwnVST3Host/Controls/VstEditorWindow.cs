using Avalonia;
using Avalonia.Controls;
using OwnVST3Host;

namespace OwnVST3Host.Controls
{
    /// <summary>
    /// A standalone window for managing VST3 plugin editors.
    /// The editor itself runs in a native window managed by the native library.
    /// This window provides a control interface (button) to open/close the native editor.
    /// </summary>
    public class VstEditorWindow : Window
    {
        private readonly OwnVst3Wrapper _plugin;
        private readonly VstEditorHost _editorHost;
        private bool _isClosing;

        /// <summary>
        /// Gets whether the VST3 native editor is currently open
        /// </summary>
        public bool IsEditorActive => _plugin?.IsEditorOpen ?? false;

        /// <summary>
        /// Creates a new VST3 editor window
        /// </summary>
        /// <param name="plugin">The VST3 plugin wrapper instance</param>
        public VstEditorWindow(OwnVst3Wrapper plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            // Create the editor host control (provides button to open/close native editor)
            _editorHost = new VstEditorHost
            {
                Plugin = _plugin
            };

            InitializeWindow();
        }

        /// <summary>
        /// Creates a new VST3 editor window with a custom title
        /// </summary>
        /// <param name="plugin">The VST3 plugin wrapper instance</param>
        /// <param name="title">Window title</param>
        public VstEditorWindow(OwnVst3Wrapper plugin, string title) : this(plugin)
        {
            Title = title;
        }

        private void InitializeWindow()
        {
            // Set window properties
            CanResize = false;
            ShowInTaskbar = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SystemDecorations = SystemDecorations.Full;

            // Set a reasonable default size for the control window
            Width = 300;
            Height = 120;
            MinWidth = 200;
            MinHeight = 100;

            // Set title from plugin name
            try
            {
                string? pluginName = _plugin.Name;
                Title = string.IsNullOrEmpty(pluginName) ? "VST3 Editor Control" : $"{pluginName} - Editor Control";
            }
            catch
            {
                Title = "VST3 Editor Control";
            }

            // Set the editor host as window content
            Content = _editorHost;
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!_isClosing)
            {
                _isClosing = true;
                // Close the native editor when this control window closes
                try
                {
                    if (_plugin?.IsEditorOpen == true)
                    {
                        _plugin.CloseEditor();
                    }
                }
                catch
                {
                    // Ignore errors during close
                }
            }

            base.OnClosing(e);
        }

        /// <summary>
        /// Opens the native VST3 editor
        /// </summary>
        public void OpenEditor()
        {
            _plugin?.OpenEditor(Title);
        }

        /// <summary>
        /// Closes the native VST3 editor
        /// </summary>
        public void CloseEditor()
        {
            _plugin?.CloseEditor();
        }

        /// <summary>
        /// Static helper to show editor control window for a plugin
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <param name="owner">Optional owner window</param>
        /// <returns>The created editor window</returns>
        public static VstEditorWindow ShowEditor(OwnVst3Wrapper plugin, Window? owner = null)
        {
            var window = new VstEditorWindow(plugin);

            if (owner != null)
            {
                window.ShowDialog(owner);
            }
            else
            {
                window.Show();
            }

            return window;
        }

        /// <summary>
        /// Static helper to show editor window as dialog
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <param name="owner">Owner window</param>
        /// <returns>Task that completes when dialog is closed</returns>
        public static async Task<VstEditorWindow> ShowEditorDialogAsync(OwnVst3Wrapper plugin, Window owner)
        {
            var window = new VstEditorWindow(plugin);
            await window.ShowDialog(owner);
            return window;
        }
    }

    /// <summary>
    /// Event arguments for VST editor errors
    /// </summary>
    public class VstEditorErrorEventArgs : EventArgs
    {
        public string Message { get; }

        public VstEditorErrorEventArgs(string message)
        {
            Message = message;
        }
    }
}
