using System;
using System.Runtime.InteropServices;
using Eto.Forms;
using Eto.Drawing;
using OwnVST3Host;

namespace OwnVST3EtoDemo
{
    /// <summary>
    /// VST3 Editor Panel using Eto.Forms with native control hosting
    /// </summary>
    public class VstEditorPanel : Panel
    {
        private OwnVst3Wrapper? _plugin;
        private bool _editorCreated;

        public event EventHandler? EditorAttached;
        public event EventHandler<string>? EditorError;

        public VstEditorPanel()
        {
            Size = new Size(800, 600);
            BackgroundColor = Colors.Black;
        }

        public void AttachPlugin(OwnVst3Wrapper plugin)
        {
            if (_editorCreated)
            {
                DetachPlugin();
            }

            _plugin = plugin;
            CreateEditor();
        }

        private void CreateEditor()
        {
            if (_plugin == null)
            {
                OnEditorError("Plugin is null");
                return;
            }

            try
            {
                // Get editor size first
                if (!_plugin.GetEditorSize(out int width, out int height))
                {
                    OnEditorError("Failed to get editor size");
                    return;
                }

                // Resize panel to match plugin editor size
                Size = new Size(width, height);

                // Get native window handle from Eto.Forms
                IntPtr windowHandle = GetNativeHandle();

                if (windowHandle == IntPtr.Zero)
                {
                    OnEditorError("Failed to get native window handle");
                    return;
                }

                Console.WriteLine($"Native handle obtained: 0x{windowHandle:X}");

                // Create VST3 editor and attach to this panel
                bool success = _plugin.CreateEditor(windowHandle);

                if (success)
                {
                    _editorCreated = true;
                    Console.WriteLine("VST3 editor attached successfully!");
                    EditorAttached?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    OnEditorError("Failed to create VST3 editor");
                }
            }
            catch (Exception ex)
            {
                OnEditorError($"Exception while creating editor: {ex.Message}");
            }
        }

        private IntPtr GetNativeHandle()
        {
            // Get the top-level window's native handle
            var window = ParentWindow;
            if (window != null)
            {
                // Use Eto.Forms ControlObject to get native handle
                var controlObject = window.ControlObject;

                // For WPF (Windows), get HWND
                if (controlObject is System.Windows.Window wpfWindow)
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(wpfWindow);
                    return helper.Handle;
                }
            }

            return IntPtr.Zero;
        }

        public void DetachPlugin()
        {
            if (_plugin != null && _editorCreated)
            {
                _plugin.CloseEditor();
                _editorCreated = false;
                _plugin = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DetachPlugin();
            }
            base.Dispose(disposing);
        }

        private void OnEditorError(string message)
        {
            Console.WriteLine($"Editor Error: {message}");
            EditorError?.Invoke(this, message);
        }
    }
}
