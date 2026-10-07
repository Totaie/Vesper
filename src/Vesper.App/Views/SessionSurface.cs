using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Vesper.Views;

/// <summary>
/// The focusable control the Mac's screen is drawn into. It owns the swap chain panel and lets
/// the session window set a real hardware cursor (ProtectedCursor is only settable from inside).
/// </summary>
public sealed partial class SessionSurface : UserControl
{
    public SessionSurface()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        AllowFocusOnInteraction = true;
        IsTextScaleFactorEnabled = false;
        Panel = new CanvasSwapChainPanel();
        Content = Panel;
    }

    public CanvasSwapChainPanel Panel { get; }

    public void SetCursor(InputCursor cursor) => ProtectedCursor = cursor;
}
