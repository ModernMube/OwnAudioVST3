using System;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// VST Editor Controller - Kezeli a VST plugin editor ablakát natív módon
    /// </summary>
    public class VstEditorController : IDisposable
    {
        private readonly OwnVst3Wrapper _vst3Wrapper;
        private INativeWindow? _nativeWindow;
        private bool _disposed = false;

        /// <summary>
        /// Ellenőrzi, hogy az editor ablak nyitva van-e
        /// </summary>
        public bool IsEditorOpen => _nativeWindow?.IsOpen ?? false;

        /// <summary>
        /// Konstruktor
        /// </summary>
        /// <param name="vst3Wrapper">A VST3 wrapper példány, amelyhez az editor tartozik</param>
        public VstEditorController(OwnVst3Wrapper vst3Wrapper)
        {
            _vst3Wrapper = vst3Wrapper ?? throw new ArgumentNullException(nameof(vst3Wrapper));
        }

        /// <summary>
        /// Megnyitja a VST plugin editor ablakát
        /// </summary>
        /// <param name="title">Az ablak címe (opcionális, alapértelmezett: plugin neve)</param>
        public void OpenEditor(string? title = null)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VstEditorController));
            }

            if (IsEditorOpen)
            {
                throw new InvalidOperationException("Az editor ablak már nyitva van!");
            }

            // Plugin méret lekérése
            var editorSize = _vst3Wrapper.GetEditorSize();
            if (editorSize == null)
            {
                throw new InvalidOperationException("A plugin nem támogatja az editor funkciót vagy nem sikerült lekérni a méretet!");
            }

            // Ablak cím meghatározása
            string windowTitle = title ?? _vst3Wrapper.Name ?? "VST3 Plugin Editor";

            try
            {
                // Natív ablak létrehozása
                _nativeWindow = NativeWindowFactory.Create();

                // Eseménykezelők csatolása
                _nativeWindow.OnClosed += OnWindowClosed;
                _nativeWindow.OnResize += OnWindowResized;

                // Ablak megnyitása
                _nativeWindow.Open(windowTitle, editorSize.Value.Width, editorSize.Value.Height);

                // VST editor csatolása az ablakhoz
                IntPtr windowHandle = _nativeWindow.GetHandle();
                bool success = _vst3Wrapper.CreateEditor(windowHandle);

                if (!success)
                {
                    // Ha nem sikerült, bezárjuk az ablakot
                    _nativeWindow.Close();
                    _nativeWindow.Dispose();
                    _nativeWindow = null;
                    throw new InvalidOperationException("Nem sikerült létrehozni a VST editor-t!");
                }
            }
            catch
            {
                // Hiba esetén tisztítás
                if (_nativeWindow != null)
                {
                    _nativeWindow.OnClosed -= OnWindowClosed;
                    _nativeWindow.OnResize -= OnWindowResized;
                    _nativeWindow.Dispose();
                    _nativeWindow = null;
                }
                throw;
            }
        }

        /// <summary>
        /// Bezárja a VST plugin editor ablakát
        /// </summary>
        public void CloseEditor()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VstEditorController));
            }

            if (!IsEditorOpen)
            {
                return; // Nincs mit bezárni
            }

            try
            {
                // Először a VST editor-t zárjuk be
                _vst3Wrapper.CloseEditor();
            }
            catch (Exception ex)
            {
                // Logolhatjuk a hibát, de folytatjuk az ablak bezárását
                Console.WriteLine($"Hiba a VST editor bezárásakor: {ex.Message}");
            }

            // Aztán az ablakot
            if (_nativeWindow != null)
            {
                _nativeWindow.OnClosed -= OnWindowClosed;
                _nativeWindow.OnResize -= OnWindowResized;
                _nativeWindow.Close();
                _nativeWindow.Dispose();
                _nativeWindow = null;
            }
        }

        /// <summary>
        /// Eseménykezelő az ablak bezárásakor
        /// </summary>
        private void OnWindowClosed()
        {
            // Ha a felhasználó bezárja az ablakot, tisztítjuk a VST editor-t is
            try
            {
                _vst3Wrapper.CloseEditor();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Hiba a VST editor bezárásakor: {ex.Message}");
            }

            if (_nativeWindow != null)
            {
                _nativeWindow.OnClosed -= OnWindowClosed;
                _nativeWindow.OnResize -= OnWindowResized;
                _nativeWindow.Dispose();
                _nativeWindow = null;
            }
        }

        /// <summary>
        /// Eseménykezelő az ablak átméretezésekor
        /// </summary>
        private void OnWindowResized(int width, int height)
        {
            try
            {
                // Értesítjük a VST plugin-t az új méretről
                _vst3Wrapper.ResizeEditor(width, height);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Hiba a VST editor átméretezésekor: {ex.Message}");
            }
        }

        /// <summary>
        /// Dispose pattern implementálása
        /// </summary>
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
