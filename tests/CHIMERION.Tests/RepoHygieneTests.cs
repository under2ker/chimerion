using System.Text.RegularExpressions;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>
/// Проверки репозитория, которые гоняет и CI: подписи каталогов верны, в файлах нет секретов, в текстах нет «ё» и длинных тире,
/// версия в CHANGELOG совпадает с версией проекта. Ошибка здесь значит, что в репозиторий попало то, чего там быть не должно.
/// </summary>
public class RepoHygieneTests
{
    private static readonly string? Root = FindRoot();

    private static string? FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !(File.Exists(Path.Combine(dir.FullName, "CHIMERION.csproj")) && File.Exists(Path.Combine(dir.FullName, "CHANGELOG.md"))))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static readonly string[] SkipDirs = { "bin", "obj", ".git", ".vs", "node_modules", "out", "review", "backup" };
    private static readonly string[] TextExt = { ".cs", ".md", ".json", ".xaml", ".yml", ".yaml", ".ps1", ".cmd", ".html", ".txt", ".js", ".csproj", ".ts", ".toml", ".xml", ".sig" };

    private static IEnumerable<string> RepoTextFiles()
    {
        if (Root == null) yield break;
        var stack = new Stack<string>(); stack.Push(Root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            foreach (var sub in Directory.GetDirectories(d))
                if (!SkipDirs.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) stack.Push(sub);
            foreach (var f in Directory.GetFiles(d))
                if (TextExt.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && new FileInfo(f).Length < 3_000_000) yield return f;
        }
    }

    [Theory]
    [InlineData("catalog/servers.json")]
    [InlineData("catalog/bypass.json")]
    public void Signed_catalogs_match_their_signature(string rel)
    {
        if (Root == null) return;
        var file = Path.Combine(Root, rel);
        var sig = File.ReadAllText(file + ".sig");
        // Подпись считается по тексту так же, как это делает программа при разборе
        Assert.True(Updater.VerifySignature(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(file)), sig, Updater.PublicKey),
            $"Подпись {rel}.sig не сходится с файлом: файл изменили без повторной подписи");
    }

    private static readonly (string Name, Regex Pattern)[] Secrets =
    {
        ("токен Telegram-бота", new Regex(@"\b\d{8,10}:AA[A-Za-z0-9_-]{33}\b")),
        ("закрытый ключ", new Regex(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----")),
        ("токен GitHub", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b|\bgithub_pat_[A-Za-z0-9_]{50,}\b")),
        ("ключ доступа AWS", new Regex(@"\bAKIA[0-9A-Z]{16}\b")),
        ("токен Cloudflare в заголовке", new Regex(@"(?i)authorization:\s*bearer\s+[A-Za-z0-9_-]{30,}")),
    };

    [Fact]
    public void No_secrets_in_text_files()
    {
        var hits = new List<string>();
        foreach (var f in RepoTextFiles())
        {
            string text;
            try { text = File.ReadAllText(f); } catch (IOException) { continue; }
            foreach (var (name, rx) in Secrets)
                if (rx.IsMatch(text)) hits.Add($"{Path.GetRelativePath(Root!, f)}: {name}");
        }
        Assert.True(hits.Count == 0, "В репозитории найдены секреты: " + string.Join("; ", hits));
    }

    [Fact]
    public void Secret_patterns_recognise_samples()
    {
        // Образцы собираются из частей: цельный токен в тексте теста сам сработал бы как находка
        var samples = new[]
        {
            "123456789:AA" + new string('x', 33),
            "-----BEGIN " + "EC PRIVATE KEY-----",
            "ghp" + "_" + new string('a', 36),
            "AKIA" + new string('A', 16),
        };
        foreach (var sample in samples)
            Assert.Contains(Secrets, s => s.Pattern.IsMatch(sample));
        Assert.DoesNotContain(Secrets, s => s.Pattern.IsMatch("https://chimerion.dev 1.1.1.1 обычный текст"));
    }

    [Fact]
    public void No_key_or_session_files_in_repository()
    {
        if (Root == null) return;
        var bad = new List<string>();
        foreach (var dir in new[] { Root })
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(Root, f);
                if (rel.Split(Path.DirectorySeparatorChar).Any(p => SkipDirs.Contains(p, StringComparer.OrdinalIgnoreCase))) continue;
                var name = Path.GetFileName(f).ToLowerInvariant();
                if (name.EndsWith(".pem") || name.EndsWith(".pfx") || name.EndsWith(".p12") || name.EndsWith(".session") ||
                    name.StartsWith("crowd-admin") || name.StartsWith("update-key") || name == "keystore.properties")
                    bad.Add(rel);
            }
        Assert.True(bad.Count == 0, "Файлы с ключами или сеансами не должны лежать в репозитории: " + string.Join(", ", bad));
    }

    [Theory]
    [InlineData("CHANGELOG.md")]
    [InlineData("README.md")]
    [InlineData("docs/advantages.md")]
    [InlineData("site/index.html")]
    public void Texts_have_no_yo_and_no_long_dashes(string rel)
    {
        if (Root == null) return;
        var path = Path.Combine(Root, rel);
        if (!File.Exists(path)) return;
        var lines = File.ReadAllLines(path);
        var bad = new List<string>();
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].IndexOfAny(new[] { 'ё', 'Ё', '—', '–' }) >= 0) bad.Add($"{rel}:{i + 1}");
        Assert.True(bad.Count == 0, "Буква «ё» или длинное тире: " + string.Join(", ", bad.Take(10)));
    }

    [Fact]
    public void Changelog_top_version_matches_project_version()
    {
        if (Root == null) return;
        var csproj = File.ReadAllText(Path.Combine(Root, "CHIMERION.csproj"));
        var version = Regex.Match(csproj, @"<Version>([\d.]+)</Version>").Groups[1].Value;
        var top = File.ReadLines(Path.Combine(Root, "CHANGELOG.md")).First(l => l.StartsWith("## "));
        Assert.StartsWith("## " + version, top);
    }
}
