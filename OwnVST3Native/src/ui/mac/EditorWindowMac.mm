#include "ui/EditorWindow.h"

#import <Cocoa/Cocoa.h>

@interface OwnVst3EditorDelegate : NSObject <NSWindowDelegate>
@property (nonatomic) EditorWindow::Callbacks* callbacks;
@property (nonatomic) BOOL programmaticResize;
@end

@implementation OwnVst3EditorDelegate

- (BOOL)windowShouldClose:(NSWindow*)sender
{
    [sender orderOut:nil];
    if (self.callbacks && self.callbacks->closeRequested)
        self.callbacks->closeRequested();
    return NO;
}

- (void)windowDidResize:(NSNotification*)note
{
    if (self.programmaticResize || !self.callbacks || !self.callbacks->resized) return;

    NSWindow* w = note.object;
    const NSRect r = [w contentRectForFrameRect:w.frame];
    self.callbacks->resized(static_cast<int>(r.size.width), static_cast<int>(r.size.height));
}

@end

namespace
{
    class EditorWindowMac final : public EditorWindow
    {
    public:
        EditorWindowMac(const std::string& title, int width, int height, bool resizable, void* owner, Callbacks cb)
            : _callbacks(std::move(cb))
        {
            NSWindowStyleMask style = NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable;
            if (resizable) style |= NSWindowStyleMaskResizable;

            _window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, width, height)
                                                  styleMask:style
                                                    backing:NSBackingStoreBuffered
                                                      defer:NO];
            _window.releasedWhenClosed = NO;
            _window.title = [NSString stringWithUTF8String:title.c_str()] ?: @"";

            _content = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, width, height)];
            _content.autoresizesSubviews = YES;
            _window.contentView = _content;

            _delegate = [[OwnVst3EditorDelegate alloc] init];
            _delegate.callbacks = &_callbacks;
            _window.delegate = _delegate;

            _parent = ownerWindow(owner);
            [_window center];
        }

        ~EditorWindowMac() override
        {
            _delegate.callbacks = nullptr;
            _window.delegate = nil;
            if (_parent) [_parent removeChildWindow:_window];
            [_window orderOut:nil];
            [_window close];
        }

        void* parentHandle() const override { return (__bridge void*)_content; }

        void show() override
        {
            if (_parent && _window.parentWindow == nil)
                [_parent addChildWindow:_window ordered:NSWindowAbove];
            [_window makeKeyAndOrderFront:nil];
        }

        void setContentSize(int width, int height) override
        {
            _delegate.programmaticResize = YES;
            [_window setContentSize:NSMakeSize(width, height)];
            _delegate.programmaticResize = NO;
        }

        void contentSize(int& width, int& height) const override
        {
            const NSRect r = [_window contentRectForFrameRect:_window.frame];
            width  = static_cast<int>(r.size.width);
            height = static_cast<int>(r.size.height);
        }

        void setResizable(bool resizable) override
        {
            if (resizable) _window.styleMask |= NSWindowStyleMaskResizable;
            else           _window.styleMask &= ~NSWindowStyleMaskResizable;
        }

        bool isVisible() const override { return _window.visible; }

        // VST3 and AU views size themselves in points on macOS; the backing scale is theirs.
        double scaleFactor() const override { return 1.0; }

    private:
        static NSWindow* ownerWindow(void* owner)
        {
            if (!owner) return nil;

            id obj = (__bridge id)owner;
            if ([obj isKindOfClass:[NSWindow class]]) return (NSWindow*)obj;
            if ([obj isKindOfClass:[NSView class]])   return ((NSView*)obj).window;
            return nil;
        }

        Callbacks                _callbacks;
        NSWindow*                _window   = nil;
        NSView*                  _content  = nil;
        NSWindow*                _parent   = nil;
        OwnVst3EditorDelegate*   _delegate = nil;
    };
}

std::unique_ptr<EditorWindow> EditorWindow::create(const std::string& title, int width, int height,
                                                   bool resizable, void* owner, Callbacks callbacks)
{
    return std::make_unique<EditorWindowMac>(title, width, height, resizable, owner, std::move(callbacks));
}
