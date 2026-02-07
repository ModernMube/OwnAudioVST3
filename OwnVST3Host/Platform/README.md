# Platform-Specific Helpers

This directory contains platform-specific helper classes for VST3 editor embedding across different operating systems.

## Overview

VST3 plugin editors require native window handles for embedding:
- **Windows**: HWND (Window Handle)
- **macOS**: NSView pointer
- **Linux**: X11 Window ID

Each platform has different requirements and APIs for creating and managing these native windows.

## Files

### NativeWindowHandle.cs
Cross-platform helper for retrieving native window handles from Avalonia UI controls.

**Features:**
- Gets native window handle from `TopLevel` (Window)
- Gets native window handle from any `Control`
- Checks platform support
- Returns platform type (Windows/macOS/Linux)

**Usage:**
```csharp
// Get handle from window
var window = new Window();
IntPtr handle = NativeWindowHandle.GetHandle(window);

// Get handle from control
var control = new Button();
IntPtr handle = NativeWindowHandle.GetHandle(control);

// Check if platform is supported
if (NativeWindowHandle.IsSupported)
{
    // Platform supports VST editor embedding
}

// Get current platform
PlatformType platform = NativeWindowHandle.CurrentPlatform;
```

**Platform-specific behavior:**
- **Windows**: Returns HWND (top-level window handle)
- **macOS**: Returns NSView pointer
- **Linux**: Returns X11 Window ID

### WindowsChildWindowHelper.cs
Windows-specific helper for creating child windows for VST3 editor embedding.

**Purpose:**
Solves the issue where Avalonia's `NativeControlHost` doesn't automatically create a child HWND on Windows, resulting in blank VST editor displays.

**Features:**
- Creates Windows child windows with proper styles
- Validates window handles
- Resizes child windows
- Destroys child windows

**Usage:**
```csharp
// Create child window
IntPtr parentHwnd = NativeWindowHandle.GetHandle(parentWindow);
IntPtr childHwnd = WindowsChildWindowHelper.CreateChildWindow(parentHwnd, 800, 600);

// Resize child window
WindowsChildWindowHelper.ResizeChildWindow(childHwnd, 1024, 768);

// Check if valid
bool isValid = WindowsChildWindowHelper.IsValidWindow(childHwnd);

// Destroy when done
WindowsChildWindowHelper.DestroyChildWindow(childHwnd);
```

**Window Styles:**
The created child window uses these styles:
- `WS_CHILD`: Marks as child window
- `WS_VISIBLE`: Initially visible
- `WS_CLIPCHILDREN`: Clips child windows when drawing
- `WS_CLIPSIBLINGS`: Clips sibling windows
- `WS_EX_CONTROLPARENT`: Enables TAB navigation (extended style)

**Window Class:**
Uses the `"Static"` window class, which is a simple container suitable for embedding.

## Platform-Specific Implementation Details

### Windows
- Uses Win32 API: `CreateWindowEx`, `DestroyWindow`, `ShowWindow`, `MoveWindow`
- Requires P/Invoke declarations
- Must handle HWND lifecycle (creation, resizing, destruction)
- Window must be made visible explicitly (`ShowWindow`)
- Window must be updated after creation (`UpdateWindow`)

### macOS
- Uses Avalonia's built-in NSView handling
- No additional helper needed (works out-of-the-box)
- Avalonia automatically creates and manages NSView

### Linux (X11)
- Uses Avalonia's built-in X11 Window handling
- No additional helper needed
- Avalonia automatically creates and manages X11 Window

## Adding New Platform Support

To add support for a new platform:

1. **Create a new helper class** (e.g., `AndroidSurfaceHelper.cs`)
2. **Implement platform-specific window creation**
3. **Update `VstEditorHost.CreateNativeControlCore()`** to detect and use the new helper
4. **Add cleanup logic** in `VstEditorHost.DestroyNativeControlCore()`
5. **Test thoroughly** on the target platform

Example template:
```csharp
internal static class [Platform]Helper
{
    // P/Invoke declarations
    [DllImport("platform-library")]
    private static extern IntPtr CreatePlatformWindow(...);

    // Public API
    public static IntPtr CreateChildWindow(IntPtr parent, int width, int height)
    {
        // Create platform-specific window
    }

    public static bool DestroyChildWindow(IntPtr handle)
    {
        // Destroy platform-specific window
    }

    public static bool ResizeChildWindow(IntPtr handle, int width, int height)
    {
        // Resize platform-specific window
    }
}
```

## Error Handling

All helper methods include error handling:
- **Invalid arguments**: Throw `ArgumentException` or `ArgumentNullException`
- **Platform API errors**: Throw `InvalidOperationException` with Win32 error code
- **Cleanup errors**: Silently ignore (logged if logging is available)

## Testing

Test checklist for each platform:
- ✅ Window creation succeeds
- ✅ Window is visible
- ✅ Window has correct size
- ✅ Window is positioned correctly (0, 0 relative to parent)
- ✅ Window can be resized
- ✅ Window can be destroyed
- ✅ Multiple windows can be created
- ✅ VST plugin editor displays correctly in the window

## Troubleshooting

### Windows: Blank VST Editor
**Problem**: VST editor shows blank screen

**Possible causes:**
1. Child window not created (helper not called)
2. Child window has wrong parent HWND
3. Child window is not visible (`WS_VISIBLE` missing)
4. Child window styles are incorrect

**Solution:**
- Verify `WindowsChildWindowHelper.CreateChildWindow()` is called
- Check that parent HWND is valid (`IsWindow(parentHwnd)`)
- Ensure `ShowWindow()` is called after creation
- Verify window styles include `WS_CHILD | WS_VISIBLE`

### macOS: Editor Not Responding
**Problem**: VST editor appears but doesn't respond to input

**Possible causes:**
1. NSView not properly attached to view hierarchy
2. Event routing issues

**Solution:**
- Verify Avalonia's NativeControlHost is used correctly
- Check that NSView is added to parent's subviews

### Linux: X11 Window Issues
**Problem**: VST editor doesn't appear or crashes

**Possible causes:**
1. X11 Window ID not valid
2. XEmbed protocol not properly implemented

**Solution:**
- Verify X11 Window ID is valid
- Check that XEmbed protocol is followed

## Performance Considerations

- **Window creation**: ~1-5ms (acceptable for UI thread)
- **Window destruction**: ~1-2ms
- **Window resize**: <1ms
- **Memory overhead**: ~4KB per child window (HWND)

Child windows are lightweight and have minimal impact on performance.

## Security Considerations

- **Handle validation**: Always validate window handles before use
- **Parent window verification**: Ensure parent HWND is valid before creating child
- **Resource cleanup**: Always destroy child windows to prevent handle leaks
- **No elevation required**: Child window creation doesn't require administrator privileges

## Future Enhancements

1. **Android Surface support**: Add helper for Android SurfaceView
2. **iOS UIView support**: Add helper for iOS UIView
3. **Wayland support**: Add helper for Wayland surfaces (Linux)
4. **DPI awareness**: Add DPI scaling support for child windows
5. **Window styles customization**: Allow custom window styles per use case
6. **Diagnostics**: Add detailed logging for troubleshooting

## References

- [Win32 CreateWindowEx Documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowexa)
- [VST3 SDK - IPlugView Documentation](https://steinbergmedia.github.io/vst3_doc/vstinterfaces/classSteinberg_1_1IPlugView.html)
- [Avalonia NativeControlHost](https://docs.avaloniaui.net/docs/concepts/custom-controls/how-to-create-a-custom-controls-library)
