using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glassgram.Controls;

public sealed class GlassPanel : ContentControl
{
    public GlassPanel() => DefaultStyleKey = typeof(GlassPanel);

    public double GlassOpacity
    {
        get => (double)GetValue(GlassOpacityProperty);
        set => SetValue(GlassOpacityProperty, value);
    }

    public static readonly DependencyProperty GlassOpacityProperty = DependencyProperty.Register(
        nameof(GlassOpacity), typeof(double), typeof(GlassPanel), new PropertyMetadata(0.72));
}
