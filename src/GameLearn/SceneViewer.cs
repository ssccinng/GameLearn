using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace GameLearn;

public sealed class SceneViewer : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(SceneViewer), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty HighlightBoundsProperty = DependencyProperty.Register(nameof(HighlightBounds), typeof(PixelRect), typeof(SceneViewer), new PropertyMetadata(null, Changed));
    public BitmapSource? Source { get => (BitmapSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public PixelRect? HighlightBounds { get => (PixelRect?)GetValue(HighlightBoundsProperty); set => SetValue(HighlightBoundsProperty, value); }
    private readonly Grid surface = new();
    private readonly Image image = new();
    public Canvas Overlay { get; } = new() { Background = Brushes.Transparent };
    public event Action<RecognizedLine>? LineClicked;
    private IReadOnlyList<RecognizedLine> lines = Array.Empty<RecognizedLine>();
    public SceneViewer()
    {
        surface.Children.Add(image); surface.Children.Add(Overlay);
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = surface };
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SceneViewer)d).Render();
    public void SetLines(IReadOnlyList<RecognizedLine> value) { lines = value; Render(); }
    private void Render()
    {
        image.Source = Source; Overlay.Children.Clear();
        if (Source is null) { surface.Width = 0; surface.Height = 0; return; }
        surface.Width = Source.PixelWidth; surface.Height = Source.PixelHeight;
        foreach (var line in lines)
        {
            var button = new Button { Background = new SolidColorBrush(Color.FromArgb(25, 168, 231, 191)), BorderBrush = new SolidColorBrush(Color.FromRgb(168, 231, 191)), BorderThickness = new Thickness(2), ToolTip = line.Text, Tag = line, Style = null, Cursor = System.Windows.Input.Cursors.Hand };
            button.Click += (_, _) => LineClicked?.Invoke(line); Place(button, line.Bounds);
        }
        if (HighlightBounds is { } rect)
            Place(new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(168, 231, 191)), StrokeThickness = Math.Max(3, Source.PixelWidth / 500.0), Fill = new SolidColorBrush(Color.FromArgb(35, 168, 231, 191)), IsHitTestVisible = false }, rect);
    }
    private void Place(FrameworkElement element, PixelRect rect)
    {
        element.Width = Math.Max(1, rect.Width); element.Height = Math.Max(1, rect.Height);
        Canvas.SetLeft(element, rect.X); Canvas.SetTop(element, rect.Y); Overlay.Children.Add(element);
    }
}
