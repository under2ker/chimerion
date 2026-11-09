using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace Dnex.Tests;

/// <summary>Разделы открываются страницами главного окна: окно раздела на экран не выводится, но живет как раньше.</summary>
[Collection("scenarios")]
public class PageEmbedTests
{
    [Fact]
    public void PresentEmbedsSectionWithoutShowingItsWindowAndCloseStillCleansUp()
    {
        Wpf.Run(() =>
        {
            var before = DnexDialog.Embed;
            var embedded = new List<Window>();
            try
            {
                DnexDialog.Embed = w => { embedded.Add(w); DnexDialog.Embedded.Add(w); w.Content = null; return true; };
                var w = DnexDialog.CreateWindow("Тест скорости", 900);
                w.Content = new TextBlock { Text = "страница" };
                bool closed = false;
                w.Closed += (_, _) => { closed = true; DnexDialog.Embedded.Remove(w); };

                DnexDialog.Present(w);
                Assert.False(w.IsVisible); // окно не показано: его содержимое забрала страница
                Assert.Single(embedded);
                Assert.Equal("Тест скорости", DnexDialog.KeyOf(w)); // по названию без перевода подсвечивается пункт меню

                DnexDialog.Present(w); // повторный вызов из меню возвращает к той же странице, а не открывает окно
                Assert.Equal(2, embedded.Count);
                Assert.False(w.IsVisible);

                // «Закрыть» и Esc в разделе вызывают Close: у не показанного окна Closed тоже приходит, раздел снимается
                w.Close();
                Assert.True(closed);
                Assert.DoesNotContain(w, DnexDialog.Embedded);
            }
            finally { DnexDialog.Embed = before; DnexDialog.Embedded.Clear(); }
        });
    }

    [Fact]
    public void WithoutMainWindowSectionOpensAsSeparateWindow()
    {
        Wpf.Run(() =>
        {
            var before = DnexDialog.Embed;
            try
            {
                DnexDialog.Embed = _ => false; // главное окно в трее
                var w = DnexDialog.CreateWindow("Журнал", 600);
                w.Content = new TextBlock { Text = "журнал" };
                w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -3000;
                DnexDialog.Present(w);
                try
                {
                    Assert.True(w.IsVisible);
                    Assert.Same(w, DnexDialog.OwnerOf(w)); // диалоги из отдельного окна принадлежат ему самому
                }
                finally { w.Close(); }
            }
            finally { DnexDialog.Embed = before; }
        });
    }
}
