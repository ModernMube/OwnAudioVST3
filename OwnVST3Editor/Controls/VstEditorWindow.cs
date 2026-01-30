using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OwnVST3Editor.Platform;
using OwnVST3Host;

namespace OwnVST3Editor.Controls
{
    /// <summary>
    /// A standalone window for displaying VST3 plugin editors.
    /// Supports Windows, Linux (X11), and macOS platforms.
    /// </summary>
    public class VstEditorWindow : Window
    {
        private readonly OwnVst3Wrapper _plugin;
        private readonly VstEditorHost _editorHost;
        private bool _isClosing;

        /// <summary>
        /// Gets whether the VST3 editor is currently active
        /// </summary>
        public bool IsEditorActive => _editorHost.IsEditorActive;

        /// <summary>
        /// Fired when the editor is successfully attached
        /// </summary>
        public event EventHandler? EditorAttached;

        /// <summary>
        /// Fired when the editor is detached
        /// </summary>
        public event EventHandler? EditorDetached;

        /// <summary>
        /// Fired when an error occurs during editor operations
        /// </summary>
        public event EventHandler<VstEditorErrorEventArgs>? EditorError;

        /// <summary>
        /// Creates a new VST3 editor window
        /// </summary>
        /// <param name="plugin">The VST3 plugin wrapper instance</param>
        public VstEditorWindow(OwnVst3Wrapper plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            // Create the editor host control
            _editorHost = new VstEditorHost
            {
                Plugin = _plugin
            };

            // Wire up events
            _editorHost.EditorAttached += (s, e) => EditorAttached?.Invoke(this, e);
            _editorHost.EditorDetached += (s, e) => EditorDetached?.Invoke(this, e);
            _editorHost.EditorError += (s, e) => EditorError?.Invoke(this, e);

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

            // Try to get editor size and set window dimensions
            if (_plugin.GetEditorSize(out int width, out int height))
            {
                Width = width;
                Height = height;
                MinWidth = width;
                MinHeight = height;

                // Update editor host size
                _editorHost.Width = width;
                _editorHost.Height = height;
            }
            else
            {
                // Default size if editor size not available
                Width = 800;
                Height = 600;
            }

            // Set title from plugin name
            try
            {
                string? pluginName = _plugin.Name;
                Title = string.IsNullOrEmpty(pluginName) ? "VST3 Editor" : $"{pluginName} - Editor";
            }
            catch
            {
                Title = "VST3 Editor";
            }

            // Set the editor host as window content
            Content = _editorHost;
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!_isClosing)
            {
                _isClosing = true;
                _editorHost.DetachEditor();
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _editorHost.DetachEditor();
            base.OnClosed(e);
        }

        /// <summary>
        /// Attaches the VST3 editor to this window
        /// </summary>
        public void AttachEditor()
        {
            _editorHost.AttachEditor();
        }

        /// <summary>
        /// Detaches the VST3 editor from this window
        /// </summary>
        public void DetachEditor()
        {
            _editorHost.DetachEditor();
        }

        /// <summary>
        /// Updates window size to match the VST3 editor's preferred size
        /// </summary>
        public void UpdateWindowSize()
        {
            _editorHost.UpdateSize();

            if (_plugin.GetEditorSize(out int width, out int height))
            {
                Width = width;
                Height = height;
                MinWidth = width;
                MinHeight = height;
            }
        }

        /// <summary>
        /// Resizes the VST3 editor to match window dimensions
        /// </summary>
        public void ResizeEditorToWindow()
        {
            _editorHost.ResizeEditor();
        }

        /// <summary>
        /// Static helper to show editor window for a plugin
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
