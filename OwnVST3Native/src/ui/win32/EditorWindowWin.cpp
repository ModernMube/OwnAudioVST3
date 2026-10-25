#include "ui/EditorWindow.h"
#include "Win32Common.h"

#include <string>

namespace
{
    const wchar_t* kFrameClass     = L"OwnVst3EditorFrame";
    const wchar_t* kContainerClass = L"OwnVst3EditorHost";

    std::wstring widen(const std::string& s)
    {
        if (s.empty()) return {};
        const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
        std::wstring w(static_cast<size_t>(n), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
        return w;
    }

    UINT dpiOf(HWND hwnd)
    {
        using Fn = UINT (WINAPI*)(HWND);
        static auto fn = reinterpret_cast<Fn>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetDpiForWindow"));
        const UINT dpi = fn && hwnd ? fn(hwnd) : 0;
        return dpi ? dpi : 96;
    }

    RECT frameRectFor(HWND hwnd, DWORD style, DWORD exStyle, int width, int height)
    {
        RECT r { 0, 0, width, height };
        using Fn = BOOL (WINAPI*)(LPRECT, DWORD, BOOL, DWORD, UINT);
        static auto fn = reinterpret_cast<Fn>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "AdjustWindowRectExForDpi"));
        if (fn) fn(&r, style, FALSE, exStyle, dpiOf(hwnd));
        else    AdjustWindowRectEx(&r, style, FALSE, exStyle);
        return r;
    }

    class EditorWindowWin;
    LRESULT CALLBACK frameProc(HWND, UINT, WPARAM, LPARAM);

    void registerClasses()
    {
        static bool done = false;
        if (done) return;
        done = true;

        WNDCLASSEXW frame { sizeof(frame) };
        frame.lpfnWndProc   = frameProc;
        frame.hInstance     = ownvst3_moduleInstance();
        frame.hCursor       = LoadCursorW(nullptr, IDC_ARROW);
        frame.hbrBackground = reinterpret_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
        frame.lpszClassName = kFrameClass;
        RegisterClassExW(&frame);

        WNDCLASSEXW host { sizeof(host) };
        host.lpfnWndProc   = DefWindowProcW;
        host.hInstance     = ownvst3_moduleInstance();
        host.hCursor       = LoadCursorW(nullptr, IDC_ARROW);
        host.lpszClassName = kContainerClass;
        RegisterClassExW(&host);
    }

    class EditorWindowWin final : public EditorWindow
    {
    public:
        EditorWindowWin(const std::string& title, int width, int height, bool resizable, void* owner, Callbacks cb)
            : _callbacks(std::move(cb))
        {
            registerClasses();

            _style = resizable ? WS_OVERLAPPEDWINDOW : (WS_OVERLAPPEDWINDOW & ~(WS_THICKFRAME | WS_MAXIMIZEBOX));
            const RECT r = frameRectFor(static_cast<HWND>(owner), _style, 0, width, height);

            _programmatic = true;
            _frame = CreateWindowExW(0, kFrameClass, widen(title).c_str(), _style | WS_CLIPCHILDREN,
                                     CW_USEDEFAULT, CW_USEDEFAULT, r.right - r.left, r.bottom - r.top,
                                     static_cast<HWND>(owner), nullptr, ownvst3_moduleInstance(), this);

            _container = CreateWindowExW(0, kContainerClass, L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                                         0, 0, width, height, _frame, nullptr, ownvst3_moduleInstance(), nullptr);
            _programmatic = false;

            // The DPI of the monitor we landed on may differ from the owner's; size the client exactly.
            setContentSize(width, height);
        }

        ~EditorWindowWin() override
        {
            if (_frame)
            {
                SetWindowLongPtrW(_frame, GWLP_USERDATA, 0);
                DestroyWindow(_frame);
            }
        }

        bool valid() const { return _frame && _container; }

        void* parentHandle() const override { return _container; }

        void show() override
        {
            ShowWindow(_frame, SW_SHOWNORMAL);
            SetForegroundWindow(_frame);
        }

        void setContentSize(int width, int height) override
        {
            const RECT r = frameRectFor(_frame, _style, 0, width, height);
            _programmatic = true;
            SetWindowPos(_frame, nullptr, 0, 0, r.right - r.left, r.bottom - r.top,
                         SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            MoveWindow(_container, 0, 0, width, height, TRUE);
            _programmatic = false;
        }

        void contentSize(int& width, int& height) const override
        {
            RECT r {};
            GetClientRect(_frame, &r);
            width  = r.right - r.left;
            height = r.bottom - r.top;
        }

        void setResizable(bool resizable) override
        {
            _style = resizable ? WS_OVERLAPPEDWINDOW : (WS_OVERLAPPEDWINDOW & ~(WS_THICKFRAME | WS_MAXIMIZEBOX));
            SetWindowLongPtrW(_frame, GWL_STYLE, static_cast<LONG_PTR>(_style | WS_CLIPCHILDREN | (IsWindowVisible(_frame) ? WS_VISIBLE : 0)));
            SetWindowPos(_frame, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        bool isVisible() const override { return IsWindowVisible(_frame) != FALSE; }

        double scaleFactor() const override { return dpiOf(_frame) / 96.0; }

        LRESULT handle(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
        {
            switch (msg)
            {
                case WM_CLOSE:
                    ShowWindow(hwnd, SW_HIDE);
                    if (_callbacks.closeRequested) _callbacks.closeRequested();
                    return 0;

                case WM_SIZE:
                    if (!_programmatic && wp != SIZE_MINIMIZED)
                    {
                        const int w = LOWORD(lp), h = HIWORD(lp);
                        MoveWindow(_container, 0, 0, w, h, TRUE);
                        if (_callbacks.resized) _callbacks.resized(w, h);
                    }
                    return 0;

                case WM_DPICHANGED:
                {
                    const auto* r = reinterpret_cast<const RECT*>(lp);
                    SetWindowPos(hwnd, nullptr, r->left, r->top, r->right - r->left, r->bottom - r->top,
                                 SWP_NOZORDER | SWP_NOACTIVATE);
                    return 0;
                }

                case WM_ERASEBKGND:
                    return 1;
            }
            return DefWindowProcW(hwnd, msg, wp, lp);
        }

    private:
        Callbacks _callbacks;
        HWND      _frame        = nullptr;
        HWND      _container    = nullptr;
        DWORD     _style        = 0;
        bool      _programmatic = false;
    };

    LRESULT CALLBACK frameProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
    {
        if (msg == WM_NCCREATE)
        {
            const auto* cs = reinterpret_cast<const CREATESTRUCTW*>(lp);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(cs->lpCreateParams));
        }

        if (auto* self = reinterpret_cast<EditorWindowWin*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA)))
            return self->handle(hwnd, msg, wp, lp);

        return DefWindowProcW(hwnd, msg, wp, lp);
    }
}

std::unique_ptr<EditorWindow> EditorWindow::create(const std::string& title, int width, int height,
                                                   bool resizable, void* owner, Callbacks callbacks)
{
    auto w = std::make_unique<EditorWindowWin>(title, width, height, resizable, owner, std::move(callbacks));
    if (!w->valid()) return nullptr;
    return w;
}
