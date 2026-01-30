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
        private bool _editorCreated;
        private bool _isClosing;

        /// <summary>
        /// Gets whether the VST3 editor is currently active
        /// </summary>
        public bool IsEditorActive => _editorCreated;

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
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            // Attach editor after window is fully opened
            Dispatcher.UIThread.Post(() => AttachEditor(), DispatcherPriority.Loaded);
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!_isClosing)
            {
                _isClosing = true;
                DetachEditor();
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            DetachEditor();
            base.OnClosed(e);
        }

        /// <summary>
        /// Attaches the VST3 editor to this window
        /// </summary>
        public void AttachEditor()
        {
            if (_editorCreated)
                return;

            try
            {
                if (!NativeWindowHandle.IsSupported)
                {
                    OnEditorError("Current platform is not supported for VST3 editor embedding");
                    return;
                }

                IntPtr windowHandle = NativeWindowHandle.GetHandle(this);
                if (windowHandle == IntPtr.Zero)
                {
                    OnEditorError("Failed to get native window handle");
                    return;
                }

                bool success = _plugin.CreateEditor(windowHandle);
                if (success)
                {
                    _editorCreated = true;

                    // Update window size to match editor
                    UpdateWindowSize();

                    EditorAttached?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    OnEditorError("Failed to create VST3 editor view");
                }
            }
            catch (Exception ex)
            {
                OnEditorError($"Error attaching editor: {ex.Message}");
            }
        }

        /// <summary>
        /// Detaches the VST3 editor from this window
        /// </summary>
        public void DetachEditor()
        {
            if (!_editorCreated)
                return;

            try
            {
                _plugin.CloseEditor();
                _editorCreated = false;
                EditorDetached?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                OnEditorError($"Error detaching editor: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates window size to match the VST3 editor's preferred size
        /// </summary>
        public void UpdateWindowSize()
        {
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
            if (_editorCreated)
            {
                _plugin.ResizeEditor((int)Width, (int)Height);
            }
        }

        private void OnEditorError(string message)
        {
            EditorError?.Invoke(this, new VstEditorErrorEventArgs(message));
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
