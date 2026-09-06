using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ToolsTouch.Desktop.Controls;

// Spacing belongs to a form/toolbar container, never to every control globally.
public static class WorkspaceLayout
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.RegisterAttached("Gap", typeof(double), typeof(WorkspaceLayout), new PropertyMetadata(0d, Changed));
    public static void SetGap(DependencyObject target, double value) => target.SetValue(GapProperty, value);
    public static double GetGap(DependencyObject target) => (double)target.GetValue(GapProperty);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Panel panel) return;
        panel.Loaded -= Apply; panel.Loaded += Apply;
        if (panel.IsLoaded) Apply(panel, new RoutedEventArgs());
    }
    private static void Apply(object sender, RoutedEventArgs args)
    {
        var panel = (Panel)sender;
        var gap = GetGap(panel);
        foreach (UIElement child in panel.Children)
            if (child is FrameworkElement element && element.ReadLocalValue(FrameworkElement.MarginProperty) == DependencyProperty.UnsetValue)
                element.SetCurrentValue(FrameworkElement.MarginProperty, panel is WrapPanel ? new Thickness(0, 0, gap, gap) : new Thickness(0, 0, 0, gap));
    }
}

// An adorner leaves the native DataGrid template and its virtualizing scroll host intact.
public static class EmptyState
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.RegisterAttached("Message", typeof(string), typeof(EmptyState), new PropertyMetadata(null, Changed));
    public static void SetMessage(DependencyObject target, string value) => target.SetValue(MessageProperty, value);
    public static string GetMessage(DependencyObject target) => (string)target.GetValue(MessageProperty);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is ItemsControl control && args.OldValue == null) _ = new Presenter(control);
    }
    private sealed class Presenter
    {
        private readonly ItemsControl control;
        private MessageAdorner? adorner;
        private AdornerLayer? layer;
        public Presenter(ItemsControl control)
        {
            this.control = control;
            control.Loaded += Loaded;
            control.Unloaded += Unloaded;
        }
        private void Loaded(object sender, RoutedEventArgs e)
        {
            ((INotifyCollectionChanged)control.Items).CollectionChanged += ItemsChanged;
            layer = AdornerLayer.GetAdornerLayer(control);
            Update();
        }
        private void Unloaded(object sender, RoutedEventArgs e)
        {
            ((INotifyCollectionChanged)control.Items).CollectionChanged -= ItemsChanged;
            if (adorner != null) layer?.Remove(adorner);
            adorner = null; layer = null;
        }
        private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Update();
        private void Update()
        {
            if (control.HasItems && adorner != null) { layer?.Remove(adorner); adorner = null; }
            else if (!control.HasItems && adorner == null && layer != null)
            {
                adorner = new MessageAdorner(control, GetMessage(control)); layer.Add(adorner);
            }
        }
    }
    private sealed class MessageAdorner : Adorner
    {
        private readonly TextBlock text;
        public MessageAdorner(UIElement target, string message) : base(target)
        {
            IsHitTestVisible = false;
            text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                FontSize = 13, Foreground = (Brush)System.Windows.Application.Current.FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
            AddVisualChild(text);
        }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => text;
        protected override Size MeasureOverride(Size constraint)
        {
            var size = AdornedElement.RenderSize;
            text.Measure(new Size(Math.Max(0, size.Width - 32), Math.Max(0, size.Height - 40)));
            return size;
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = AdornedElement.RenderSize;
            Clip = new RectangleGeometry(new Rect(size));
            text.Arrange(new Rect(16, 40, Math.Max(0, size.Width - 32), Math.Max(0, size.Height - 40)));
            return size;
        }
    }
}
