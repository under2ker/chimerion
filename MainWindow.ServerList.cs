using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Dnex.Models;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>Таблица серверов: фильтр, выбор, избранное, правка списка, порядок, места и сохранение</summary>
public partial class MainWindow
{
    private int _filterIndex = 5; // «Рабочие»
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string _dotMode = "";           // режим точки-индикатора: busy / on / idle
    private bool _locating, _locatingAgain; // идет определение локаций серверов


    private bool FilterServer(object o)
    {
        if (o is not DnsServer s) return false;
        var q = SearchBox?.Text.Trim() ?? "";
        if (q.Length > 0)
        {
            bool Has(string? v) => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
            if (!(Has(s.Name) || Has(s.Primary) || Has(s.Secondary) || Has(s.DohUrl) || Has(s.Source) || Has(s.Summary)))
                return false;
        }
        return _filterIndex switch
        {
            1 => s.Status == ServerStatus.Ok,
            2 => s.Status == ServerStatus.Partial,
            3 => s.Status == ServerStatus.Fail,
            4 => s.Status == ServerStatus.Unknown,
            5 => s.Status is ServerStatus.Ok or ServerStatus.Partial or ServerStatus.Checking && AccountSafety.Fits(s),
            6 => s.IsFavorite,
            _ => true,
        };
    }

