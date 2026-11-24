using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using NeoIDE.Sdk;

namespace NeoIDE.Plugin.Database;

/// <summary>DataGrid of query results: real column names, NULL shown as NULL, copy as TSV / CSV.</summary>
public static class ResultGrid
{
    private sealed class NullConverter : IValueConverter
    {
        public static readonly NullConverter Instance = new();
        public object Convert(object? value, Type t, object? p, CultureInfo c) => value is null or DBNull ? "NULL" : value is DateTime d ? d.ToString("yyyy-MM-dd HH:mm:ss") : value;
        public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
    }

    public static DataGrid Create()
    {
        var grid = Ui.DataGrid();
        grid.AutoGeneratingColumn += (_, e) =>
        {
            if (grid.ItemsSource is not DataView view || !view.Table!.Columns.Contains(e.PropertyName)) return;
            var col = view.Table.Columns[e.PropertyName]!;
            var numeric = col.DataType != typeof(string) && col.DataType != typeof(bool) && col.DataType != typeof(DateTime);
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            if (numeric) style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            var nullTrigger = new DataTrigger { Binding = new Binding(e.PropertyName), Value = DBNull.Value };
            nullTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x86, 0x8A, 0x91))));
            nullTrigger.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic));
            style.Triggers.Add(nullTrigger);
            e.Column = new DataGridTextColumn
            {
                Header = col.Caption, SortMemberPath = e.PropertyName, MaxWidth = 420, ElementStyle = style,
                Binding = new Binding(e.PropertyName) { Converter = NullConverter.Instance, Mode = BindingMode.OneWay },
            };
        };
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Add("Copy", () => CopySelection(grid, header: false));
        Add("Copy with header", () => CopySelection(grid, header: true));
        Add("Copy all as CSV", () => { if (grid.ItemsSource is DataView v) Clipboard.SetText(ToCsv(v.Table!)); });
        Add("Export to CSV…", () =>
        {
            if (grid.ItemsSource is not DataView v) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "result.csv" };
            if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, ToCsv(v.Table!), new UTF8Encoding(true));
        });
        grid.ContextMenu = menu;
        return grid;
    }

    public static void Show(DataGrid grid, DataTable? table)
    {
        grid.ItemsSource = null;
        grid.Columns.Clear();
        if (table != null) grid.ItemsSource = table.DefaultView;
    }

    private static string Cell(object? v) => v is null or DBNull ? "NULL" : v is DateTime d ? d.ToString("yyyy-MM-dd HH:mm:ss") : System.Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";

    private static void CopySelection(DataGrid grid, bool header)
    {
        if (grid.ItemsSource is not DataView view) return;
        var cells = grid.SelectedCells;
        if (cells.Count == 0) return;
        var cols = cells.Select(c => c.Column).Distinct().OrderBy(c => c.DisplayIndex).ToList();
        var rows = cells.Select(c => c.Item).Distinct().OfType<DataRowView>().ToList();
        var sb = new StringBuilder();
        if (header) sb.AppendLine(string.Join('\t', cols.Select(c => c.Header)));
        foreach (var r in rows)
            sb.AppendLine(string.Join('\t', cols.Select(c => Cell(r.Row[c.SortMemberPath]))));
        Clipboard.SetText(sb.ToString().TrimEnd('\r', '\n'));
    }

    public static string ToCsv(DataTable t)
    {
        static string Esc(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r', ';' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', t.Columns.Cast<DataColumn>().Select(c => Esc(c.Caption))));
        foreach (DataRow r in t.Rows) sb.AppendLine(string.Join(',', r.ItemArray.Select(v => Esc(v is null or DBNull ? "" : Cell(v)))));
        return sb.ToString();
    }
}
