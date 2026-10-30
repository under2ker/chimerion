using System.IO;
using Dnex.Models;
using Dnex.Services;
using Microsoft.Win32;

namespace Dnex;

/// <summary>Перенос настроек и избранных серверов на другой компьютер: «Настройки» → «Данные».</summary>
public partial class MainWindow
{
    private void ExportSettings()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"chimerion-settings-{DateTime.Now:yyyy-MM-dd}.json", Filter = "Настройки CHIMERION (*.json)|*.json", DefaultExt = ".json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var favorites = _servers.Count(s => s.IsFavorite);
            File.WriteAllText(dlg.FileName, SettingsTransfer.Export(_settings, _servers), new System.Text.UTF8Encoding(false));
            Log($"Настройки экспортированы: избранных серверов {favorites}");
            ShowToast($"Настройки сохранены: {Path.GetFileName(dlg.FileName)}, избранных серверов {favorites}");
        }
        catch (Exception e) { ShowToast("Не удалось сохранить файл: " + e.Message, "Warn"); }
    }

    private void ImportSettings()
    {
        var dlg = new OpenFileDialog { Filter = "Настройки CHIMERION (*.json)|*.json|Все файлы|*.*", Title = "Файл настроек CHIMERION" };
        if (dlg.ShowDialog(this) != true) return;
        string text;
        try
        {
            if (new FileInfo(dlg.FileName).Length > SettingsTransfer.MaxBytes) { ShowToast("Файл слишком большой для файла настроек CHIMERION", "Warn"); return; }
            text = File.ReadAllText(dlg.FileName);
        }
        catch (Exception e) { ShowToast("Не удалось прочитать файл: " + e.Message, "Warn"); return; }

        var (plan, error) = SettingsTransfer.Parse(text);
        if (plan == null) { Log("Импорт настроек: " + error); ShowToast(error!, "Warn"); return; }

        // Что уже есть в списке (по ключу сервера), а что придется добавить
        var byKey = _servers.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        static string KeyOf(PortableServer f) => !string.IsNullOrEmpty(f.Primary) ? f.Primary : (f.DohUrl ?? f.Name).ToLowerInvariant();
        var toMark = plan.Favorites.Where(f => byKey.TryGetValue(KeyOf(f), out var s) && !s.IsFavorite).ToList();
        var toAdd = plan.Favorites.Where(f => !byKey.ContainsKey(KeyOf(f))).ToList();
        var lines = new List<string> { $"Настроек будет применено: {plan.SettingsCount}", $"Избранных: отметить уже известных {toMark.Count}, добавить новых {toAdd.Count}" };
        if (plan.Skipped.Count > 0) lines.Add("Пропущено: " + string.Join("; ", plan.Skipped.Take(4)));
        lines.Add("Cookie, источники поиска, «Два DNS» и согласие на общую карту не меняются. Импорт можно отменить кнопкой «Отменить».");
        if (!DnexDialog.Ask("Импортировать настройки?", string.Join("\n", lines), "Импортировать", "Отмена", DialogKind.Info, false, "Импорт")) return;

        var before = System.Text.Json.JsonSerializer.Serialize(_settings, Storage.Options);
        var marked = toMark.Select(f => byKey[KeyOf(f)]).ToList();
        var added = new List<DnsServer>();
        SettingsTransfer.Apply(plan.Settings, _settings);
        Storage.Save("settings.json", _settings);
        _settings = LoadSettings();
        ApplyImportedLook();
        foreach (var s in marked) s.IsFavorite = true;
        foreach (var f in toAdd)
        {
            var s = new DnsServer { Name = f.Name, Primary = f.Primary ?? "", Secondary = f.Secondary, DohUrl = f.DohUrl, Source = "импорт настроек", IsFavorite = true };
            _servers.Add(s); added.Add(s);
        }
        SaveServers();
        _view?.Refresh();
        UpdateTopList();
        Log($"Импорт настроек: применено {plan.SettingsCount}, избранных отмечено {marked.Count}, добавлено {added.Count}");
        OfferUndo("Настройки импортированы", () =>
        {
            Storage.SaveJson("settings.json", before);
            _settings = LoadSettings();
            ApplyImportedLook();
            foreach (var s in marked) s.IsFavorite = false;
            foreach (var s in added) _servers.Remove(s);
            SaveServers();
            _view?.Refresh();
            UpdateTopList();
            Log("Импорт настроек отменен");
            return Task.CompletedTask;
        });
    }

    /// <summary>Тема, цвета, язык, режим скрытия данных и интервалы из только что записанных настроек.</summary>
    private void ApplyImportedLook()
    {
        ThemeColors.Apply(_settings.Colors, _settings.Theme);
        PrivacyGuard.Set(_settings.PrivacyMode, _settings.PrivacyHideWindows);
        Loc.SetLanguage(_settings.Language);
        ApplyTimerSettings();
        ApplyCountryPin();
    }
}