    // Поиск с задержкой 250 мс: список не пересчитывается на каждую букву.
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void FilterTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && int.TryParse(t, out var i)) _filterIndex = i;
        Filter_Changed(sender, e);
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSwitchButton();
        var sel = ServerGrid.SelectedItems.Cast<DnsServer>().ToList();
        Brush B(string k) => (Brush)FindResource(k);
        SelectionSource.Visibility = Visibility.Collapsed;
        if (sel.Count == 0)
        {
            SelectionIcon.Fill = B("Idle");
            SelectionTitle.Text = "Сервер не выбран";
            SelectionDetails.Text = "Щелкните строку, чтобы выбрать. Двойной щелчок подключает к серверу.";
            return;
        }
        if (sel.Count > 1)
        {
            SelectionIcon.Fill = B("Accent");
            SelectionTitle.Text = $"Выбрано: {sel.Count}";
            SelectionDetails.Text = "Можно проверить или удалить выбранные";
            return;
        }
        var s = sel[0];
        SelectionIcon.Fill = B(s.Status switch { ServerStatus.Ok => "Ok", ServerStatus.Partial => "Warn", ServerStatus.Fail => "Bad", _ => "Accent" });
        // Название как в таблице (страна и город или имя из каталога), а не сайт, где сервер найден
        SelectionTitle.Text = s.ShortName;
        SelectionTitle.ToolTip = s.Name;
        ShowSelectionSource(s);
        var parts = new List<string> { s.Address };
        if (!string.IsNullOrEmpty(s.DohUrl) && !string.IsNullOrEmpty(s.Primary)) parts.Add(s.DohUrl!);
        parts.Add(s.StatusText.ToLowerInvariant());
        if (s.LatencyMs != null) parts.Add($"пинг {s.PingText} мс");
        if (s.ChecksTotal > 0) parts.Add($"стабильность {s.Stability}");
        SelectionDetails.Text = string.Join("   ·   ", parts);
    }

    /// <summary>
    /// Источник выбранного сервера «хлебными крошками»: «источник  github.com › MAECTRO › magicdns › commits › main».
    /// Сайт выделен цветом, путь светлее, разделители приглушены. Полная ссылка в подсказке, щелчок открывает страницу.
    /// </summary>
    private void ShowSelectionSource(DnsServer s)
    {
        Brush B(string k) => (Brush)FindResource(k);
        SelectionSource.Inlines.Clear();
        var parts = s.HasSourceUrl ? SourceLabel.Parts(s.SourceUrl) : new List<string>();
        SelectionSource.Inlines.Add(new System.Windows.Documents.Run("источник  ") { Foreground = B("Muted") });
        if (parts.Count == 0)
        {
            SelectionSource.Inlines.Add(new System.Windows.Documents.Run(s.Source) { Foreground = B("Text") });
            SelectionSource.Cursor = null;
            SelectionSource.ToolTip = null;
        }
        else
        {
            SelectionSource.Inlines.Add(new System.Windows.Documents.Run(parts[0]) { Foreground = B("Accent") });
            foreach (var part in parts.Skip(1))
            {
                SelectionSource.Inlines.Add(new System.Windows.Documents.Run("  ›  ") { Foreground = B("Muted") });
                SelectionSource.Inlines.Add(new System.Windows.Documents.Run(part) { Foreground = B("Text") });
            }
            SelectionSource.Cursor = Cursors.Hand;
            SelectionSource.ToolTip = $"{s.SourceUrl}\nЩелчок открывает страницу, где найден сервер";
        }
        SelectionSource.Tag = s;
        SelectionSource.Visibility = Visibility.Visible;
    }

    private void SelectionSource_Click(object sender, MouseButtonEventArgs e)
    {
        if (SelectionSource.Tag is DnsServer { HasSourceUrl: true } s)
            AutoStart.OpenUrl(new Uri(s.SourceUrl!).AbsoluteUri); // только http/https — проверено в HasSourceUrl
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Двойной клик по нередактируемой колонке (статус, сервисы и т.п.) = применить.
        // Только по строке (не по заголовку и не по кнопке «Открыть»).
        if (e.OriginalSource is DependencyObject d &&
            ItemsControl.ContainerFromElement(ServerGrid, d) is DataGridRow { Item: DnsServer } &&
            FindParent<Button>(d) == null)
            Connect_Click(sender, e);
    }

    private void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Connect_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { Delete_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { MenuCopy_Click(sender, e); e.Handled = true; }
    }

    /// <summary>Звездочка в строке: добавить сервер в избранное или убрать.</summary>
    private void Fav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DnsServer s }) ToggleFavorite(new[] { s });
        e.Handled = true;
    }

    private static string Names(IReadOnlyList<DnsServer> list) =>
        list.Count <= 3 ? string.Join(", ", list.Select(s => s.DisplayName)) : $"{list.Count} серверов";

    private void MenuFav_Click(object sender, RoutedEventArgs e) =>
        ToggleFavorite(ServerGrid.SelectedItems.Cast<DnsServer>().ToList());

    /// <summary>Если среди выбранных есть не избранные — все добавляются, иначе все убираются.</summary>
    private void ToggleFavorite(IReadOnlyList<DnsServer> list)
    {
        if (list.Count == 0) return;
        var was = list.Select(s => (s, s.IsFavorite)).ToList();
        bool add = list.Any(s => !s.IsFavorite);
        foreach (var s in list) s.IsFavorite = add;
        OfferUndo(add ? $"В избранное: {Names(list)}" : $"Убрано из избранного: {Names(list)}", () =>
        {
            foreach (var (srv, fav) in was) srv.IsFavorite = fav;
            SaveServers();
            _view?.Refresh();
            Log("Отменено изменение избранного");
            return Task.CompletedTask;
        });
        Log(add ? $"В избранное: {string.Join(", ", list.Select(s => s.DisplayName))}"
                : $"Убрано из избранного: {string.Join(", ", list.Select(s => s.DisplayName))}");
        SaveServers();
        _view?.Refresh(); // избранные сразу поднимаются наверх
        UpdateStats();
    }

    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        var picked = ServerGrid.SelectedItems.Cast<DnsServer>().ToList();
        MenuFav.Header = picked.Count > 0 && picked.All(s => s.IsFavorite) ? "Убрать из избранного" : "В избранное";
        var s = ServerGrid.SelectedItem as DnsServer;
        bool isActive = s != null && DnsSwitcher.GetActive()?.ServerId == s.Id;
        MenuConnect.Header = DnsSwitcher.GetActive() == null ? "Подключить" : "Переключить сюда";
        MenuConnect.IsEnabled = s != null && !isActive;
        MenuSource.IsEnabled = s?.HasSourceUrl == true;
    }

    private void MenuConnect_Click(object sender, RoutedEventArgs e) => Connect_Click(sender, e);

    private void MenuSource_Click(object sender, RoutedEventArgs e)
    {
        if (ServerGrid.SelectedItem is DnsServer { HasSourceUrl: true } s)
            AutoStart.OpenUrl(new Uri(s.SourceUrl!).AbsoluteUri);
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, ServerGrid.SelectedItems.Cast<DnsServer>()
            .Select(s => string.Join(" ", new[] { s.Primary, s.Secondary, s.DohUrl }.Where(x => !string.IsNullOrWhiteSpace(x)))));
        if (text.Length > 0) { if (Ui.CopyText(text)) Log("Адрес скопирован"); else ShowToast("Не удалось скопировать: буфер обмена занят другой программой", "Warn"); }
    }


    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var primary = AddPrimary.Text.Trim();
        var secondary = AddSecondary.Text.Trim();
        var doh = AddDoh.Text.Trim();

        bool hasIp = IPAddress.TryParse(primary, out _);
        bool hasDoh = Uri.TryCreate(doh, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
        // Ошибки ввода видны сразу (всплывающее сообщение), а не только в журнале
        void Fail(string text, Control field) { Log(text); ShowToast(text, "Warn"); field.Focus(); }
        if (!hasIp && !hasDoh)
        {
            Fail(primary.Length == 0 && doh.Length == 0 ? "Укажите основной IP (например 203.0.113.10) или адрес DoH"
                 : primary.Length > 0 ? $"«{primary}» не похож на IP-адрес: нужен вид 203.0.113.10, без порта и запятых"
                 : "Адрес DoH должен начинаться с https://", primary.Length > 0 || doh.Length == 0 ? AddPrimary : AddDoh);
            return;
        }
        if (secondary.Length > 0 && !IPAddress.TryParse(secondary, out _)) { Fail($"«{secondary}» не похож на IP-адрес", AddSecondary); return; }

        var s = new DnsServer
        {
            Name = string.IsNullOrWhiteSpace(AddName.Text) ? (hasIp ? primary : u!.Host) : AddName.Text.Trim(),
            Primary = hasIp ? primary : "",
            Secondary = secondary.Length > 0 ? secondary : null,
            DohUrl = hasDoh ? doh : null,
            Manual = true,
        };
        if (_servers.FirstOrDefault(x => x.Key == s.Key) is { } existing)
        {
            // Уже в списке: показываем его, а не молчим
            ShowToast($"Такой сервер уже есть: {existing.DisplayName}", "Warn");
            Log($"Такой сервер уже есть: {existing.DisplayName}");
            existing.Manual = true;
            SaveServers();
            AllTab.IsChecked = true;
            SearchBox.Clear();
            _view?.Refresh();
            ServerGrid.SelectedItem = existing;
            ServerGrid.ScrollIntoView(existing);
            return;
        }

        // Удаленный раньше или убранный автоочисткой: раз добавлен вручную, больше не считается мусором
        if (_junk.Remove(s.Key)) try { Storage.Save(JunkFile, _junk); } catch (Exception) { }
        _known.Add(s.Key);
        s.JustAdded = true;
        _servers.Add(s);
        _ = ClearJustAddedLaterAsync(s);
        SaveServers();
        Log($"Добавлен сервер: {s.DisplayName}");
        OfferUndo($"Добавлен сервер {s.DisplayName}", () =>
        {
            if (DnsSwitcher.GetActive()?.ServerId == s.Id) { Log("Отмена не выполнена: к этому серверу уже подключено"); return Task.CompletedTask; }
            _servers.Remove(s);
            _known.Remove(s.Key);
            SaveServers();
            UpdateStats();
            Log($"Добавление отменено: {s.DisplayName}");
            return Task.CompletedTask;
        });
        AddName.Clear(); AddPrimary.Clear(); AddSecondary.Clear(); AddDoh.Clear();
        // Новый сервер еще не проверен, фильтр «Рабочие» его скрыл бы: показываем все и выбираем добавленный
        AllTab.IsChecked = true;
        SearchBox.Clear();
        _view?.Refresh();
        ServerGrid.SelectedItem = s;
        ServerGrid.ScrollIntoView(s);
        Log($"Добавлен «{s.Name}», проверяю");
        ShowToast($"Сервер «{s.Name}» добавлен, проверяю");
        _ = RunChecksAsync(new List<DnsServer> { s });
        _ = ResolveLocationsAsync();
    }

    /// <summary>Подключенный сервер всегда первый в таблице, какой бы колонкой ни была отсортирована.</summary>
    /// <summary>Сверху всегда подключенный сервер, под ним избранные (в порядке «лучшие для сервиса» избранные не поднимаются,
    /// чтобы наверху были лучшие именно для него); дальше порядок выбранной колонки.</summary>
    private void PinConnectedFirst()
    {
        if (_view == null) return;
        var sd = _view.SortDescriptions;
        bool favPinned = _rankFor == null;
        bool ok = sd.Count > 0 && sd[0].PropertyName == nameof(DnsServer.IsConnected)
                  && (favPinned ? sd.Count > 1 && sd[1].PropertyName == nameof(DnsServer.IsFavorite)
                                : !sd.Any(d => d.PropertyName == nameof(DnsServer.IsFavorite)));
        if (ok) return;
        for (int i = sd.Count - 1; i >= 0; i--)
            if (sd[i].PropertyName is nameof(DnsServer.IsConnected) or nameof(DnsServer.IsFavorite)) sd.RemoveAt(i);
        if (favPinned) sd.Insert(0, new SortDescription(nameof(DnsServer.IsFavorite), ListSortDirection.Descending));
        sd.Insert(0, new SortDescription(nameof(DnsServer.IsConnected), ListSortDirection.Descending));
    }


    private string? _rankFor;

    /// <summary>
    /// Выстраивает список по одному сервису (после проверки только его) или возвращает порядок для всех (null).
    /// Наверху серверы, где сервис работает, по стабильности именно по нему, при равенстве по пингу; ниже остальные.
    /// Плашка у вкладок и приглушенные плашки других сервисов показывают, по какому сервису выстроен список.
    /// </summary>
    private void SetRankFocus(string? service)
    {
        _rankFor = service;
        ServiceChip.Focus = service;
        ApplyFocusRanks();
        foreach (var s in _servers) s.RefreshChips();

        if (_view != null)
        {
            var sd = _view.SortDescriptions;
            sd.Clear();
            sd.Add(new SortDescription(nameof(DnsServer.IsConnected), ListSortDirection.Descending));
            if (service == null)
            {
                sd.Add(new SortDescription(nameof(DnsServer.IsFavorite), ListSortDirection.Descending));
                sd.Add(new SortDescription(nameof(DnsServer.Rank), ListSortDirection.Ascending));
                sd.Add(new SortDescription(nameof(DnsServer.LatencyMs), ListSortDirection.Ascending));
            }
            else sd.Add(new SortDescription(nameof(DnsServer.FocusRank), ListSortDirection.Ascending));
        }

        RankChip.Visibility = service == null ? Visibility.Collapsed : Visibility.Visible;
        ColStab.Header = service == null ? "СТАБИЛЬНОСТЬ" : $"СТАБ. {service.ToUpperInvariant()}"; // колонка показывает стабильность по сервису
        if (service != null)
        {
            RankChipText.Text = $"Сначала лучшие для {service}";
            RankChipLogo.Source = AiLogo(service);
            Log($"Список выстроен по {service}: наверху серверы, где {service} работает стабильнее всего");
        }
        _view?.Refresh();
    }

    /// <summary>Места в порядке «лучшие для сервиса». Считаются заново после каждой проверки.</summary>
    private void ApplyFocusRanks()
    {
        if (_rankFor is not { } svc) return;
        foreach (var s in _servers)
        {
            bool works = s.Chips.Any(c => c.Name == svc && c.State == "ok");
            int stab = s.ServiceStability(svc) ?? (s.ChecksTotal > 0 ? s.ChecksOk * 100 / s.ChecksTotal : 0);
            int ping = (int)Math.Min(s.LatencyMs ?? 9999, 9999);
            s.FocusRank = works ? (100 - stab) * 10_000 + ping : 2_000_000 + s.Rank;
        }
    }

    private void RankChipClear_Click(object sender, RoutedEventArgs e) => SetRankFocus(null);

    /// <summary>
    /// Клик без Ctrl и Shift выбирает одну строку. Протаскивание мышью список не выделяет:
    /// событие перехватывается до того, как таблица начнет выделение диапазоном.
    /// Кнопки в строке (обновить, открыть источник) работают как обычно.
    /// </summary>
    private void ServerGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.ClickCount > 1) return;
        if (FindParent<Button>(e.OriginalSource as DependencyObject) != null) return;
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not DnsServer s) return;
        e.Handled = true;
        ServerGrid.SelectedItems.Clear();
        ServerGrid.SelectedItem = s;
        ServerGrid.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var sel = ServerGrid.SelectedItems.Cast<DnsServer>().ToList();
        // Подключенный сервер не удаляется: DNS на нем работает, а без строки в списке сторож и проверка сервисов
        // его не находили, и после перезапуска программа сама переключалась на другой
        var activeId = DnsSwitcher.GetActive()?.ServerId;
        if (sel.RemoveAll(s => s.Id == activeId) > 0)
            ShowToast("Подключенный сервер не удаляется: сначала отключитесь или переключитесь на другой", "Warn");
        if (sel.Count == 0) return;
        if (sel.Count > 1 && !DnexDialog.Ask($"Удалить {sel.Count} {ServersWord(sel.Count)}?", "Удаление можно отменить кнопкой «Отменить» внизу окна.",
                "Удалить", danger: true, windowTitle: "Удаление")) return;
        // Для отмены: место в списке и прежняя отметка в мусоре (удаленный сервер поиск больше не добавляет)
        var removed = sel.Select(s => (Server: s, Index: _servers.IndexOf(s),
                                       Junk: _junk.TryGetValue(s.Key, out var t) ? t : (DateTime?)null))
                         .OrderBy(r => r.Index).ToList();
        foreach (var s in sel) { _servers.Remove(s); _known.Remove(s.Key); _junk[s.Key] = DateTime.Now; }
        try { Storage.Save(JunkFile, _junk); } catch (Exception) { }
        SaveServers();
        Log($"Удалено: {Names(sel)}");
        OfferUndo(sel.Count == 1 ? $"Удален сервер {sel[0].DisplayName}" : $"Удалено серверов: {sel.Count}", () =>
        {
            foreach (var r in removed)
            {
                if (_servers.Contains(r.Server)) continue;
                _servers.Insert(Math.Clamp(r.Index, 0, _servers.Count), r.Server);
                _known.Add(r.Server.Key);
                if (r.Junk is { } t) _junk[r.Server.Key] = t; else _junk.Remove(r.Server.Key);
            }
            try { Storage.Save(JunkFile, _junk); } catch (Exception) { }
            SaveServers();
            UpdateStats();
            Log($"Удаление отменено: {Names(removed.Select(r => r.Server).ToList())}");
            return Task.CompletedTask;
        });
    }

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.InvokeAsync(SaveServers);

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DnsServer { HasSourceUrl: true } s)
            AutoStart.OpenUrl(new Uri(s.SourceUrl!).AbsoluteUri); // только http/https — проверено в HasSourceUrl
    }


    /// <summary>Определяет страну и город серверов без данных или с устаревшими данными (старше 30 дней).</summary>
    private async Task ResolveLocationsAsync()
    {
        if (_locating) { _locatingAgain = true; return; } // уже идет — повторим после окончания
        _locating = true;
        try
        {
            do
            {
                _locatingAgain = false;
                await ResolveLocationsPassAsync();
            } while (_locatingAgain);
        }
        finally { _locating = false; }
    }

    private async Task ResolveLocationsPassAsync()
    {
        // Флаги уже известных стран берем с диска, без сети
        foreach (var s in _servers.ToList())
            if (s.Flag == null && !string.IsNullOrEmpty(s.Country) && GeoLocation.FlagPath(s.Country) is { } fp)
                s.Flag = LoadFlag(fp);

        // Нет данных, данные старше 30 дней или вторая база еще не сверяла (повторим при следующем запуске)
        var todo = _servers.Where(s => s.GeoCheckedAt == null || DateTime.Now - s.GeoCheckedAt > TimeSpan.FromDays(30)
                                       || (s.Country != null && s.Country2 == null)
                                       || (s.Country != null && s.GeoVotes == null)).ToList(); // данные до сверки тремя базами
        // Прокси Smart DNS, которому еще не определили страну (адрес появляется после проверки)
        var proxyTodo = _servers.Where(s => !string.IsNullOrEmpty(s.ProxyIp) && s.ProxyCountry == null).ToList();
        // Город выхода (страну дает сам trace Cloudflare)
        var egressTodo = _servers.SelectMany(s => s.EgressBy.Values.Where(e => !s.EgressCityByIp.ContainsKey(e.Ip)).Select(e => (s, e.Ip)))
                                 .ToList();
        var egressCities = new System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>>();

        await Task.WhenAll(todo.Select(ResolveOneAsync).Concat(proxyTodo.Select(ResolveProxyAsync))
            .Concat(egressTodo.Select(async x =>
            {
                // Один адрес выхода у многих серверов: город спрашиваем один раз
                var city = await egressCities.GetOrAdd(x.Ip, async ip => (await GeoLocation.LookupAsync(ip, CancellationToken.None)).City ?? "");
                x.s.EgressCityByIp[x.Ip] = city;
                x.s.NotifyEgress();
            })));
        if (todo.Count > 0 || proxyTodo.Count > 0 || egressTodo.Count > 0) SaveServers();   // страны должны пережить перезапуск
        UpdateConnectionLocation();
        UpdateTopList(); // флаги и страны появились: блок «Лучшие по стабильности» перерисуется с ними
    }

    /// <summary>Страна адреса прокси Smart DNS — той локации, через которую на самом деле идет доступ к ИИ.</summary>
    private async Task ResolveProxyAsync(DnsServer s)
    {
        var ip = s.ProxyIp!;
        if (!GeoLocation.IsPublicIp(ip)) { s.SetProxyLocation(null, null); return; }
        var r = await GeoLocation.ResolveAsync(ip, CancellationToken.None);
        if (r == null) return;                                     // сеть или лимит — попробуем позже
        s.SetProxyLocation(r.Country, r.Second);
    }

    private async Task ResolveOneAsync(DnsServer s)
    {
        var ip = s.Primary;
        if (string.IsNullOrEmpty(ip) && Uri.TryCreate(s.DohUrl, UriKind.Absolute, out var u))
            ip = await HostIpAsync(u.Host);
        if (string.IsNullOrEmpty(ip)) return;                        // адрес не узнали — попробуем в следующий раз
        if (!GeoLocation.IsPublicIp(ip)) { s.SetLocation(null, null); return; }

        var r = await GeoLocation.ResolveAsync(ip, CancellationToken.None);
        if (r?.Country == null) return;                              // сеть или лимит сервиса — попробуем позже
        s.SetLocation(r);
        if (await GeoLocation.FlagPathAsync(r.Country, CancellationToken.None) is { } path)
            s.Flag = LoadFlag(path);
    }

    /// <summary>IPv4 хоста для геолокации. Сначала через эталонный DoH: системный DNS может быть подменен
    /// подключенным Smart DNS или провайдером. Если эталон недоступен, через системный.</summary>
    private async Task<string?> HostIpAsync(string host)
    {
        try
        {
            var (ips, _) = await DnsWire.ResolveDohAsync(_settings.ReferenceDoh, host, TimeSpan.FromSeconds(6), CancellationToken.None);
            if (ips.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) is { } ip) return ip.ToString();
        }
        catch (Exception) { }
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host);
            return addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
        }
        catch (Exception) { return null; }
    }

    private static ImageSource? LoadFlag(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (FlagCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            FlagCache[path] = img;
            return img;
        }
        catch (Exception) { return null; }
    }

    private void AddToggle_Changed(object sender, RoutedEventArgs e) => Reveal(AddPanel, AddToggle.IsChecked == true);

    /// <summary>Раскрытие блока: появляется с легким сдвигом; при сворачивании гаснет и выпадает из верстки.</summary>
    private static void Reveal(FrameworkElement el, bool show)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (show)
        {
            el.Visibility = Visibility.Visible;
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion(220)) { EasingFunction = ease });
            if (el.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, Motion(220)) { EasingFunction = ease });
        }
        else
        {
            // Если блок снова открыли до конца анимации, Completed не сработает — блок останется видимым.
            var fade = new DoubleAnimation(1, 0, Motion(160)) { EasingFunction = ease };
            fade.Completed += (_, _) => el.Visibility = Visibility.Collapsed;
            el.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    /// <summary>Новая строка проявляется один раз. Через секунду флаг сбрасывается, чтобы при пересортировке строки не мигали.</summary>
    private static async Task ClearJustAddedLaterAsync(DnsServer s)
    {
        await Task.Delay(1000);
        s.JustAdded = false;
    }

    /// <summary>Строки плавно гаснут, потом исчезают из списка.</summary>
    private async Task FadeOutAndRemoveAsync(IEnumerable<DnsServer> items)
    {
        var list = items.ToList();
        // Большая пачка (сотни строк): без анимации и одним пакетом. Удаление по одной с пересортировкой таблицы
        // на каждой строке подвешивало программу на минуты (930 строк в прогоне)
        if (list.Count > 50)
        {
            using (_view?.DeferRefresh()) foreach (var s in list) _servers.Remove(s);
            SaveServers();
            return;
        }
        foreach (var s in list) s.Fading = true;
        await Task.Delay(320);
        foreach (var s in list) if (s.Fading) _servers.Remove(s); // отмена успела вернуть строку: ее не трогаем
        SaveServers();
    }

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _savePending;

    /// <summary>Сохранение в фоне: снимок списка берется в UI-потоке, запись на диск — вне его.
    /// Частые вызовы схлопываются в одну запись.</summary>
    private string? _lastServersJson;

    private async void SaveServers()
    {
        if (_savePending) return;
        _savePending = true;
        await Task.Delay(300);
        _savePending = false;
        // Сериализация в UI-потоке: серверы меняются только в нем, поэтому фоновый поток получает готовую строку,
        // а не живые объекты, которые в этот момент могут меняться (иначе «Collection was modified»)
        string json;
        try { json = System.Text.Json.JsonSerializer.Serialize(_servers.ToList(), Storage.Options); }
        catch (Exception ex) { Log("Не удалось сохранить список: " + ex.Message); return; }
        // ConfigureAwait(false): продолжение не ждет UI-поток. Иначе SaveServersNow при закрытии окна
        // держит UI-поток в Wait() на замке, который отпускается только в этом продолжении — взаимная блокировка.
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (json != _lastServersJson) // содержимое не менялось: на диск не пишем
            {
                await Task.Run(() => Storage.SaveJson(ServersFile, json)).ConfigureAwait(false);
                _lastServersJson = json;
            }
        }
        catch (Exception ex) { Log("Не удалось сохранить список: " + ex.Message); }
        finally { _saveLock.Release(); }
    }

    private void SaveServersNow()
    {
        _saveLock.Wait();
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_servers.ToList(), Storage.Options);
            if (json != _lastServersJson) { Storage.SaveJson(ServersFile, json); _lastServersJson = json; }
        }
        catch (Exception) { }
        finally { _saveLock.Release(); }
    }

    /// <summary>Enter в полях ручного добавления равен кнопке «Добавить».</summary>
    private void AddPanel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Add_Click(sender, e);
    }
}
