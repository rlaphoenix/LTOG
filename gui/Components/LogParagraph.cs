using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace LTOG.Gui;

/// <summary>
/// Renders a live collection of <see cref="LogLine"/> into a single
/// <see cref="RichTextBlock"/> — one paragraph, a Run + LineBreak per line — so the
/// whole output selects and copies as one contiguous, multi-line paragraph while each
/// line keeps its severity colour. (An ItemsControl of TextBlocks can't be selected
/// across line boundaries.)
/// </summary>
public static class LogParagraph
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source", typeof(ObservableCollection<LogLine>), typeof(LogParagraph),
            new PropertyMetadata(null, OnSourceChanged));

    public static void SetSource(DependencyObject o, ObservableCollection<LogLine> v) => o.SetValue(SourceProperty, v);
    public static ObservableCollection<LogLine> GetSource(DependencyObject o) => (ObservableCollection<LogLine>)o.GetValue(SourceProperty);

    // Remember the handler per target so recycled templates detach the old collection.
    private static readonly DependencyProperty HandlerProperty =
        DependencyProperty.RegisterAttached(
            "Handler", typeof(NotifyCollectionChangedEventHandler), typeof(LogParagraph), new PropertyMetadata(null));

    private static void OnSourceChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not RichTextBlock rtb) return;

        if (e.OldValue is ObservableCollection<LogLine> old &&
            o.GetValue(HandlerProperty) is NotifyCollectionChangedEventHandler prev)
            old.CollectionChanged -= prev;

        var para = new Paragraph();
        rtb.Blocks.Clear();
        rtb.Blocks.Add(para);

        if (e.NewValue is not ObservableCollection<LogLine> lines) return;

        foreach (var line in lines) Append(para, line);

        NotifyCollectionChangedEventHandler handler = (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset) { para.Inlines.Clear(); return; }
            if (args.NewItems != null)
                foreach (LogLine line in args.NewItems) Append(para, line);
        };
        lines.CollectionChanged += handler;
        o.SetValue(HandlerProperty, handler);
    }

    private static void Append(Paragraph para, LogLine line)
    {
        if (para.Inlines.Count > 0) para.Inlines.Add(new LineBreak());
        para.Inlines.Add(new Run { Text = line.Text, Foreground = line.Brush });
    }
}
