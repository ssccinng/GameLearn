using System.Windows;
using System.Windows.Controls;
namespace GameLearn;
public partial class WordDetailView : UserControl
{
    public WordDetailView() => InitializeComponent();
    private MainViewModel Vm => (MainViewModel)DataContext;
    private async void Explain_Click(object sender, RoutedEventArgs e) => await Vm.ExplainAsync();
    private void Save_Click(object sender, RoutedEventArgs e) => Vm.SaveWord();
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        using var interaction = Vm.HoldAutomaticPresentation();
        if (Vm.SelectedWord is not null && MessageBox.Show($"删除 {Vm.SelectedWord.Word} 及其遇见记录？", "删除词条", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) Vm.DeleteSelected();
    }
    private void EnlargeScene_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Vm.SelectedEncounter is not { } encounter || Vm.HistoryImage is null) return;
        var window = new Window { Title = $"{encounter.Word} · {encounter.Game} · {encounter.At:g}", Width = 1150, Height = 780,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, Style = (Style)FindResource(typeof(Window)),
            Content = new SceneViewer { Source = Vm.HistoryImage, HighlightBounds = encounter.Bounds, Margin = new Thickness(20) } };
        _ = new WindowInteractionGuard(window, Vm, holdWhileVisible: true);
        window.PreviewKeyDown += (_, key) => { if (key.Key == System.Windows.Input.Key.Escape) window.Close(); };
        window.Show();
    }
}
