#pragma once

#include <functional>
#include <memory>
#include <string>

/**
 * Top-level window the native library creates and owns for a plugin editor (rule 1 of the plan:
 * the C# side never creates the window). Every method runs on the UI thread.
 */
class EditorWindow
{
public:
    struct Callbacks
    {
        /** The user resized the window; width / height are the new content size in pixels. */
        std::function<void(int, int)> resized;

        /** The user asked to close it. The window is already hidden; tear down when convenient. */
        std::function<void()> closeRequested;
    };

    /** owner: HWND to stay in front of (Windows), NSWindow / NSView (macOS), X11 Window (Linux), or null. */
    static std::unique_ptr<EditorWindow> create(const std::string& title, int width, int height,
                                                bool resizable, void* owner, Callbacks callbacks);

    virtual ~EditorWindow() = default;

    /** HWND / NSView* / X11 Window id the plugin view attaches to. */
    virtual void* parentHandle() const = 0;

    virtual void   show() = 0;
    virtual void   setContentSize(int width, int height) = 0;
    virtual void   contentSize(int& width, int& height) const = 0;
    virtual void   setResizable(bool resizable) = 0;
    virtual bool   isVisible() const = 0;

    /** Physical pixels per logical pixel, what IPlugViewContentScaleSupport wants. */
    virtual double scaleFactor() const = 0;

    /**
     * Call before the plugin takes its view out: hides the window and lets go of every
     * pointer AppKit keeps into the plugin's views (first responder, key-view loop, popups).
     */
    virtual void prepareForViewRemoval() {}
};
