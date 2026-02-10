using System;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Controller for managing a VST plugin editor window.
    ///
    /// Threading model:
    /// - A natív ablak (HWND) dedikált szálon fut saját Win32 message loop-pal
    /// - A CreateEditor/CloseEditor hívások a HÍVÓ szálon futnak (ahol a plugin betöltődött)
    /// - Az attached() közben a HWND szál szabadon pumpálja az üzeneteket
    /// - Ez megakadályozza a cross-thread SendMessage deadlockot
    /// </summary>
    public class VstEditorController : IDisposable
    {
        private readonly OwnVst3Wrapper _vst3Wrapper;
        private INativeWindow? _nativeWindow;
        private System.Threading.Timer? _idleTimer;
        private bool _disposed = false;

        private const int IdleIntervalMs = 20; // 50Hz for GUI updates

        public VstEditorController(OwnVst3Wrapper vst3Wrapper)
        {
            _vst3Wrapper = vst3Wrapper ?? throw new ArgumentNullException(nameof(vst3Wrapper));
        }

        public bool IsOpen => _nativeWindow?.IsOpen ?? false;

        public bool IsEditorOpen => IsOpen;

        /// <summary>
        /// Opens the VST editor window.
        /// A HWND dedikált szálon jön létre (saját message loop-pal),
        /// de a CreateEditor a hívó szálon fut (ahol a controller létrejött).
        /// </summary>
        public void OpenEditor(string windowTitle)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VstEditorController));

            if (IsOpen) return;

            try
            {
                var editorSize = _vst3Wrapper.GetEditorSize() ?? new EditorSize(800, 600);

                _nativeWindow = NativeWindowFactory.Create();
                _nativeWindow.OnClosed += OnWindowClosed;
                _nativeWindow.OnResize += OnWindowResized;

                // Ablak létrehozása a dedikált szálon (Windows-on saját message loop-pal)
                _nativeWindow.Open(windowTitle, editorSize.Width, editorSize.Height);

                IntPtr windowHandle = _nativeWindow.GetHandle();

                // KRITIKUS: CreateEditor a HÍVÓ (Avalonia UI) szálon fut!
                // A controller és a view ezen a szálon jött létre.
                // A HWND viszont a dedikált window thread-en van, ami fut és pumpálja
                // az üzeneteket. Így az attached() közben:
                // - Ha a plugin SendMessage-t küld a HWND-nek → a window thread feldolgozza
                // - Ha a plugin COM hívást csinál → az Avalonia szálon fut (direkt hívás)
                // - Nincs deadlock!
                bool success = _vst3Wrapper.CreateEditor(windowHandle);

                if (!success)
                {
                    CloseEditor();
                    throw new InvalidOperationException("Nem sikerült létrehozni a VST editor-t!");
                }

                _idleTimer = new System.Threading.Timer(OnIdleTimer, null, IdleIntervalMs, IdleIntervalMs);
            }
            catch
            {
                CloseEditor();
                throw;
            }
        }

        public void CloseEditor()
        {
            try
            {
                if (_idleTimer != null)
                {
                    _idleTimer.Dispose();
                    _idleTimer = null;
                }

                if (_nativeWindow != null)
                {
                    var win = _nativeWindow;
                    _nativeWindow = null;

                    // VST editor bezárása a hívó szálon
                    try
                    {
                        _vst3Wrapper.CloseEditor();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[VST Editor] Error closing editor: {ex.Message}");
                    }

                    win.OnClosed -= OnWindowClosed;
                    win.OnResize -= OnWindowResized;
                    win.Close();
                    win.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Hiba a VST editor bezárásakor: {ex.Message}");
            }
        }

        private void OnWindowClosed()
        {
            // Az ablak bezárás gombjával (X) zárták be - window thread-ről hívódik
            try
            {
                _idleTimer?.Dispose();
                _idleTimer = null;

                _vst3Wrapper.CloseEditor();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VST Editor] Error in OnWindowClosed: {ex.Message}");
            }

            if (_nativeWindow != null)
            {
                _nativeWindow.OnClosed -= OnWindowClosed;
                _nativeWindow.OnResize -= OnWindowResized;
                _nativeWindow = null;
            }
        }

        private void OnIdleTimer(object? state)
        {
            if (_disposed || _nativeWindow == null) return;
            try
            {
                // ProcessIdle a window thread-en fut (üzenet pumpálás)
                _nativeWindow.BeginInvoke(() => _vst3Wrapper.ProcessIdle());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VST DEBUG] ERROR in OnIdleTimer: {ex.Message}");
            }
        }

        private void OnWindowResized(int width, int height)
        {
            try
            {
                _vst3Wrapper.ResizeEditor(width, height);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Hiba a VST editor átméretezésekor: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                CloseEditor();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        ~VstEditorController()
        {
            Dispose();
        }
    }
}
