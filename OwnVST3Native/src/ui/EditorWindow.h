#pragma once

#include <functional>
#include <memory>
#include <string>

// we own the editor window, not C#. UI thread only.
class EditorWindow
{
public:
    struct Callbacks
    {
        std::function<void(int, int)> resized;

        std::function<void()> closeRequested;
    };

    // owner: HWND / NSWindow / X11 Window, or null
    static std::unique_ptr<EditorWindow> create(const std::string& title, int width, int height,
                                                bool resizable, void* owner, Callbacks callbacks);

    virtual ~EditorWindow() = default;

    virtual void* parentHandle() const = 0;

    virtual void   show() = 0;
    virtual void   setContentSize(int width, int height) = 0;
    virtual void   contentSize(int& width, int& height) const = 0;
    virtual void   setResizable(bool resizable) = 0;
    virtual bool   isVisible() const = 0;

    virtual double scaleFactor() const = 0;

    // AppKit keeps unretained pointers into the plugin view, drop them first
    virtual void prepareForViewRemoval() {}
};
