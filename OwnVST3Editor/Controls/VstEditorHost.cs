using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using OwnVST3Editor.Platform;
using OwnVST3Host;

namespace OwnVST3Editor.Controls
{
    /// <summary>
    /// A control that hosts a VST3 plugin editor within an Avalonia layout.
    /// Uses platform-specific native child window embedding.
    /// </summary>
    public class VstEditorHost : NativeControlHost
    {
        private OwnVst3Wrapper? _plugin;
        private bool _editorCreated;
        private IntPtr _embeddedHandle;
        private bool _isAttached;

        /// <summary>
        /// Defines the Plugin property
        /// </summary>
        public static readonly DirectProperty<VstEditorHost, OwnVst3Wrapper?> PluginProperty =
            AvaloniaProperty.RegisterDirect<VstEditorHost, OwnVst3Wrapper?>(
                nameof(Plugin),
                o => o.Plugin,
                (o, v) => o.Plugin = v);

        /// <summary>
        /// Gets or sets the VST3 plugin to display
        /// </summary>
        public OwnVst3Wrapper? Plugin
        {
            get => _plugin;
            set
            {
                if (_plugin != value)
                {
                    DetachEditor();
                    _plugin = value;

                    if (_plugin != null && _isAttached)
                    {
                        UpdateSize();
                        Dispatcher.UIThread.Post(AttachEditor, DispatcherPriority.Loaded);
                    }
                }
            }
        }

        /// <summary>
        /// Gets whether the editor is currently active
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
        /// Fired when an error occurs
        /// </summary>
        public event EventHandler<VstEditorErrorEventArgs>? EditorError;

        public VstEditorHost()
        {
            // Set default size
            Width = 800;
            Height = 600;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _isAttached = true;

            if (_plugin != null)
            {
                UpdateSize();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _isAttached = false;
            DetachEditor();
            base.OnDetachedFromVisualTree(e);
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            // Create the native control (child window)
            var handle = base.CreateNativeControlCore(parent);

            // Store the native control handle for editor attachment
            _embeddedHandle = handle?.Handle ?? parent.Handle;

            // Attach editor after native control is created
            Dispatcher.UIThread.Post(AttachEditor, DispatcherPriority.Loaded);

            return handle;
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            DetachEditor();
            base.DestroyNativeControlCore(control);
        }

        /// <summary>
        /// Attaches the VST3 editor to the native control
        /// </summary>
        public void AttachEditor()
        {
            if (_editorCreated || _plugin == null)
                return;

            try
            {
                if (!NativeWindowHandle.IsSupported)
                {
                    OnEditorError("Current platform is not supported");
                    return;
                }

                // Use the embedded native control handle if available
                IntPtr windowHandle = _embeddedHandle;

                // Fallback to top-level window handle if embedded handle not yet created
                if (windowHandle == IntPtr.Zero)
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel == null)
                    {
                        OnEditorError("Control is not attached to a window");
                        return;
                    }

                    windowHandle = NativeWindowHandle.GetHandle(topLevel);
                }

                if (windowHandle == IntPtr.Zero)
                {
                    OnEditorError("Failed to get native window handle");
                    return;
                }

                bool success = _plugin.CreateEditor(windowHandle);
                if (success)
                {
                    _editorCreated = true;
                    EditorAttached?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    OnEditorError("Failed to create VST3 editor");
                }
            }
            catch (Exception ex)
            {
                OnEditorError($"Error attaching editor: {ex.Message}");
            }
        }

        /// <summary>
        /// Detaches the VST3 editor
        /// </summary>
        public void DetachEditor()
        {
            if (!_editorCreated || _plugin == null)
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
        /// Updates control size based on plugin editor size
        /// </summary>
        public void UpdateSize()
        {
            if (_plugin != null && _plugin.GetEditorSize(out int width, out int height))
            {
                Width = width;
                Height = height;
                MinWidth = width;
                MinHeight = height;
            }
        }

        /// <summary>
        /// Resizes the editor to match control dimensions
        /// </summary>
        public void ResizeEditor()
        {
            if (_editorCreated && _plugin != null)
            {
                _plugin.ResizeEditor((int)Width, (int)Height);
            }
        }

        private void OnEditorError(string message)
        {
            EditorError?.Invoke(this, new VstEditorErrorEventArgs(message));
        }
    }
}
