using System;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Interface for native window management for VST3 plugins.
    /// Platform-independent abstraction for Windows, macOS and Linux.
    /// </summary>
    public interface INativeWindow : IDisposable
    {
        /// <summary>
        /// Creates and displays the window.
        /// </summary>
        /// <param name="title">Window title</param>
        /// <param name="width">Window width in pixels</param>
        /// <param name="height">Window height in pixels</param>
        void Open(string title, int width, int height);

        /// <summary>
        /// Closes the window.
        /// </summary>
        void Close();

        /// <summary>
        /// Returns the native window handle.
        /// (HWND on Windows, NSView* on macOS, Window ID on Linux).
        /// </summary>
        /// <returns>Platform-specific window handle</returns>
        IntPtr GetHandle();

        /// <summary>
        /// Gets a value indicating whether the window is currently open.
        /// </summary>
        bool IsOpen { get; }

        /// <summary>
        /// Gets a value indicating whether the window is active (key/foreground window).
        /// Useful for detecting when VST dropdown menus are closed.
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// Event triggered when the window is resized.
        /// </summary>
        event Action<int, int>? OnResize;

        /// <summary>
        /// Event triggered when the window is closed.
        /// </summary>
        event Action? OnClosed;

        /// <summary>
        /// Synchronously executes an action on the window's thread.
        /// On Windows it marshals to the UI thread, on others executes directly.
        /// </summary>
        void Invoke(Action action);

        /// <summary>
        /// Asynchronously executes an action on the window's thread.
        /// Does not wait for the action to complete.
        /// </summary>
        void BeginInvoke(Action action);
    }
}
