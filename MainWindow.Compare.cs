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

/// <summary>Проверка подключения: сравнение «до» и «после», карточки сервисов и их адреса</summary>
public partial class MainWindow
{
    private NetSnapshot? _before, _after;

    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        ProbeButton.IsEnabled = false;
        VerdictText.Text = "проверяю…";
        try
        {
            var now = await ConnectionProbe.TakeAsync(_settings, leakTest: true);
            if (_after == null && _before == null) _before = now; else _after = now;
            ShowCompare();
            SetTraySnapshot(now);
        }
        catch (Exception ex) { Log("Ошибка проверки подключения: " + ex.Message); }
        finally { ProbeButton.IsEnabled = true; }
    }

    private void CompareToggle_Click(object sender, RoutedEventArgs e)
    {
        CompareGrid.Visibility = CompareGrid.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        CompareToggle.Content = CompareGrid.Visibility == Visibility.Visible ? "Скрыть" : "Показать";
    }

    private void LogSnapshot(string title, NetSnapshot n)
    {
        Log($"{title}: IP {n.PublicIp} ({n.Location}, {n.Provider}), DNS {n.SystemDns}");
        // Без подключения адрес, не совпавший с эталоном, — это обычно CDN сервиса, а не Smart DNS: так и пишем
        bool connected = DnsSwitcher.GetActive() != null;
        string Route(bool proxied) => !proxied ? "(напрямую)" : connected ? "(через Smart DNS)" : "(адрес не совпал с эталоном)";
        foreach (var x in n.Services)
            Log($"  {x.Name}: {x.Host} → {x.ResolvedIp} {Route(x.Proxied)}, {x.Access}{(x.Note != null ? ". " + x.Note : "")}");
        foreach (var x in n.Hosts)
            Log($"  доп. {x.Name} → {x.ResolvedIp} {Route(x.Proxied)}, {x.Access}");
        foreach (var r in n.Resolvers)
            Log($"  резолвер: {r.Ip} · {r.Org} · {r.Place}");
        if (n.Egress is { } eg)
            Log($"  ИИ видят выход: {GeoLocation.CountryName(eg.Country)}{(string.IsNullOrEmpty(n.EgressCity) ? "" : ", " + n.EgressCity)} ({eg.Ip}{(string.IsNullOrWhiteSpace(n.EgressOrg) ? "" : ", " + n.EgressOrg)})");
        // В режиме «Два DNS» тестовый домен идет через общий DNS: разные резолверы там не утечка
        if (DnsSwitcher.GetActive() is { Split: false } && LeakTest.LooksLikeLeak(n.Resolvers))
            Log("  ⚠ Утечка DNS: запросы уходят к разным резолверам, часть идет мимо сервера");
    }

    /// <summary>
    /// Блок «До / После»: строка с внешним IP и DNS, ниже по плитке на каждый ИИ-сервис —
    /// крупно текущий доступ, мельче «было» и адрес, через который идет сервис.
    /// </summary>
    /// <summary>Логотип ИИ-сервиса по названию из настроек проверки. Null, если логотипа нет.</summary>
    private static ImageSource? AiLogo(string name) => name switch
    {
        "ChatGPT" => Application.Current.TryFindResource("LogoOpenAI") as ImageSource,
        "Claude" => Application.Current.TryFindResource("LogoClaude") as ImageSource,
        "Gemini" => Application.Current.TryFindResource("LogoGemini") as ImageSource,
        _ => null,
    };

    private string? _compareSig;
    /// <summary>Снимок, по которому построены плитки. Опоздавший результат поиска стран для старого снимка их не трогает.</summary>
    private NetSnapshot? _shownSnap;
    private readonly HashSet<string> _geoPending = new();
    /// <summary>Сервисы, у которых раскрыт список серверов. Сохраняется между перерисовками блока.</summary>
    private System.Windows.Controls.Primitives.Popup? _hostsPopup;
    private string? _hostsPopupFor;      // сервис, чьи адреса открыты
    private DateTime _hostsClosedAt;     // щелчок по той же кнопке закрывает панель, а не открывает заново
    private bool _compareDisconnected;

    private void ShowCompare(bool disconnected = false)
    {
        Brush B(string k) => (Brush)FindResource(k);
        var host = CompareGrid;
        // В компактном виде блок сам показывает последнюю проверку сервисов (фоновую или при подключении)
        var cur = _after ?? _before ?? (_compact ? _traySnap : null);
        if (cur != null)
        {
            // Пересобираем блок только при новом снимке: иначе плитки перерисовываются на каждое обновление
            var sig = $"{disconnected}|{cur.Taken:O}|{_before?.Taken:O}|{_after?.Taken:O}|{cur.SystemDns}|" +
                      string.Join(";", cur.Services.Select(x => $"{x.Name}{x.Access}{x.ResolvedIp}{x.Proxied}"));
            if (sig == _compareSig) return;
            _compareSig = sig;
        }
        host.Children.Clear();
        _tileGeo.Clear();
        _compareDisconnected = disconnected;
        _shownSnap = cur;
        if (_hostsPopup != null) _hostsPopup.IsOpen = false;
        if (cur == null)
        {
            host.Children.Add(new TextBlock
            {
                Text = _compact ? "Проверяю ИИ-сервисы…" : "Нажмите «Подключить», чтобы увидеть сравнение до и после.",
                Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap,
            });
            VerdictText.Text = "еще не проверялось";
            VerdictText.Foreground = B("Muted");
            return;
        }

        TextBlock Line(params (string text, Brush brush, bool bold)[] runs)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            foreach (var (t, br, bold) in runs)
                tb.Inlines.Add(new System.Windows.Documents.Run(t) { Foreground = br, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
            return tb;
        }

        // Строка 1: внешний IP (при Smart DNS не меняется — так и должно быть)
        if (!_compact) host.Children.Add(Line(
            ("Внешний IP  ", B("Muted"), false), (cur.PublicIp, B("Text"), true),
            ($"   ·   {cur.Location}   ·   {cur.Provider}", B("Muted"), false),
            (_before != null && _after != null && _before.PublicIp == _after.PublicIp ? "   ·   не меняется" : "", B("Muted"), false)));

        // Строка 2: DNS системы до → после
        var dnsRuns = new List<(string, Brush, bool)> { ("DNS системы  ", B("Muted"), false) };
        if (_before != null && _after != null && _before.SystemDns != _after.SystemDns)
        {
            dnsRuns.Add((_before.SystemDns, B("Muted"), false));
            dnsRuns.Add(("  →  ", B("Accent"), true));
            dnsRuns.Add((_after.SystemDns, B("Text"), true));
        }
        else dnsRuns.Add((cur.SystemDns, B("Text"), true));
        if (!_compact) host.Children.Add(Line(dnsRuns.ToArray()));

        // Откуда ИИ видят соединение, по каждому сервису. Если у сервисов разные страны — показываем каждую
        var perSvc = cur.Services.Where(x => x.Egress != null).ToList();
        if (!_compact && perSvc.Select(x => x.Egress!.Country).Distinct().Count() > 1)
        {
            host.Children.Add(Line(
                ("ИИ видят вас из  ", B("Muted"), false),
                (string.Join("   ·   ", perSvc.Select(x => $"{x.Name}: {GeoLocation.CountryName(x.Egress!.Country)}")), B("Text"), true)));
        }
        // Откуда ИИ видят соединение: адрес выхода в интернет. Может отличаться и от DNS-сервера, и от прокси
        else if (!_compact && (perSvc.FirstOrDefault()?.Egress ?? cur.Egress) is { } eg)
        {
            var org = LeakTest.CleanOrg(cur.EgressOrg ?? "");
            org = System.Text.RegularExpressions.Regex.Replace(org, @"^AS\d+\s*", "");
            host.Children.Add(Line(
                ("ИИ видят вас из  ", B("Muted"), false),
                (GeoLocation.CountryName(eg.Country) + (string.IsNullOrEmpty(cur.EgressCity) ? "" : ", " + GeoLocation.CityName(cur.EgressCity!)), B("Text"), true),
                ($"   ·   {eg.Ip}{(org.Length > 0 ? ", " + org : "")}", B("Muted"), false)));
        }

        // Строка 3: резолверы, которые на самом деле дошли до тестового сервера (тест утечки). Только при подключении:
        // без него сравнивать не с чем, а провайдер и так отвечает через несколько своих серверов
        if (!_compact && cur.Resolvers.Count > 0 && !disconnected && DnsSwitcher.GetActive() != null)
        {
            static string Country(string place) => place.Split(',').Last().Trim();
            // Один владелец — одна запись: адреса его серверов в подсказке
            var groups = cur.Resolvers.GroupBy(r => LeakTest.CleanOrg(r.Org), StringComparer.OrdinalIgnoreCase).ToList();
            var runs = new List<(string, Brush, bool)> { ("Резолверы  ", B("Muted"), false) };
            runs.Add((string.Join("   ·   ", groups.Select(g =>
            {
                var name = g.Key.Length <= 32 ? g.Key : g.Key[..31] + "…";
                var place = g.First().Place;
                return place.Length > 0 ? $"{name} ({Country(place)})" : name;
            })), B("Text"), true));
            bool leak = groups.Count > 1;
            if (DnsSwitcher.GetActive() is { Split: true })
                runs.Add(("   · это общий DNS, домены ИИ идут через сервер отдельно", B("Muted"), true));
            else
                runs.Add((leak ? "   ⚠ часть запросов идет мимо сервера" : "   ✓ все запросы через сервер", leak ? B("Warn") : B("Ok"), true));
            var line = Line(runs.ToArray());
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            line.TextWrapping = TextWrapping.NoWrap;
            line.ToolTip = string.Join("\n", cur.Resolvers.Select(r => $"{r.Ip} · {r.Org} · {r.Place}"));
            host.Children.Add(line);
        }

        // Плитки сервисов: статус доступа сверху, страна (флаг и название) под ним
        // Карточки в колонках, по верху: раскрытая карточка растет сама, соседние не вытягиваются
        var tiles = new Grid { Margin = new Thickness(-4, _compact ? 0 : 8, -4, 0), VerticalAlignment = VerticalAlignment.Top };
        foreach (var _ in cur.Services) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var svc in cur.Services)
        {
            var was = _after != null ? _before?.Services.FirstOrDefault(x => x.Name == svc.Name) : null;
            bool ok = svc.Access.StartsWith("✓");
            var card = new Border
            {
                Background = new SolidColorBrush(((SolidColorBrush)B("Surface2")).Color), CornerRadius = new CornerRadius(10), Padding = _compact ? new Thickness(8, 5, 8, 5) : new Thickness(14, 10, 14, 10),
                Margin = new Thickness(4, 0, 4, 0), BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = ok ? B("Ok") : B("Bad"),
            };
            var sp = new StackPanel();
            var head = new DockPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            int logoSize = _compact ? 15 : 18;
            if (AiLogo(svc.Name) is { } logo)
                title.Children.Add(new Image { Source = logo, Width = logoSize, Height = logoSize, Margin = new Thickness(0, 0, _compact ? 6 : 8, 0), Stretch = Stretch.Uniform });
            title.Children.Add(new TextBlock { Text = svc.Name, FontWeight = FontWeights.SemiBold, FontSize = _compact ? 13 : 14, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(title);
            var state = new TextBlock { Text = svc.Access.TrimStart('✓', '✗', ' '), Foreground = ok ? B("Ok") : B("Bad"),
                                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                                        FontSize = _compact ? 12 : 13, TextTrimming = TextTrimming.CharacterEllipsis,
                                        MaxWidth = _compact ? 120 : 150, Margin = new Thickness(_compact ? 0 : 8, 0, 0, 0),
                                        ToolTip = svc.Note };
            var geo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _tileGeo[svc.Name] = geo;
            if (_compact)
            {
                // Компактная карточка в две строки: название; флаг страны доступа и статус. Адрес в подсказке
                sp.Children.Add(head);
                var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                row2.Children.Add(geo);
                state.HorizontalAlignment = HorizontalAlignment.Left;
                row2.Children.Add(state);
                sp.Children.Add(row2);
                card.ToolTip = $"{svc.Access.TrimStart('✓', '✗', ' ')}\n{svc.Host} → {svc.ResolvedIp}{(svc.Proxied ? " (через Smart DNS)" : " (напрямую)")}";
            }
            else
            {
                DockPanel.SetDock(state, Dock.Right);
                head.Children.Insert(0, state);
                sp.Children.Add(head);
                geo.Margin = new Thickness(0, 8, 0, 0);
                sp.Children.Add(geo);
                sp.Children.Add(Line(
                    (svc.Proxied ? "через Smart DNS  " : "напрямую  ", svc.Proxied ? B("Accent") : B("Muted"), false),
                    (svc.ResolvedIp, B("Muted"), false)));
                if (was != null)
                    sp.Children.Add(Line(("было: ", B("Muted"), false),
                        ($"{was.Access.TrimStart('✓', '✗', ' ')}, {(was.Proxied ? "через Smart DNS" : "напрямую")}", B("Muted"), false)));
            }
            // Кнопка «Серверы» открывает адреса сервиса всплывающей панелью под карточкой: карточка не растет
            // и таблица не уезжает. В компактном окне кнопки нет: там мало места
            if (!_compact)
            {
                int hostCount = 1 + cur.Hosts.Count(h => h.Name.StartsWith(svc.Name + ": ", StringComparison.Ordinal));
                var toggle = new TextBlock
                {
                    Text = $"Серверы ({hostCount}) ▾",
                    Foreground = B("Accent"), FontSize = 12, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand,
                    Tag = svc, ToolTip = "Адреса сервиса и их статус",
                };
                toggle.MouseLeftButtonUp += ToggleHosts_Click;
                sp.Children.Add(toggle);
            }
            card.Child = sp;
            Grid.SetColumn(card, tiles.Children.Count);
            AnimateTile(card, (SolidColorBrush)card.Background, tiles.Children.Count,
                changed: was != null && (was.Access != svc.Access || was.Proxied != svc.Proxied));
            tiles.Children.Add(card);
        }
        host.Children.Add(tiles);

        FillServiceGeo(cur);
        _ = ResolveServiceGeoAsync(cur);

        // Итог
        int proxied = cur.Services.Count(x => x.Proxied);
        int open = cur.Services.Count(x => x.Access.StartsWith("✓"));
        int total = cur.Services.Count;
        (string text, string color) v =
            disconnected ? (proxied == 0 ? "отключено, сервисы идут напрямую" : "отключено, но адреса еще подменены: перезапустите браузер", proxied == 0 ? "Ok" : "Warn")
            : DnsSwitcher.GetActive() == null ? ($"не подключено · открываются {open} из {total}", "Muted")
            : proxied > 0 && open == total ? ($"работает · все {total} сервиса доступны", "Ok")
            : proxied > 0 ? ($"частично · доступно {open} из {total}", "Warn")
            : ("DNS сменился, но адреса ИИ не подменяются", "Bad");
        VerdictText.Text = v.text;
        VerdictText.Foreground = B(v.color);
    }

    /// <summary>
    /// Адреса сервиса: основной и дополнительные. На адрес две строки: имя и плашка статуса, ниже IP
    /// (синий — адрес прокси Smart DNS, серый — напрямую). Причина статуса в подсказке.
    /// </summary>
    private StackPanel HostsPanel(ServiceState svc, NetSnapshot cur)
    {
        Brush B(string k) => (Brush)FindResource(k);
        var mono = new FontFamily("Cascadia Mono, Consolas");
        var panel = new StackPanel();
        var entries = new List<ServiceState> { svc };
        entries.AddRange(cur.Hosts.Where(h => h.Name.StartsWith(svc.Name + ": ", StringComparison.Ordinal)));
        for (int i = 0; i < entries.Count; i++)
        {
            var h = entries[i];
            bool isOpen = h.Access.StartsWith("✓");
            bool isClosed = h.Access.Contains("закрыт");
            string pillColor = isOpen ? "ChipOk" : isClosed ? "ChipBad" : "ChipWarn";
            string pillText = isOpen ? "доступен" : isClosed ? "закрыт" : h.Access.Contains("подтвержден") ? "не подтвержден" : "не отвечает";
            string pillFg = isOpen ? "Ok" : isClosed ? "Bad" : "Warn";

            var top = new DockPanel();
            var pill = new Border
            {
                Background = (Brush)FindResource(pillColor),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = h.Note ?? h.Access,
                Child = new TextBlock { Text = pillText, FontSize = 11, Foreground = B(pillFg) },
            };
            DockPanel.SetDock(pill, Dock.Right);
            top.Children.Add(pill);
            top.Children.Add(new TextBlock
            {
                Text = h.Host, FontFamily = mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = h.Host,
            });

            var row = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0) };
            row.Children.Add(top);
            row.Children.Add(new TextBlock
            {
                Text = h.ResolvedIp + (h.Proxied ? "  · через Smart DNS" : "  · напрямую"), FontFamily = mono, FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0), Foreground = h.Proxied ? B("Accent") : B("Muted"),
            });
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>
    /// Открывает адреса сервиса всплывающей панелью под его карточкой, шириной в карточку. Щелчок мимо или Esc закрывает.
    /// Длинный список прокручивается внутри панели.
    /// </summary>
    private void ToggleHosts_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { Tag: ServiceState svc } toggle || _shownSnap is not { } cur) return;
        // Щелчок по кнопке открытой панели: панель уже закрылась от щелчка мимо, заново не открываем
        if (_hostsPopupFor == svc.Name && DateTime.Now - _hostsClosedAt < TimeSpan.FromMilliseconds(300)) return;
        if (_hostsPopup != null) _hostsPopup.IsOpen = false;
        if (toggle.Parent is not FrameworkElement { Parent: FrameworkElement card }) return;

        Brush B(string k) => (Brush)FindResource(k);
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = $"{svc.Name} · адреса и доступ", Foreground = B("Muted"), FontSize = 12, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        content.Children.Add(new ScrollViewer
        {
            Content = HostsPanel(svc, cur), MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 4, 0),
        });
        var frame = new Border
        {
            Child = content, Background = B("Surface2"), BorderBrush = B("Border"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(8, 0, 8, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black },
        };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Child = frame, PlacementTarget = card, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            HorizontalOffset = -8, VerticalOffset = 4, Width = card.ActualWidth + 16, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
        };
        popup.Closed += (_, _) =>
        {
            // Текст задаем заново, а не заменой в показанном: на английском показан перевод
            toggle.Text = $"Серверы ({1 + cur.Hosts.Count(h => h.Name.StartsWith(svc.Name + ": ", StringComparison.Ordinal))}) ▾";
            _hostsClosedAt = DateTime.Now;
        };
        popup.KeyDown += (_, k) => { if (k.Key == Key.Escape) popup.IsOpen = false; };
        _hostsPopup = popup;
        _hostsPopupFor = svc.Name;
        toggle.Text = "Скрыть серверы ▴";
        popup.IsOpen = true;
    }


    // IP → коды стран по двум базам (ipinfo, ipwho.is); null, если база не ответила
    private readonly Dictionary<string, (string? First, string? Second)> _ipCountry = new();
    private readonly Dictionary<string, StackPanel> _tileGeo = new(); // строка страны в каждой плитке сервиса

    // Через Smart DNS сервис отдает адрес прокси в другой стране — его и показываем флагом.
    // Напрямую адрес настоящий (часто anycast Cloudflare), база его локации не отражает — не геолокируем.
    private void FillServiceGeo(NetSnapshot cur)
    {
        var muted = (Brush)FindResource("Muted");
        foreach (var s in cur.Services)
        {
            if (!_tileGeo.TryGetValue(s.Name, out var row)) continue;
            row.Children.Clear();
            row.Visibility = s.Proxied ? Visibility.Visible : Visibility.Collapsed; // напрямую страну не показываем
            if (!s.Proxied) continue;

            // Страна — та, откуда сервис видит соединение (выход), а если выход не определен — страна прокси
            var proxyName = CountryOfIp(s.ResolvedIp);
            var code = s.Egress?.Country ?? CodeOfIp(s.ResolvedIp);
            if (code != null && LoadFlag(GeoLocation.FlagPath(code) ?? "") is { } flag)
                row.Children.Add(new Image { Source = flag, Width = 20, Height = 14, Margin = new Thickness(0, 0, 6, 0), Stretch = Stretch.Uniform });
            var name = s.Egress != null ? GeoLocation.CountryName(s.Egress.Country) : proxyName;
            var warn = s.Egress == null && _ipCountry.TryGetValue(s.ResolvedIp, out var r) && r.First != null && r.Second != null && r.First != r.Second;
            row.ToolTip = s.Egress != null
                ? $"{s.Name} видит вас из: {name} ({s.Egress.Ip})\nПрокси Smart DNS: {proxyName} ({s.ResolvedIp})"
                : warn ? proxyName + "\nБазы определяют страну по-разному" : proxyName + "\nВыход не определен: показана страна прокси";
            if (_compact) continue; // компактная плитка: только флаг, название страны в подсказке
            row.Children.Add(new TextBlock { Text = warn ? name + " ⚠" : name, Foreground = muted, FontWeight = FontWeights.SemiBold });
        }
    }

    private string? CodeOfIp(string ip) =>
        _ipCountry.TryGetValue(ip, out var r) ? r.First ?? r.Second : null;

    private string CountryOfIp(string ip)
    {
        if (!GeoLocation.IsPublicIp(ip)) return "локальный адрес";
        if (!_ipCountry.TryGetValue(ip, out var r)) return "определяется…";
        if (r.First == null && r.Second == null) return "неизвестно";
        if (r.First != null && r.Second != null && r.First != r.Second)
            return $"{GeoLocation.CountryName(r.First)} / {GeoLocation.CountryName(r.Second)}, базы расходятся";
        return GeoLocation.CountryName((r.First ?? r.Second)!);
    }

    private async Task ResolveServiceGeoAsync(NetSnapshot cur)
    {
        foreach (var s in cur.Services)
        {
            if (!s.Proxied || !GeoLocation.IsPublicIp(s.ResolvedIp) || _ipCountry.ContainsKey(s.ResolvedIp)) continue;
            if (!_geoPending.Add(s.ResolvedIp)) continue; // этот адрес уже определяется другим вызовом
            // Страна голосованием баз; вторая страна в паре только когда большинства нет (тогда плитка покажет обе)
            try
            {
                var r = await GeoLocation.ResolveAsync(s.ResolvedIp, CancellationToken.None);
                if (r == null) continue;                             // сеть или лимит: определим при следующей перерисовке
                _ipCountry[s.ResolvedIp] = (r.Country, r.Disputed ? r.Second : r.Country);
            }
            finally { _geoPending.Remove(s.ResolvedIp); }
        }
        // Флаги нужных стран: скачиваем один раз, потом берем из кэша
        foreach (var code in _ipCountry.Values.Select(r => r.First ?? r.Second)
                     .Concat(cur.Services.Select(x => x.Egress?.Country)).Where(c => c != null).Distinct())
            await GeoLocation.FlagPathAsync(code!, CancellationToken.None);
        if (ReferenceEquals(cur, _shownSnap)) FillServiceGeo(cur);
    }

    /// <summary>Флаг и место текущего подключения в шапке.</summary>
    private void UpdateConnectionLocation()
    {
        var active = DnsSwitcher.GetActive();
        var srv = active == null ? null : _servers.FirstOrDefault(x => x.Id == active.ServerId);
        ConnFlag.Source = srv?.Flag;
        ConnFlag.Visibility = srv?.Flag != null ? Visibility.Visible : Visibility.Collapsed;
        var text = srv == null ? "" : srv.LocationCell;
        ConnLocation.Text = text;
        ConnLocation.ToolTip = srv?.LocationTooltip;
        ConnLocation.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Плитка въезжает по очереди; если значение изменилось — на мгновение вспыхивает.</summary>
    private static void AnimateTile(Border card, SolidColorBrush bg, int index, bool changed)
    {
        var delay = Motion(90 * index);
        var shift = new TranslateTransform(0, 10);
        card.RenderTransform = shift;
        card.Opacity = 0;
        card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion(320)) { BeginTime = delay });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, Motion(320))
        {
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        if (changed)
            bg.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromRgb(0x2F, 0x5C, 0xA8), bg.Color, Motion(1200))
                {
                    BeginTime = delay + TimeSpan.FromMilliseconds(250),
                });
    }
}
