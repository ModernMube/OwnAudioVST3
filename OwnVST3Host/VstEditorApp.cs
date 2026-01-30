using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using OwnVST3Host.Controls;
using OwnVST3Host;

namespace OwnVST3Host
{
    /// <summary>
    /// Avalonia application for VST3 editor windows
    /// </summary>
    public class VstEditorApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>
        /// Builds and returns a configured Avalonia AppBuilder
        /// </summary>
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<VstEditorApp>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
        }

        /// <summary>
        /// Initializes Avalonia for use with VST3 editors.
        /// Call this once at application startup.
        /// </summary>
        public static void InitializeAvalonia()
        {
            if (Application.Current != null)
                return;

            BuildAvaloniaApp()
                .SetupWithoutStarting();
        }

        /// <summary>
        /// Starts the Avalonia application with a VST3 editor as main window
        /// </summary>
        /// <param name="plugin">The VST3 plugin to show editor for</param>
        /// <param name="args">Command line arguments</param>
        public static void RunWithEditor(OwnVst3Wrapper plugin, string[] args)
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args, lifetime =>
                {
                    lifetime.MainWindow = new VstEditorWindow(plugin);
                });
        }

        /// <summary>
        /// Starts the Avalonia application with a custom main window
        /// </summary>
        /// <param name="mainWindow">The main window</param>
        /// <param name="args">Command line arguments</param>
        public static void Run(Window mainWindow, string[] args)
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args, lifetime =>
                {
                    lifetime.MainWindow = mainWindow;
                });
        }

        /// <summary>
        /// Shows a standalone VST3 editor window.
        /// Initializes Avalonia if not already initialized.
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <returns>The editor window</returns>
        public static VstEditorWindow ShowEditor(OwnVst3Wrapper plugin)
        {
            InitializeAvalonia();

            var window = new VstEditorWindow(plugin);
            window.Show();
            return window;
        }

        /// <summary>
        /// Shows a VST3 editor window as a modal dialog.
        /// Initializes Avalonia if not already initialized.
        /// </summary>
        /// <param name="plugin">The VST3 plugin</param>
        /// <param name="owner">Owner window</param>
        /// <returns>Task that completes when dialog closes</returns>
        public static async Task ShowEditorDialogAsync(OwnVst3Wrapper plugin, Window owner)
        {
            InitializeAvalonia();

            var window = new VstEditorWindow(plugin);
            await window.ShowDialog(owner);
        }
    }

    /// <summary>
    /// Extension methods for AppBuilder
    /// </summary>
    public static class AppBuilderExtensions
    {
        /// <summary>
        /// Starts the application with classic desktop lifetime
        /// </summary>
        public static int StartWithClassicDesktopLifetime(
            this AppBuilder builder,
            string[] args,
            Action<IClassicDesktopStyleApplicationLifetime> configure)
        {
            var lifetime = new ClassicDesktopStyleApplicationLifetime
            {
                Args = args,
                ShutdownMode = ShutdownMode.OnLastWindowClose
            };

            builder.SetupWithLifetime(lifetime);
            configure(lifetime);

            return lifetime.Start(args);
        }
    }
}
