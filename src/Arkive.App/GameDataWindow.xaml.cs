using System.Data;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arkive.Core.GameData;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace Arkive.App;

/// <summary>
/// Makes or changes a Game Data mod: browse the game's tables in data2.lpk, change cells, or import a .sql patch. The
/// window only collects the changes; the Mods tab saves them into the mod.
/// </summary>
public partial class GameDataWindow : Window
{
    private const int RowLimit = 500;

    public sealed class ChangeRow(GameDataCellEdit edit)
    {
        public GameDataCellEdit Edit { get; } = edit;
        public string Database => Edit.Database;
        public string Table => Edit.Table;
        public string Row => Edit.RowLabel;
        public string Column => Edit.Column;
        public string Original => GameDataValues.Describe(Edit.Original);
        public string Value => GameDataValues.Describe(Edit.Value);
    }

    private readonly GameDataArchive _archive;
    private readonly bool _mayBeEmpty;
    private readonly Dictionary<string, GameDataCellEdit> _edits = new(StringComparer.Ordinal);
    private readonly Dictionary<DataGridColumn, string> _columns = [];
    private string? _database;
    private string? _databasePath;
    private GameDataTableSchema? _schema;
    private IReadOnlyList<GameDataRow> _rows = [];
    private bool _busy;

    /// <param name="mayBeEmpty">True when the mod has other changes, so it may keep no table changes.</param>
    public GameDataWindow(GameDataArchive archive, string name, bool newMod, bool mayBeEmpty, IReadOnlyList<GameDataCellEdit> edits)
    {
        InitializeComponent();
        _archive = archive;
        _mayBeEmpty = mayBeEmpty;
        foreach (var edit in edits) _edits[edit.CellKey] = edit;
        Title = newMod ? "New Game Data mod" : $"Game tables · {name}";
        NameTextBox.Text = name;
        NameTextBox.IsEnabled = newMod;
        ShowChanges();
        Loaded += async (_, _) => await LoadDatabasesAsync(edits.FirstOrDefault()?.Database ?? "EFTable_Item.db");
    }

    public string ModName => NameTextBox.Text.Trim();

    public IReadOnlyList<GameDataCellEdit> Edits => _edits.Values.ToArray();

