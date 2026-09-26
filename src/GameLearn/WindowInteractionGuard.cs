using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace GameLearn;

/// <summary>Protects hover, drag, keyboard editing and popup navigation before controls handle input.</summary>
internal sealed class WindowInteractionGuard
{
    private readonly Window window;
    private readonly MainViewModel vm;
    private readonly DispatcherTimer timer;
    private readonly bool holdWhileVisible;
    private DateTime lastInput;
    private bool closed;
    private bool contextMenuOpen;

    public WindowInteractionGuard(Window window, MainViewModel vm, bool holdWhileVisible = false)
    {
        this.window = window; this.vm = vm;
        this.holdWhileVisible = holdWhileVisible;
        timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => Update();
        window.MouseEnter += (_, _) => Update(); window.MouseLeave += (_, _) => Update();
        window.Activated += (_, _) => Update(); window.Deactivated += (_, _) => Update();
        window.StateChanged += (_, _) => Update(); window.IsVisibleChanged += (_, _) => Update();
        window.GotKeyboardFocus += (_, _) => Update(); window.LostKeyboardFocus += (_, _) => Update();
        window.GotMouseCapture += (_, _) => Update(); window.LostMouseCapture += (_, _) => Update();
        window.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => Touch()), true);
        window.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => Touch()), true);
        window.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, _) => Touch()), true);
        window.AddHandler(FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler((_, _) => { contextMenuOpen = true; vm.SetPresentationInteraction(this, true); }), true);
        window.AddHandler(FrameworkElement.ContextMenuClosingEvent, new ContextMenuEventHandler((_, _) => { contextMenuOpen = false; Update(); }), true);
        window.Loaded += (_, _) => { Update(); timer.Start(); };
        window.Closed += (_, _) => { closed = true; timer.Stop(); vm.SetPresentationInteraction(this, false); };
    }
    private void Touch() { lastInput = DateTime.UtcNow; vm.SetPresentationInteraction(this, true); }
    private void Update()
    {
        if (closed) return;
        var visible = window.IsVisible && window.WindowState != WindowState.Minimized;
        var editing = window.IsActive && HasEditorOrSelectionFocus();
        vm.SetPresentationInteraction(this, visible && (holdWhileVisible || contextMenuOpen || window.IsMouseCaptureWithin || editing
            || (window.IsActive && DateTime.UtcNow - lastInput < TimeSpan.FromSeconds(1))));
    }
    private bool HasEditorOrSelectionFocus()
    {
        var current = Keyboard.FocusedElement as DependencyObject;
        while (current is not null && current != window)
        {
            if (current is TextBoxBase or PasswordBox or Selector) return true;
            current = current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
}
