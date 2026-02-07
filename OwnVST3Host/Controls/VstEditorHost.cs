using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using OwnVST3Host.Platform;
using OwnVST3Host;

namespace OwnVST3Host.Controls
{
    /// <summary>
    /// A control that hosts a VST3 plugin editor within an Avalonia layout.
    /// Uses platform-specific native child window embedding.
    /// Includes idle timer for proper popup menu handling on all platforms.
    /// </summary>
    public class VstEditorHost : NativeControlHost
    {
        private OwnVst3Wrapper? _plugin;
        private bool _editorCreated;
        private IntPtr _embeddedHandle;
        private bool _isAttached;
        private DispatcherTimer? _idleTimer;
        private bool _windowsChildCreated;

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
            // Try to create the native control through base implementation
            var handle = base.CreateNativeControlCore(parent);

            // On Windows, if base doesn't create a child window, create one explicitly
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && handle == null)
            {
                try
                {
                    // Get initial size for child window
                    int width = (int)Width;
                    int height = (int)Height;
                    if (width <= 0) width = 800;
                    if (height <= 0) height = 600;

                    // Create Windows child window for VST editor embedding
                    IntPtr childHwnd = WindowsChildWindowHelper.CreateChildWindow(parent, width, height);
                    _embeddedHandle = childHwnd;
                    _windowsChildCreated = true;

                    // Create a platform handle wrapper for the child window
                    handle = new PlatformHandle(childHwnd, "HWND");
                }
                catch (Exception ex)
                {
                    OnEditorError($"Failed to create Windows child window: {ex.Message}");
                    _embeddedHandle = parent.Handle; // Fallback to parent
                    _windowsChildCreated = false;
                }
            }
            else
            {
                // Store the native control handle for editor attachment
                _embeddedHandle = handle?.Handle ?? parent.Handle;
                _windowsChildCreated = false;
            }

            // Attach editor after native control is created
            // On Windows, add a small delay to ensure the window is fully initialized
            // This prevents deadlocks and black screen issues
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    AttachEditor();
                };
                timer.Start();
            }
            else
            {
                Dispatcher.UIThread.Post(AttachEditor, DispatcherPriority.Loaded);
            }

            return handle!;
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            DetachEditor();

            // Clean up Windows child window if we created it
            if (_windowsChildCreated && _embeddedHandle != IntPtr.Zero)
            {
                try
                {
                    WindowsChildWindowHelper.DestroyChildWindow(_embeddedHandle);
                }
                catch
                {
                    // Ignore errors during cleanup
                }
                _windowsChildCreated = false;
                _embeddedHandle = IntPtr.Zero;
            }

            base.DestroyNativeControlCore(control);
        }

        /// <summary>
        /// Attaches the VST3 editor to the native control.
        /// This method ensures it runs on the UI thread.
        /// </summary>
        public void AttachEditor()
        {
            // Ensure we're on the UI thread
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(AttachEditor, DispatcherPriority.Loaded);
                return;
            }

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
                    StartIdleTimer();
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
        /// Detaches the VST3 editor.
        /// This method ensures it runs on the UI thread.
        /// </summary>
        public void DetachEditor()
        {
            // Ensure we're on the UI thread
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(DetachEditor);
                return;
            }

            if (!_editorCreated || _plugin == null)
                return;

            try
            {
                StopIdleTimer();
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
        /// Starts the idle timer for processing plugin UI events.
        /// This is essential for proper popup menu handling when
        /// running with a separate audio thread.
        /// </summary>
        private void StartIdleTimer()
        {
            if (_idleTimer != null) return;

            _idleTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60fps for smooth UI
            };
            _idleTimer.Tick += OnIdleTimerTick;
            _idleTimer.Start();
        }

        /// <summary>
        /// Stops the idle timer
        /// </summary>
        private void StopIdleTimer()
        {
            if (_idleTimer != null)
            {
                _idleTimer.Stop();
                _idleTimer.Tick -= OnIdleTimerTick;
                _idleTimer = null;
            }
        }

        /// <summary>
        /// Called periodically to process plugin idle events
        /// </summary>
        private void OnIdleTimerTick(object? sender, EventArgs e)
        {
            try
            {
                _plugin?.ProcessIdle();
            }
            catch
            {
                // Ignore exceptions in idle processing
            }
        }

        /// <summary>
        /// Updates control size based on plugin editor size.
        /// This method ensures it runs on the UI thread.
        /// </summary>
        public void UpdateSize()
        {
            // Ensure we're on the UI thread
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(UpdateSize);
                return;
            }

            if (_plugin != null && _plugin.GetEditorSize(out int width, out int height))
            {
                Width = width;
                Height = height;
                MinWidth = width;
                MinHeight = height;
            }
        }

        /// <summary>
        /// Resizes the editor to match control dimensions.
        /// This method ensures it runs on the UI thread.
        /// </summary>
        public void ResizeEditor()
        {
            // Ensure we're on the UI thread
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(ResizeEditor);
                return;
            }

            if (_editorCreated && _plugin != null)
            {
                // Resize the Windows child window if we created it
                if (_windowsChildCreated && _embeddedHandle != IntPtr.Zero)
                {
                    WindowsChildWindowHelper.ResizeChildWindow(_embeddedHandle, (int)Width, (int)Height);
                }

                _plugin.ResizeEditor((int)Width, (int)Height);
            }
        }

        private void OnEditorError(string message)
        {
            EditorError?.Invoke(this, new VstEditorErrorEventArgs(message));
        }
    }
}
