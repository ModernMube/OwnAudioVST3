using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OwnVST3Host;

namespace OwnVST3Host.Controls
{
    /// <summary>
    /// A control that manages the VST3 plugin editor.
    /// Since the editor now runs in its own native window, this control
    /// provides a button to open/close the editor and manages the idle timer.
    /// </summary>
    public class VstEditorHost : UserControl
    {
        private OwnVst3Wrapper? _plugin;
        private bool _isAttached;
        private DispatcherTimer? _idleTimer;
        private Button _editorButton;

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
                    CloseEditor();
                    _plugin = value;
                    UpdateUI();
                    StartIdleTimer();
                }
            }
        }

        public VstEditorHost()
        {
            _editorButton = new Button
            {
                Content = "Open Plugin Editor",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Padding = new Thickness(20, 10)
            };

            _editorButton.Click += OnEditorButtonClick;
            this.Content = _editorButton;

            // Set default size for the placeholder
            Width = 300;
            Height = 100;
        }

        private void OnEditorButtonClick(object? sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;

            try
            {
                if (_plugin.IsEditorOpen)
                {
                    _plugin.CloseEditor();
                }
                else
                {
                    _plugin.OpenEditor("VST3 Plugin Editor");
                }
                StartIdleTimer();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error toggling editor: {ex.Message}");
            }
            UpdateUI();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _isAttached = true;
            StartIdleTimer();
            UpdateUI();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _isAttached = false;
            StopIdleTimer();
            CloseEditor(); // Ensure closed when control is removed
            base.OnDetachedFromVisualTree(e);
        }

        private void CloseEditor()
        {
            try
            {
                if (_plugin != null && _plugin.IsEditorOpen)
                {
                    _plugin.CloseEditor();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error closing editor: {ex.Message}");
            }
        }

        private void UpdateUI()
        {
            if (_plugin == null)
            {
                _editorButton.IsEnabled = false;
                _editorButton.Content = "No Plugin Loaded";
            }
            else
            {
                _editorButton.IsEnabled = true;
                // Note: Actual text update happens in IdleTick to sync with external closes
            }
        }

        private void StartIdleTimer()
        {
            if (_idleTimer != null) return;
            if (_plugin == null) return;

            _idleTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60fps
            };
            _idleTimer.Tick += OnIdleTimerTick;
            _idleTimer.Start();
        }

        private void StopIdleTimer()
        {
            if (_idleTimer != null)
            {
                _idleTimer.Stop();
                _idleTimer.Tick -= OnIdleTimerTick;
                _idleTimer = null;
            }
        }

        private void OnIdleTimerTick(object? sender, EventArgs e)
        {
            try
            {
                if (_plugin != null)
                {
                    _plugin.ProcessIdle();

                    // Sync button state with actual window state
                    bool isOpen = _plugin.IsEditorOpen;
                    _editorButton.Content = isOpen ? "Close Plugin Editor" : "Open Plugin Editor";
                }
            }
            catch
            {
                // Ignore exceptions
            }
        }
    }
}