    private async Task LoadDatabasesAsync(string preferred)
    {
        IReadOnlyList<string> databases = [];
        await RunAsync("Reading the list of game tables…", async () =>
        {
            databases = await Task.Run(_archive.Databases);
            DatabaseBox.ItemsSource = databases;
            return $"{databases.Count:N0} databases in data2.lpk.";
        });
        // Choosing one opens it, which waits for the list to finish loading.
        DatabaseBox.SelectedItem = databases.FirstOrDefault(name => name.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ??
                                   databases.FirstOrDefault();
    }

    private async void DatabaseBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DatabaseBox.SelectedItem is not string database || database == _database) return;
        await OpenDatabaseAsync(database, table: null, search: null);
    }

    private async Task OpenDatabaseAsync(string database, string? table, string? search)
    {
        IReadOnlyList<string>? tables = null;
        await RunAsync($"Reading {database} from data2.lpk…", async () =>
        {
            var path = await _archive.GameDatabaseAsync(database);
            tables = await Task.Run(() =>
            {
                using var connection = GameDataTables.Open(path, readOnly: true);
                return GameDataTables.Tables(connection);
            });
            _database = database;
            _databasePath = path;
            _schema = null;
            _rows = [];
            ShowRows();
            TableBox.ItemsSource = tables;
            return $"{database}: {tables.Count:N0} table(s).";
        });
        if (tables is null) return;
        if (search is not null) SearchTextBox.Text = search;
        var chosen = tables.FirstOrDefault(name => name.Equals(table, StringComparison.OrdinalIgnoreCase)) ?? tables.FirstOrDefault();
        if (Equals(TableBox.SelectedItem, chosen)) await LoadRowsAsync();
        else TableBox.SelectedItem = chosen;
    }

    private async void TableBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadRowsAsync();

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await LoadRowsAsync();

    private async void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LoadRowsAsync();
    }

    private async Task LoadRowsAsync()
    {
        if (_databasePath is not { } path || TableBox.SelectedItem is not string table) return;
        var search = SearchTextBox.Text;
        await RunAsync($"Finding {table} rows…", async () =>
        {
            var (schema, rows) = await Task.Run(() =>
            {
                using var connection = GameDataTables.Open(path, readOnly: true);
                return (GameDataTables.Schema(connection, table)!, GameDataTables.Rows(connection, table, search, RowLimit, CancellationToken.None));
            });
            _schema = schema;
            _rows = rows;
            ShowRows();
            var found = rows.Count >= RowLimit ? $"Showing the first {RowLimit:N0} rows that match; narrow the search to see others." : $"{rows.Count:N0} row(s).";
            return schema.Key.Count == 0
                ? $"{found} {table} has no key column, so its rows cannot be changed."
                : $"{found} Double-click a cell to change it.";
        });
    }

    // The rows as the game has them, with this mod's changes shown in place.
    private void ShowRows()
    {
        RowsGrid.ItemsSource = null;
        _columns.Clear();
        if (_schema is null) return;
        var data = new DataTable(_schema.Name);
        foreach (var column in _schema.Columns)
            data.Columns.Add(new DataColumn(column.Name, typeof(string)) { ReadOnly = column.KeyOrder > 0 || _schema.Key.Count == 0 });
        foreach (var row in _rows)
        {
            var values = _schema.Columns.Select(column =>
            {
                var value = _edits.TryGetValue(CellKey(row, column.Name), out var edit) ? edit.Value : row.Cells[column.Name];
                return (object)Display(value);
            }).ToArray();
            data.Rows.Add(values);
        }
        RowsGrid.ItemsSource = data.DefaultView;
    }

    private void RowsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        // Column headers read underscores as access keys; doubled, they show as written.
        e.Column.Header = e.PropertyName.Replace("_", "__");
        _columns[e.Column] = e.PropertyName;
    }

    private void RowsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || _schema is null || _database is null ||
            !_columns.TryGetValue(e.Column, out var column) || e.EditingElement is not TextBox box) return;
        var index = e.Row.GetIndex();
        if (index < 0 || index >= _rows.Count) return;
        var row = _rows[index];
        var original = row.Cells[column];
        JsonElement value;
        try
        {
            value = string.IsNullOrEmpty(box.Text) && original.ValueKind == JsonValueKind.Null
                ? original
                : GameDataValues.Parse(box.Text, original);
        }
        catch (FormatException exception)
        {
            e.Cancel = true;
            StatusText.Text = $"{column} holds numbers: {exception.Message}";
            return;
        }
        var key = CellKey(row, column);
        if (GameDataValues.Same(value, original))
        {
            _edits.Remove(key);
            StatusText.Text = $"{column} is back to the game's value.";
        }
        else
        {
            var edit = GameDataAuthoring.Edit(_database, _schema, row, column, value);
            _edits[edit.CellKey] = edit;
            StatusText.Text = $"{_schema.Name}.{column} for {edit.RowLabel}: {GameDataValues.Describe(original)} → {GameDataValues.Describe(value)}.";
        }
        ShowChanges();
    }

    private string CellKey(GameDataRow row, string column) =>
        new GameDataCellEdit(_database!, _schema!.Name,
            _schema.Key.ToDictionary(item => item.Name, item => row.Cells[item.Name], StringComparer.OrdinalIgnoreCase),
            column, GameDataValues.Null, GameDataValues.Null).CellKey;

    private static string Display(JsonElement value) => value.ValueKind == JsonValueKind.Null ? string.Empty : GameDataValues.Describe(value);

    private void ShowChanges()
    {
        ChangesGrid.ItemsSource = _edits.Values
            .OrderBy(edit => edit.Database, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edit => edit.Table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edit => edit.RowLabel, StringComparer.Ordinal)
            .ThenBy(edit => edit.Column, StringComparer.OrdinalIgnoreCase)
            .Select(edit => new ChangeRow(edit)).ToArray();
        ChangesTitle.Text = _edits.Count == 0 ? "Changes in this mod: none yet" : $"Changes in this mod: {_edits.Count:N0} cell(s)";
        RemoveButton.IsEnabled = false;
    }

    private void ChangesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveButton.IsEnabled = !_busy && ChangesGrid.SelectedItems.Count > 0;

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in ChangesGrid.SelectedItems.OfType<ChangeRow>().ToArray()) _edits.Remove(row.Edit.CellKey);
        ShowChanges();
        ShowRows();
    }

    // Opens the changed row so it can be seen and changed again.
    private async void ChangesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || ChangesGrid.SelectedItem is not ChangeRow change || _busy) return;
        var search = GameDataValues.Describe(change.Edit.Row.Values.First());
        if (!change.Database.Equals(_database, StringComparison.OrdinalIgnoreCase))
        {
            DatabaseBox.SelectionChanged -= DatabaseBox_SelectionChanged;
            DatabaseBox.SelectedItem = change.Database;
            DatabaseBox.SelectionChanged += DatabaseBox_SelectionChanged;
            await OpenDatabaseAsync(change.Database, change.Table, search);
            return;
        }
        SearchTextBox.Text = search;
        if (TableBox.SelectedItem as string == change.Table) await LoadRowsAsync();
        else TableBox.SelectedItem = change.Table;
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a .sql patch",
            Filter = "SQL patches (*.sql)|*.sql|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync($"Reading {Path.GetFileName(dialog.FileName)}…", async () =>
        {
            var sql = await File.ReadAllTextAsync(dialog.FileName);
            var import = await GameDataAuthoring.ImportSqlAsync(_archive, sql);
            foreach (var edit in import.Edits) _edits[edit.CellKey] = edit;
            ShowChanges();
            ShowRows();
            if (string.IsNullOrWhiteSpace(ModName)) NameTextBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            return $"Imported {import.Edits.Count:N0} cell change(s) from {Path.GetFileName(dialog.FileName)}." +
                   (import.Notes.Count == 0 ? string.Empty : " " + string.Join(" ", import.Notes.Take(3)));
        });
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true) is false) return;
        if (ModName.Length == 0)
        {
            StatusText.Text = "Give the mod a name.";
            NameTextBox.Focus();
            return;
        }
        if (_edits.Count == 0 && !_mayBeEmpty)
        {
            StatusText.Text = "Change at least one cell, or import a .sql patch, first.";
            return;
        }
        DialogResult = true;
    }

    private async Task RunAsync(string working, Func<Task<string>> action)
    {
        if (_busy) return;
        _busy = true;
        foreach (var control in new Control[] { DatabaseBox, TableBox, SearchButton, ImportButton, SaveButton, RemoveButton })
            control.IsEnabled = false;
        StatusText.Text = working;
        Cursor = Cursors.Wait;
        try
        {
            StatusText.Text = await action();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                                              InvalidOperationException or NotSupportedException or ArgumentException or SqliteException)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            Cursor = null;
            _busy = false;
            foreach (var control in new Control[] { DatabaseBox, TableBox, SearchButton, ImportButton, SaveButton })
                control.IsEnabled = true;
            RemoveButton.IsEnabled = ChangesGrid.SelectedItems.Count > 0;
        }
    }
}
