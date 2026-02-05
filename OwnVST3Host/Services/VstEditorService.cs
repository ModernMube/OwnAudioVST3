using Avalonia.Controls;
using Avalonia.Threading;
using OwnVST3Host.Controls;
using OwnVST3Host;

namespace OwnVST3Host.Services
{
    /// <summary>
    /// Service for managing VST3 editor windows
    /// </summary>
    public class VstEditorService : IDisposable
    {
        private readonly Dictionary<OwnVst3Wrapper, VstEditorWindow> _openEditors = new();
        private readonly object _lock = new();
        private bool _disposed;

        /// <summary>
        /// Gets the number of currently open editor windows
        /// </summary>
        public int OpenEditorCount
        {
            get
            {
                lock (_lock)
                {
                    return _openEditors.Count;
                }
            }
        }

        /// <summary>
        /// Opens an editor window for the specified plugin.
        /// If an editor is already open for this plugin, it will be focused.
        /// This method ensures it runs on the UI thread.
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <param name="owner">Optional owner window</param>
        /// <returns>The editor window, or null if called from non-UI thread (window will be created asynchronously)</returns>
        public VstEditorWindow? OpenEditor(OwnVst3Wrapper plugin, Window? owner = null)
        {
            if (plugin == null)
                throw new ArgumentNullException(nameof(plugin));

            // Ensure we're on the UI thread for window creation
            if (!Dispatcher.UIThread.CheckAccess())
            {
                VstEditorWindow? result = null;
                Dispatcher.UIThread.Post(() => result = OpenEditorInternal(plugin, owner));
                return result;
            }

            return OpenEditorInternal(plugin, owner);
        }

        private VstEditorWindow OpenEditorInternal(OwnVst3Wrapper plugin, Window? owner)
        {
            lock (_lock)
            {
                // Check if editor already exists for this plugin
                if (_openEditors.TryGetValue(plugin, out var existingWindow))
                {
                    existingWindow.Activate();
                    existingWindow.Focus();
                    return existingWindow;
                }

                // Create new editor window
                var window = new VstEditorWindow(plugin);

                window.Closed += (s, e) =>
                {
                    lock (_lock)
                    {
                        _openEditors.Remove(plugin);
                    }
                };

                _openEditors[plugin] = window;

                if (owner != null)
                {
                    window.Show(owner);
                }
                else
                {
                    window.Show();
                }

                return window;
            }
        }

        /// <summary>
        /// Opens an editor window for the specified plugin asynchronously.
        /// This method can be safely called from any thread.
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <param name="owner">Optional owner window</param>
        /// <returns>The editor window</returns>
        public async Task<VstEditorWindow> OpenEditorAsync(OwnVst3Wrapper plugin, Window? owner = null)
        {
            if (plugin == null)
                throw new ArgumentNullException(nameof(plugin));

            // If already on UI thread, execute directly
            if (Dispatcher.UIThread.CheckAccess())
            {
                return OpenEditorInternal(plugin, owner);
            }

            // Otherwise, invoke on UI thread and wait for result
            return await Dispatcher.UIThread.InvokeAsync(() => OpenEditorInternal(plugin, owner));
        }

        /// <summary>
        /// Opens an editor window as a dialog.
        /// This method ensures it runs on the UI thread.
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <param name="owner">Owner window (required for dialog)</param>
        /// <returns>Task that completes when dialog closes</returns>
        public async Task<VstEditorWindow> OpenEditorDialogAsync(OwnVst3Wrapper plugin, Window owner)
        {
            if (plugin == null)
                throw new ArgumentNullException(nameof(plugin));
            if (owner == null)
                throw new ArgumentNullException(nameof(owner));

            // Ensure we're on the UI thread
            if (!Dispatcher.UIThread.CheckAccess())
            {
                return await Dispatcher.UIThread.InvokeAsync(async () => await OpenEditorDialogInternalAsync(plugin, owner));
            }

            return await OpenEditorDialogInternalAsync(plugin, owner);
        }

        private async Task<VstEditorWindow> OpenEditorDialogInternalAsync(OwnVst3Wrapper plugin, Window owner)
        {
            var window = new VstEditorWindow(plugin);

            lock (_lock)
            {
                _openEditors[plugin] = window;
            }

            window.Closed += (s, e) =>
            {
                lock (_lock)
                {
                    _openEditors.Remove(plugin);
                }
            };

            await window.ShowDialog(owner);
            return window;
        }

        /// <summary>
        /// Closes the editor window for the specified plugin
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        public void CloseEditor(OwnVst3Wrapper plugin)
        {
            VstEditorWindow? window;
            lock (_lock)
            {
                if (!_openEditors.TryGetValue(plugin, out window))
                    return;
            }

            Dispatcher.UIThread.Post(() => window.Close());
        }

        /// <summary>
        /// Checks if an editor window is open for the specified plugin
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <returns>True if editor is open</returns>
        public bool IsEditorOpen(OwnVst3Wrapper plugin)
        {
            lock (_lock)
            {
                return _openEditors.ContainsKey(plugin);
            }
        }

        /// <summary>
        /// Gets the editor window for the specified plugin if open
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <returns>The editor window or null</returns>
        public VstEditorWindow? GetEditorWindow(OwnVst3Wrapper plugin)
        {
            lock (_lock)
            {
                _openEditors.TryGetValue(plugin, out var window);
                return window;
            }
        }

        /// <summary>
        /// Closes all open editor windows
        /// </summary>
        public void CloseAllEditors()
        {
            List<VstEditorWindow> windows;
            lock (_lock)
            {
                windows = _openEditors.Values.ToList();
            }

            foreach (var window in windows)
            {
                Dispatcher.UIThread.Post(() => window.Close());
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            CloseAllEditors();
            _disposed = true;
        }
    }
}
