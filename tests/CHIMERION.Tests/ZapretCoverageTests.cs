using Dnex.Services.Tools;
using Xunit;

namespace Dnex.Tests;

/// <summary>Счетчик сайтов обхода без повторов и без адресов обновлений (аудит T-15).</summary>
[Collection("scenarios")]
public class ZapretCoverageTests
{
    [Fact]
    public void Sites_are_counted_once_without_update_hosts()
    {
        var lists = Path.Combine(ZapretTool.Dir, "lists");
        Directory.CreateDirectory(lists);
        var general = Path.Combine(lists, "list-general.txt");
        var user = Path.Combine(lists, "list-general-user.txt");
        string? oldGeneral = File.Exists(general) ? File.ReadAllText(general) : null, oldUser = File.Exists(user) ? File.ReadAllText(user) : null;
        try
        {
            File.WriteAllText(general, "youtube.com\r\ndiscord.com\r\n# комментарий\r\n\r\n");
            File.WriteAllText(user, "# Never leave this file empty\r\n# Собрано CHIMERION: свои сайты\r\nYouTube.com\r\nexample-own.org\r\nr2.dev\r\ngithub.com\r\ngithubusercontent.com\r\n");
            var (sites, _) = new ZapretTool().Coverage(null);
            Assert.Equal(3, sites); // youtube.com, discord.com, example-own.org; раньше было бы 2 + 5 = 7

            File.WriteAllText(user, "# Never leave this file empty\r\ndomain.example.abc\r\n");
            Assert.Equal(2, new ZapretTool().Coverage(null).Sites); // заглушка не сайт, файл изменился — пересчет
        }
        finally
        {
            if (oldGeneral == null) File.Delete(general); else File.WriteAllText(general, oldGeneral);
            if (oldUser == null) File.Delete(user); else File.WriteAllText(user, oldUser);
        }
    }
}
