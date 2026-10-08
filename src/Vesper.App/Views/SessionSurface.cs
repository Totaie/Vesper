using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Vesper.Views;

/// <summary>
/// The focusable control the Mac's screen is drawn into, and where the session window sets a real
/// hardware cursor (ProtectedCursor is only settable from inside).
///
/// The picture is a composition visual filling the control, which Windows composes like any other
/// window content. There's deliberately no swap chain: a swap chain can be promoted to its own
/// overlay plane or drive a variable-refresh monitor, and one that presents only when the Mac
/// sends something presents irregularly, which makes such monitors flicker.
/// </summary>
public sealed partial class SessionSurface : UserControl
{
    public SessionSurface()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        AllowFocusOnInteraction = true;
        IsTextScaleFactorEnabled = false;
        var host = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 12, 16)) };
        Content = host;
        Visual = ElementCompositionPreview.GetElementVisual(host).Compositor.CreateSpriteVisual();
        Visual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(host, Visual);
    }

    /// <summary>The visual the picture is shown in; it always covers the control.</summary>
    public SpriteVisual Visual { get; }

    public void SetCursor(InputCursor cursor) => ProtectedCursor = cursor;
}
