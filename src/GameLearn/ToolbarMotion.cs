using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GameLearn;

public static class ToolbarMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ToolbarMotion), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ButtonBase button) return;
        if ((bool)args.NewValue)
        {
            button.RenderTransformOrigin = new Point(.5, .5); button.RenderTransform = new ScaleTransform(1, 1);
            button.MouseEnter += Hover; button.MouseLeave += Leave;
            button.PreviewMouseLeftButtonDown += Press; button.PreviewMouseLeftButtonUp += Release;
            button.LostMouseCapture += Release; button.Unloaded += Leave;
        }
        else
        {
            button.MouseEnter -= Hover; button.MouseLeave -= Leave;
            button.PreviewMouseLeftButtonDown -= Press; button.PreviewMouseLeftButtonUp -= Release;
            button.LostMouseCapture -= Release; button.Unloaded -= Leave;
            Scale(button, 1);
        }
    }
    private static void Hover(object sender, RoutedEventArgs e) => Scale((ButtonBase)sender, 1.025);
    private static void Leave(object sender, RoutedEventArgs e) => Scale((ButtonBase)sender, 1);
    private static void Press(object sender, RoutedEventArgs e) => Scale((ButtonBase)sender, .96);
    private static void Release(object sender, RoutedEventArgs e) => Scale((ButtonBase)sender, ((ButtonBase)sender).IsMouseOver ? 1.025 : 1);
    private static void Scale(ButtonBase button, double target)
    {
        if (button.RenderTransform is not ScaleTransform transform) return;
        var enabled = SystemParameters.ClientAreaAnimation && button.IsVisible;
        var x = transform.ScaleX; var y = transform.ScaleY;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, null); transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        transform.ScaleX = enabled ? target : 1; transform.ScaleY = enabled ? target : 1;
        if (!enabled) return;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(x, target, 110));
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(y, target, 110));
    }
    public static void Enter(FrameworkElement content)
    {
        if (!content.IsVisible || !SystemParameters.ClientAreaAnimation) return;
        content.BeginAnimation(UIElement.OpacityProperty, null); content.Opacity = 1;
        content.BeginAnimation(UIElement.OpacityProperty, Tween(.35, 1, 160));
        var slide = new TranslateTransform(); content.RenderTransform = slide;
        slide.BeginAnimation(TranslateTransform.YProperty, Tween(5, 0, 160));
    }
    private static DoubleAnimation Tween(double from, double to, int milliseconds) => new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
}
