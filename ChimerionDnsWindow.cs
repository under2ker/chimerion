using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Dnex.Services;

namespace Dnex;

/// <summary>Сведения о платном CHIMERION DNS. Цены и сроки те же, что в боте и магазине панели.</summary>
internal static class ChimerionDnsInfo
{
    public const string Region = "Нидерланды";
    public const string ContactName = "@dev_under2ker";
    public const string ContactUrl = "https://t.me/dev_under2ker";
    public const string BotName = "@CHIMERIONDNS_BOT";
    public const string BotUrl = "https://t.me/CHIMERIONDNS_BOT";
    public const string ChannelName = "@chimeriondns";
    public const string ChannelUrl = "https://t.me/chimeriondns";
    public const string ShopUrl = "https://chimerion.dev/buy";

    /// <summary>Строка в таблице серверов: страна для флага, без адресов. Проверено «сейчас», чтобы фон ее не трогал.</summary>
    public static Models.DnsServer PromoRow() => new()
    {
        Id = new Guid("c41f7a6e-2b0d-4e58-9a13-6d0e5b7f2c11"),
        IsPromo = true,
        Name = "CHIMERION DNS",
        Manual = true,
        Country = "NL",
        AddedAt = DateTime.MinValue, // не «новый»
        GeoCheckedAt = DateTime.Now,
        LastChecked = DateTime.Now,
    };

    public static readonly (string Title, int Days, int Rub, int Stars)[] Plans =
    {
        ("30 дней", 30, 299, 299),
        ("90 дней", 90, 799, 799),
        ("365 дней", 365, 2490, 2490),
    };

    /// <summary>Главное в одной строке: заголовок витрины.</summary>
    public const string Promise = "ChatGPT, Claude и Gemini без VPN";

    public static readonly (string Icon, string Title, string Text)[] Benefits =
    {
        ("link", "Личный доступ", "Своя ссылка DoH и свой адрес DoT, а не общий сервер из списка"),
        ("shield-check", "Шифрование", "Запросы DNS зашифрованы, провайдер их не видит и не подменяет"),
        ("smartphone", "Ваши устройства", "CHIMERION на Windows 10 и 11, браузеры и «Частный DNS» на Android"),
        ("send", "Доступ сразу", "Бот присылает доступ после оплаты, команда /dns покажет его снова"),
    };

    /// <summary>Как подключить: от оплаты до работающего DNS, три шага.</summary>
    public static readonly (string Title, string Text)[] Steps =
    {
        ("Оплатите в боте", "Откройте @CHIMERIONDNS_BOT, выберите срок и оплатите звездами. Другой способ оплаты: напишите @dev_under2ker"),
        ("Получите доступ", "Бот сразу пришлет вашу ссылку DoH и адрес DoT. Команда /dns покажет их снова в любой момент"),
        ("Подключите", "В CHIMERION: «Добавить сервер», вставьте ссылку DoH и подключитесь. На Android адрес DoT вписывается в «Частный DNS»"),
    };

    /// <summary>Частые вопросы, те же ответы, что в боте.</summary>
    public static readonly (string Question, string Answer)[] Faq =
    {
        ("На скольких устройствах работает", "На всех ваших сразу: ПК, ноутбук, телефон. Настройку повторите на каждом устройстве и в каждом браузере"),
        ("Можно поделиться доступом", "Нет: адреса личные, по ним считается ваш трафик. Если ссылка попала к чужим, напишите, выдадим новую"),
        ("Оплатил, а доступа нет", "Отправьте боту /dns. Если доступ не появился за несколько минут, напишите в поддержку и укажите Telegram ID"),
        ("Сайт все равно не открывается", "Выключите в браузере свой безопасный DNS и перезапустите браузер. Не помогло: напишите в чат и приложите снимок экрана"),
    };
}

/// <summary>Окно «CHIMERION DNS»: чем хорош, где находится, сколько стоит, где купить и куда написать.</summary>
internal static class ChimerionDnsWindow
{
    public static void Show() => DnexDialog.Present(Build());

