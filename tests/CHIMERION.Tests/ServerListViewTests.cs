using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Dnex.Models;

namespace Dnex.Tests;

/// <summary>
/// Изменение списка серверов, к которому привязана таблица. Под DeferRefresh() WPF не дает добавлять, вставлять и
/// удалять строки: InvalidOperationException «Не удается изменить или проверить содержимое позиции Current для
/// CollectionView, пока обновление Refresh отложено». Так упал первый вариант отсева найденных серверов, и так же
/// молча не работали удаление больше 50 строк автоочисткой и ее отмена.
/// </summary>
public class ServerListViewTests
{
    private static (ObservableCollection<DnsServer> List, ListCollectionView View) Make(int n)
    {
        var list = new ObservableCollection<DnsServer>();
        for (int i = 0; i < n; i++) list.Add(new DnsServer { Name = "s" + i });
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(list);
        view.SortDescriptions.Add(new SortDescription(nameof(DnsServer.Name), ListSortDirection.Ascending));
        view.MoveCurrentToFirst();
        return (list, view);
    }

    [Fact]
    public void Removing_rows_without_deferred_refresh_works_and_keeps_the_view_in_sync()
    {
        var (list, view) = Make(600);
        foreach (var s in list.Where((_, i) => i % 2 == 0).ToList()) list.Remove(s);
        Assert.Equal(300, list.Count);
        Assert.Equal(300, view.Count);
    }

    [Fact]
    public void WPF_still_forbids_changes_under_deferred_refresh()
    {
        // Если этот тест упадет, WPF разрешил такие изменения, и сторожевой тест ниже можно будет убрать
        var (list, view) = Make(40);
        using (view.DeferRefresh())
        {
            Assert.Throws<InvalidOperationException>(() => list.Remove(list[0]));
            Assert.Throws<InvalidOperationException>(() => list.Insert(0, new DnsServer { Name = "x" }));
        }
    }

    [Fact]
    public void Window_code_never_changes_the_server_list_under_deferred_refresh()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CHIMERION.csproj"))) dir = dir.Parent;
        if (dir == null) return;
        foreach (var file in Directory.GetFiles(dir.FullName, "MainWindow*.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//") || !lines[i].Contains("DeferRefresh()")) continue;
                // В блоке под DeferRefresh() (строка с ним и ближайшие 12 строк) нет изменений списка серверов
                var block = string.Join("\n", lines.Skip(i).Take(13));
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(block, @"_servers\.(Remove|Insert|Add|Clear)\("),
                    $"{Path.GetFileName(file)}:{i + 1}: изменение списка серверов под DeferRefresh() падает в WPF");
            }
        }
    }
}
