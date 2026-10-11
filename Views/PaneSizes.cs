using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SysLens.Views;

/// <summary>
/// Remembers where the user leaves a GridSplitter: once a drag or an arrow key moves it, the lengths of the rows or
/// columns on either side are saved in <see cref="AppSettings"/> under the splitter's <c>PaneSizes.Key</c>, and they
/// are given back when the splitter is first shown. The splitter sits in a row or column of its own, between the two
/// it resizes, and sets ResizeDirection.
/// </summary>
public static class PaneSizes
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(PaneSizes), new PropertyMetadata(null, OnKeyChanged));

    private static readonly GridLengthConverter Lengths = new();

    public static string? GetKey(GridSplitter splitter) => (string?)splitter.GetValue(KeyProperty);

    public static void SetKey(GridSplitter splitter, string? value) => splitter.SetValue(KeyProperty, value);

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not GridSplitter splitter || e.OldValue is not null)
            return;

        splitter.Loaded += Restore;
        splitter.DragCompleted += (_, _) => Save(splitter);
        // GridSplitter moves on the arrow keys and marks them handled.
        splitter.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key is Key.Left or Key.Right or Key.Up or Key.Down)
                Save(splitter);
        }), handledEventsToo: true);
    }

    // Only the first time: a tab loads its content again each time it is selected.
    private static void Restore(object sender, RoutedEventArgs e)
    {
        var splitter = (GridSplitter)sender;
        splitter.Loaded -= Restore;
        if (GetKey(splitter) is not { } key || !AppSettings.Current.Panes.TryGetValue(key, out var saved)
            || Definitions(splitter) is not { } definitions || saved.Length != definitions.Length)
            return;

        try
        {
            for (var i = 0; i < definitions.Length; i++)
                SetLength(definitions[i], (GridLength)Lengths.ConvertFromInvariantString(saved[i])!);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            // Edited by hand into something that is not a length: the panes keep their own sizes.
        }
    }

    private static void Save(GridSplitter splitter)
    {
        if (GetKey(splitter) is not { } key || Definitions(splitter) is not { } definitions)
            return;

        AppSettings.Current.Panes[key] = [.. definitions.Select(d => Lengths.ConvertToInvariantString(GetLength(d))!)];
        // When the file cannot be written, the sizes last until SysLens closes.
        AppSettings.Current.Save();
    }

    /// <summary>The rows or columns on either side of the splitter, or null when it is not between two.</summary>
    private static DefinitionBase[]? Definitions(GridSplitter splitter)
    {
        if (splitter.Parent is not Grid grid)
            return null;

        var rows = splitter.ResizeDirection == GridResizeDirection.Rows;
        var index = rows ? Grid.GetRow(splitter) : Grid.GetColumn(splitter);
        DefinitionBase[] definitions = rows ? [.. grid.RowDefinitions] : [.. grid.ColumnDefinitions];
        return index > 0 && index < definitions.Length - 1 ? [definitions[index - 1], definitions[index + 1]] : null;
    }

    private static GridLength GetLength(DefinitionBase definition) =>
        definition is RowDefinition row ? row.Height : ((ColumnDefinition)definition).Width;

    private static void SetLength(DefinitionBase definition, GridLength length)
    {
        if (definition is RowDefinition row)
            row.Height = length;
        else
            ((ColumnDefinition)definition).Width = length;
    }
}
