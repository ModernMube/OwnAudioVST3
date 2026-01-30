using Avalonia.Controls;
using OwnVST3Editor.Controls;
using OwnVST3Host;

namespace OwnVST3Editor.Extensions
{
    /// <summary>
    /// Extension methods for OwnVst3Wrapper to easily show VST3 editors
    /// </summary>
    public static class OwnVst3WrapperExtensions
    {
        /// <summary>
        /// Shows the VST3 editor in a standalone window
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <returns>The editor window</returns>
        public static VstEditorWindow ShowEditor(this OwnVst3Wrapper plugin)
        {
            VstEditorApp.InitializeAvalonia();
            var window = new VstEditorWindow(plugin);
            window.Show();
            return window;
        }

        /// <summary>
        /// Shows the VST3 editor in a standalone window with custom title
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <param name="title">Window title</param>
        /// <returns>The editor window</returns>
        public static VstEditorWindow ShowEditor(this OwnVst3Wrapper plugin, string title)
        {
            VstEditorApp.InitializeAvalonia();
            var window = new VstEditorWindow(plugin, title);
            window.Show();
            return window;
        }

        /// <summary>
        /// Shows the VST3 editor as a child of an owner window
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <param name="owner">Owner window</param>
        /// <returns>The editor window</returns>
        public static VstEditorWindow ShowEditor(this OwnVst3Wrapper plugin, Window owner)
        {
            VstEditorApp.InitializeAvalonia();
            var window = new VstEditorWindow(plugin);
            window.Show(owner);
            return window;
        }

        /// <summary>
        /// Shows the VST3 editor as a modal dialog
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <param name="owner">Owner window</param>
        /// <returns>Task that completes when dialog is closed</returns>
        public static async Task ShowEditorDialogAsync(this OwnVst3Wrapper plugin, Window owner)
        {
            VstEditorApp.InitializeAvalonia();
            var window = new VstEditorWindow(plugin);
            await window.ShowDialog(owner);
        }

        /// <summary>
        /// Creates a VstEditorHost control for embedding in layouts
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <returns>The editor host control</returns>
        public static VstEditorHost CreateEditorHost(this OwnVst3Wrapper plugin)
        {
            return new VstEditorHost { Plugin = plugin };
        }

        /// <summary>
        /// Gets the preferred editor size for the plugin
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <returns>Tuple of (width, height) or null if unavailable</returns>
        public static (int Width, int Height)? GetPreferredEditorSize(this OwnVst3Wrapper plugin)
        {
            if (plugin.GetEditorSize(out int width, out int height))
            {
                return (width, height);
            }
            return null;
        }

        /// <summary>
        /// Checks if the plugin has an editor view available
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <returns>True if editor is available</returns>
        public static bool HasEditor(this OwnVst3Wrapper plugin)
        {
            return plugin.GetEditorSize(out _, out _);
        }
    }
}
