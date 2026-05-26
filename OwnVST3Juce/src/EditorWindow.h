#pragma once

#include "JuceHeader.h"
#include <functional>

/**
 * Top-level native window that hosts the VST3 plugin editor component.
 *
 * JUCE creates and owns the native OS window; the plugin editor is set as the
 * content component.  The window is always a separate top-level window (never
 * an embedded child of the host application).  On Windows the host window
 * handle is set as the Win32 owner so the editor stays in front of the host.
 *
 * All methods must be called on the JUCE message thread.
 */
class EditorWindow final : public juce::DocumentWindow
{
public:
    /**
     * Creates the window, takes ownership of editor, and shows it.
     * onClose is invoked when the user clicks the close button.
     */
    EditorWindow(juce::AudioProcessorEditor* editor,
                 const juce::String& title,
                 std::function<void()> onClose);

    ~EditorWindow() override;

    /** Makes the window visible and brings it to the front. */
    void show();

    /** Hides the window without destroying it. */
    void hide();

    /** Returns true if the window is currently visible. */
    bool isWindowOpen() const noexcept;

    /** Resizes both the window frame and the content component. */
    void resizeTo(int width, int height);

    /**
     * Fills width and height with the content component's current pixel size.
     * Returns false if no content component is present.
     */
    bool getContentSize(int& width, int& height) const;

    /** Drives any pending plugin idle processing. */
    void runIdle();

    /* juce::DocumentWindow override */
    void closeButtonPressed() override;

private:
    std::function<void()> _onClose;
    bool _open{ false };

    JUCE_DECLARE_NON_COPYABLE_WITH_LEAK_DETECTOR(EditorWindow)
};