    internal static Window Build()
    {
        var w = DnexDialog.CreateWindow("CHIMERION DNS", Ui.Narrow + 40);
        // Шапка сверху, кнопки прижаты к низу, середина прокручивается: на странице главного окна кнопки всегда внизу
        var root = new DockPanel { Margin = new Thickness(24, 22, 24, 20), LastChildFill = true };
        var header = DnexDialog.Header(DialogKind.Info, "CHIMERION DNS", "Личный Smart DNS от создателей программы", glyph: "star");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var close = Ui.Action("Закрыть", "Ghost", w.Close).With(b => b.IsCancel = true);
        // Купить и написать теперь в карточке цен; внизу новости и закрыть
        var news = new TextBlock { Text = $"Новости: {ChimerionDnsInfo.ChannelName}", FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = ChimerionDnsInfo.ChannelUrl }
            .Ref(TextBlock.ForegroundProperty, "Muted");
        news.MouseLeftButtonUp += (_, _) => AutoStart.OpenUrl(ChimerionDnsInfo.ChannelUrl);
        var footer = Ui.Footer(news, close);
        footer.Margin = new Thickness(0, 14, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Витрина: слева что это и регион, рядом заметная карточка цен с покупкой. В узком окне колонки встают друг под друга
        var left = new StackPanel();
        left.Children.Add(Ui.Section("О доступе", first: true));
        left.Children.Add(IntroCard());

        var right = new StackPanel();
        right.Children.Add(Ui.Section("Сроки и цены", first: true));
        right.Children.Add(PriceCard());

        var body = new StackPanel();
        body.Children.Add(TwoColumns(left, right, 820));
        body.Children.Add(Ui.Section("Преимущества"));
        body.Children.Add(Ui.IconTiles(ChimerionDnsInfo.Benefits, 4, 190));
        body.Children.Add(Ui.Section("Как подключить"));
        body.Children.Add(StepTiles());
        body.Children.Add(Ui.Section("Частые вопросы"));
        body.Children.Add(FaqGrid());

        // Отдельным окном (программа в трее) середина не выше экрана: шапка и кнопки занимают около 230
        var scroll = Ui.Scroll(body, Ui.WorkAreaOf(null).Height - 230);
        root.Children.Add(scroll);
        // При показе окно ставит фокус на первую кнопку, а это ссылка внизу списка: без сброса окно открывалось прокрученным
        w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(scroll.ScrollToTop, System.Windows.Threading.DispatcherPriority.Background);
        w.Content = root;
        return w;
    }

    /// <summary>Две колонки на широкой странице, одна под другой уже <paramref name="wide"/> точек.</summary>
    internal static Grid TwoColumns(FrameworkElement left, FrameworkElement right, double wide) => Ui.Columns(left, right, wide);

    /// <summary>Что это и откуда вас видят: обещание, пояснение и регион с флагом.</summary>
    private static Border IntroCard()
    {
        var box = new StackPanel();
        box.Children.Add(new TextBlock { Text = ChimerionDnsInfo.Promise, FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"] });
        box.Children.Add(Ui.Hint("Личный Smart DNS от создателей CHIMERION: свой доступ для нейросетей, а не общий сервер из списка", 6));
        box.Children.Add(Ui.Separator().With(s => s.Margin = new Thickness(0, 14, 0, 14)));
        box.Children.Add(RegionRow());
        var card = Ui.Card(box, bottom: 0);
        card.Padding = new Thickness(18, 16, 18, 16);
        return card;
    }

    /// <summary>Цены и покупка в одной заметной карточке: три срока, кнопка бота и другой способ оплаты.</summary>
    private static Border PriceCard()
    {
        var box = new StackPanel();
        box.Children.Add(PlanTiles());
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(Ui.Action("Купить в боте", "Primary", () => AutoStart.OpenUrl(ChimerionDnsInfo.BotUrl)).With(b => b.Margin = new Thickness(0, 0, 8, 0)));
        actions.Children.Add(Ui.Action("Написать", "Strong", () => AutoStart.OpenUrl(ChimerionDnsInfo.ContactUrl)).With(b => b.ToolTip = ChimerionDnsInfo.ContactName));
        box.Children.Add(actions);
        box.Children.Add(Ui.Hint($"Оплата звездами Telegram, доступ приходит в чат сразу. Другой способ оплаты и вопросы: {ChimerionDnsInfo.ContactName}", 10));
        var card = Ui.Card(box, selected: true, bottom: 0);
        card.Padding = new Thickness(16, 16, 16, 16);
        return card;
    }

    /// <summary>Страна сервера: флаг, название и пояснение. Флаг берется из кэша, а если его еще нет, догружается.</summary>
    private static Grid RegionRow()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var flag = new Image { Width = 36, Height = 27, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var flagFrame = new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = flag, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(flagFrame);
        _ = ShowFlagAsync(flag, "nl");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = ChimerionDnsInfo.Region, FontSize = 15, FontWeight = FontWeights.SemiBold });
        text.Children.Add(Ui.Hint("Нейросети видят вас из этой страны", 2));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private static async Task ShowFlagAsync(Image flag, string country)
    {
        try
        {
            var path = GeoLocation.FlagPath(country) ?? await GeoLocation.FlagPathAsync(country, CancellationToken.None);
            if (path == null) return;
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            flag.Source = img;
            flag.Visibility = Visibility.Visible;
        }
        catch (Exception) { /* без флага остается название страны */ }
    }

    /// <summary>Три срока рядом: крупная цена в рублях и цена в звездах Telegram.</summary>
    private static UniformGrid PlanTiles()
    {
        var grid = new UniformGrid { Columns = ChimerionDnsInfo.Plans.Length };
        for (int i = 0; i < ChimerionDnsInfo.Plans.Length; i++)
        {
            var plan = ChimerionDnsInfo.Plans[i];
            var tile = new StackPanel();
            tile.Children.Add(new TextBlock { Text = plan.Title, FontSize = 12.5 }.Ref(TextBlock.ForegroundProperty, "Muted"));
            tile.Children.Add(new TextBlock
            {
                Text = $"{plan.Rub} ₽", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2),
                FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"],
            });
            var stars = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = "Оплата звездами Telegram" };
            stars.Children.Add(new TextBlock { Text = plan.Stars.ToString(), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Muted"));
            stars.Children.Add(new Controls.Icon { Kind = "star", Size = 13, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }.Ref(Controls.Icon.ForegroundProperty, "Accent"));
            tile.Children.Add(stars);
            var perDay = (double)plan.Rub / plan.Days;
            var day = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            day.Children.Add(Ui.Hint($"около {Math.Round(perDay):0} ₽ в день", 0).With(t => t.Margin = new Thickness(0, 0, 8, 0)));
            if (i == ChimerionDnsInfo.Plans.Length - 1) day.Children.Add(Ui.Hint("Выгоднее всего", 0, "Accent"));
            tile.Children.Add(day);
            var card = Ui.Card(tile, bottom: 0);
            card.Margin = new Thickness(0, 0, i < ChimerionDnsInfo.Plans.Length - 1 ? 8 : 0, 0);
            grid.Children.Add(card);
        }
        return grid;
    }

    /// <summary>Три шага подключения рядом: номер, заголовок и пояснение.</summary>
    private static UniformGrid StepTiles()
    {
        var grid = new UniformGrid { Columns = ChimerionDnsInfo.Steps.Length };
        for (int i = 0; i < ChimerionDnsInfo.Steps.Length; i++)
        {
            var (title, text) = ChimerionDnsInfo.Steps[i];
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock { Text = (i + 1).ToString(), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                    .Ref(TextBlock.ForegroundProperty, "Accent") }.Ref(Border.BackgroundProperty, "AccentSoft");
            head.Children.Add(badge);
            head.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var tile = new StackPanel();
            tile.Children.Add(head);
            tile.Children.Add(Ui.Hint(text, 10));
            var card = Ui.Card(tile, bottom: 0);
            card.Margin = new Thickness(0, 0, i < ChimerionDnsInfo.Steps.Length - 1 ? 8 : 0, 0);
            grid.Children.Add(card);
        }
        return grid;
    }

    /// <summary>Частые вопросы карточками по два в ряд.</summary>
    private static UniformGrid FaqGrid() => Ui.InfoTiles(ChimerionDnsInfo.Faq, 2);
}
