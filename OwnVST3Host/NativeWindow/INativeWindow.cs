using System;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Interfész natív ablakkezeléshez VST3 pluginok számára.
    /// Platform-független absztrakció Windows, macOS és Linux rendszerekhez.
    /// </summary>
    public interface INativeWindow : IDisposable
    {
        /// <summary>
        /// Ablak létrehozása és megjelenítése
        /// </summary>
        /// <param name="title">Ablak címe</param>
        /// <param name="width">Ablak szélessége pixelben</param>
        /// <param name="height">Ablak magassága pixelben</param>
        void Open(string title, int width, int height);

        /// <summary>
        /// Ablak bezárása
        /// </summary>
        void Close();

        /// <summary>
        /// Natív ablakkezelő visszaadása (HWND Windows-on, NSView* macOS-en, Window ID Linux-on)
        /// </summary>
        /// <returns>Platform-specifikus ablak handle</returns>
        IntPtr GetHandle();

        /// <summary>
        /// Ellenőrzi, hogy az ablak nyitva van-e
        /// </summary>
        bool IsOpen { get; }

        /// <summary>
        /// Esemény, amely akkor hívódik meg, amikor az ablak átméretezésre kerül
        /// </summary>
        event Action<int, int>? OnResize;

        /// <summary>
        /// Esemény, amely akkor hívódik meg, amikor az ablak bezárul
        /// </summary>
        event Action? OnClosed;
    }
}
