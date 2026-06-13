using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopOrganizer.Models;
using DesktopOrganizer.Services;

namespace DesktopOrganizer.UI;

/// <summary>Окно быстрого поиска по элементам коробок, Ctrl+Space (раздел 5.7 ТЗ).</summary>
public partial class SearchWindow : Window
{
    public SearchWindow()
    {
        InitializeComponent();
        Left = (SystemParameters.WorkArea.Width - Width) / 2 + SystemParameters.WorkArea.Left;
        Top = SystemParameters.WorkArea.Height * 0.2 + SystemParameters.WorkArea.Top;
        Loaded += (_, _) => QueryBox.Focus();
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var results = App.Db.SearchItems(QueryBox.Text);
        foreach (var r in results)
            r.Icon = IconCache.GetIcon(r.FullPath, r.ItemType == "Folder");
        ResultsList.ItemsSource = results;
        if (results.Count > 0) ResultsList.SelectedIndex = 0;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
            case Key.Down when QueryBox.IsKeyboardFocused && ResultsList.Items.Count > 0:
                ResultsList.Focus();
                ResultsList.SelectedIndex = Math.Min(ResultsList.SelectedIndex + 1, ResultsList.Items.Count - 1);
                e.Handled = true;
                break;
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (ResultsList.SelectedItem is not SearchResult result) return;
        try
        {
            Process.Start(new ProcessStartInfo(result.FullPath) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            Logger.Error("SearchOpen", ex);
            // Файл мог быть удален — предложим открыть папку.
            var dir = Path.GetDirectoryName(result.FullPath);
            if (dir != null && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
    }

    private void Window_Deactivated(object sender, EventArgs e) => Close();
}
