#include "ui/EditorWindow.h"
#include "LinuxLoop.h"

#include <X11/Xatom.h>
#include <X11/Xlib.h>
#include <X11/Xutil.h>

#include <cstdint>
#include <cstdlib>
#include <cstring>

namespace
{
    double xftScale(Display* d)
    {
        const char* rm = XResourceManagerString(d);
        if (!rm) return 1.0;

        const char* p = std::strstr(rm, "Xft.dpi:");
        if (!p) return 1.0;

        const double dpi = std::atof(p + 8);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    class EditorWindowX11 final : public EditorWindow
    {
    public:
        EditorWindowX11(Display* d, const std::string& title, int width, int height, bool resizable, void* owner, Callbacks cb)
            : _display(d), _callbacks(std::move(cb)), _width(width), _height(height), _resizable(resizable)
        {
            const int screen = DefaultScreen(d);

            XSetWindowAttributes attrs {};
            attrs.background_pixel = BlackPixel(d, screen);
            attrs.event_mask       = StructureNotifyMask;

            _window = XCreateWindow(d, RootWindow(d, screen), 0, 0, static_cast<unsigned>(width), static_cast<unsigned>(height), 0,
                                    CopyFromParent, InputOutput, CopyFromParent, CWBackPixel | CWEventMask, &attrs);

            XStoreName(d, _window, title.c_str());
            XChangeProperty(d, _window, XInternAtom(d, "_NET_WM_NAME", False), XInternAtom(d, "UTF8_STRING", False), 8,
                            PropModeReplace, reinterpret_cast<const unsigned char*>(title.c_str()), static_cast<int>(title.size()));

            _wmDelete = XInternAtom(d, "WM_DELETE_WINDOW", False);
            XSetWMProtocols(d, _window, &_wmDelete, 1);

            if (owner)
                XSetTransientForHint(d, _window, static_cast<Window>(reinterpret_cast<uintptr_t>(owner)));

            applySizeHints();
            LinuxLoop::addWindow(_window, [this](XEvent& ev) { onEvent(ev); });
            XFlush(d);
        }

        ~EditorWindowX11() override
        {
            LinuxLoop::removeWindow(_window);
            XDestroyWindow(_display, _window);
            XFlush(_display);
        }

        void* parentHandle() const override { return reinterpret_cast<void*>(static_cast<uintptr_t>(_window)); }

        void show() override
        {
            XMapRaised(_display, _window);
            _visible = true;
            XFlush(_display);
        }

        void setContentSize(int width, int height) override
        {
            _width  = width;
            _height = height;
            applySizeHints();
            XResizeWindow(_display, _window, static_cast<unsigned>(width), static_cast<unsigned>(height));
            XFlush(_display);
        }

        void contentSize(int& width, int& height) const override
        {
            width  = _width;
            height = _height;
        }

        void setResizable(bool resizable) override
        {
            _resizable = resizable;
            applySizeHints();
        }

        bool isVisible() const override { return _visible; }

        double scaleFactor() const override { return xftScale(_display); }

    private:
        void applySizeHints()
        {
            XSizeHints* hints = XAllocSizeHints();
            if (!hints) return;

            if (!_resizable)
            {
                hints->flags      = PMinSize | PMaxSize;
                hints->min_width  = hints->max_width  = _width;
                hints->min_height = hints->max_height = _height;
            }
            XSetWMNormalHints(_display, _window, hints);
            XFree(hints);
        }

        void onEvent(XEvent& ev)
        {
            if (ev.type == ClientMessage && static_cast<Atom>(ev.xclient.data.l[0]) == _wmDelete)
            {
                XUnmapWindow(_display, _window);
                _visible = false;
                if (_callbacks.closeRequested) _callbacks.closeRequested();
            }
            else if (ev.type == ConfigureNotify)
            {
                const int w = ev.xconfigure.width, h = ev.xconfigure.height;
                if (w == _width && h == _height) return;

                _width  = w;
                _height = h;
                if (_callbacks.resized) _callbacks.resized(w, h);
            }
            else if (ev.type == UnmapNotify)
            {
                _visible = false;
            }
            else if (ev.type == MapNotify)
            {
                _visible = true;
            }
        }

        Display*  _display;
        Callbacks _callbacks;
        Window    _window   = 0;
        Atom      _wmDelete = 0;
        int       _width, _height;
        bool      _resizable;
        bool      _visible  = false;
    };
}

std::unique_ptr<EditorWindow> EditorWindow::create(const std::string& title, int width, int height,
                                                   bool resizable, void* owner, Callbacks callbacks)
{
    Display* d = LinuxLoop::display();
    if (!d) return nullptr;
    return std::make_unique<EditorWindowX11>(d, title, width, height, resizable, owner, std::move(callbacks));
}
