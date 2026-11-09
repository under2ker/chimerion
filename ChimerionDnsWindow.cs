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

    public static readonly string[] Benefits =
    {
        "ChatGPT, Claude и Gemini открываются без VPN",
        "Личный доступ: своя ссылка DoH и свой адрес DoT, а не общий сервер из списка",
        "Запросы DNS зашифрованы, провайдер их не видит и не подменяет",
        "Работает в CHIMERION на Windows 10 и 11, в браузере и в «Частном DNS» на Android",
        "Команда /dns в боте в любой момент покажет доступ и инструкцию",
    };
}

/// <summary>Окно «CHIMERION DNS»: чем хорош, где находится, сколько стоит, где купить и куда написать.</summary>
internal static class ChimerionDnsWindow
{
    public static void Show() => DnexDialog.Present(Build());

    internal static Window Build()
    {
        var w = DnexDialog.CreateWindow("CHIMERION DNS", Ui.Narrow + 40);
        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "CHIMERION DNS", "Личный Smart DNS от создателей программы", glyph: "star"));

        // Слева что дает доступ и сколько стоит, справа регион и где купить. В узком окне колонки встают друг под друга
        var left = new StackPanel();
        left.Children.Add(Ui.Section("Преимущества", first: true));
        foreach (var benefit in ChimerionDnsInfo.Benefits) left.Children.Add(BenefitRow(benefit));
        left.Children.Add(Ui.Section("Сроки и цены"));
        left.Children.Add(PlanTiles());

        var right = new StackPanel();
        right.Children.Add(Ui.Section("Регион", first: true));
        right.Children.Add(RegionCard());
        right.Children.Add(Ui.Section("Где купить"));
        right.Children.Add(ContactCard());

        var body = TwoColumns(left, right, 820);

        // Шапка и кнопки занимают около 230: на небольшом экране прокручивается середина, а кнопки остаются видны
        var scroll = Ui.Scroll(body, Math.Min(780, Ui.WorkAreaOf(null).Height - 230));
        root.Children.Add(scroll);
        // При показе окно ставит фокус на первую кнопку, а это ссылка внизу списка: без сброса окно открывалось прокрученным
        w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(scroll.ScrollToTop, System.Windows.Threading.DispatcherPriority.Background);

        var buy = Ui.Action("Открыть бота", "Primary", () => AutoStart.OpenUrl(ChimerionDnsInfo.BotUrl));
        var contact = Ui.Action("Написать", "Strong", () => AutoStart.OpenUrl(ChimerionDnsInfo.ContactUrl));
        var close = Ui.Action("Закрыть", "Ghost", w.Close).With(b => b.IsCancel = true);
        // Все три кнопки справа: левый слот подвала растягивается на всю свободную ширину, и «Написать» выходила огромной
        var footer = Ui.Footer(null, buy, contact, close);
        footer.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(footer);
        w.Content = root;
        return w;
    }

    /// <summary>Две колонки на широкой странице, одна под другой уже <paramref name="wide"/> точек.</summary>
    internal static Grid TwoColumns(FrameworkElement left, FrameworkElement right, double wide)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.Children.Add(left); g.Children.Add(right);
        bool? sideBySide = null;
        void Place(double width)
        {
            bool side = width >= wide;
            if (side == sideBySide) return;
            sideBySide = side;
            Grid.SetColumnSpan(left, side ? 1 : 3);
            Grid.SetRow(right, side ? 0 : 1); Grid.SetColumn(right, side ? 2 : 0); Grid.SetColumnSpan(right, side ? 1 : 3);
            right.Margin = new Thickness(0, side ? 0 : 14, 0, 0);
        }
        Place(0);
        g.SizeChanged += (_, e) => Place(e.NewSize.Width);
        return g;
    }

    /// <summary>Пункт списка: галочка и текст, перенос строки встает под текст, а не под значок.</summary>
    private static Grid BenefitRow(string text)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mark = new Controls.Icon { Kind = "circle-check", Size = 17, Margin = new Thickness(0, 1, 11, 0), VerticalAlignment = VerticalAlignment.Top }
            .Ref(Controls.Icon.ForegroundProperty, "Ok");
        g.Children.Add(mark);
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    /// <summary>Страна сервера: флаг, название и пояснение. Флаг берется из кэша, а если его еще нет, догружается.</summary>
    private static Border RegionCard()
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
        var card = Ui.Card(row, bottom: 0);
        card.Padding = new Thickness(16, 12, 16, 12);
        return card;
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
            var card = Ui.Card(tile, bottom: 0);
            card.Margin = new Thickness(0, 0, i < ChimerionDnsInfo.Plans.Length - 1 ? 8 : 0, 0);
            grid.Children.Add(card);
        }
        return grid;
    }

    /// <summary>Где купить и как связаться: бот, личные сообщения и канал. Адрес в каждой строке открывает свою ссылку.</summary>
    private static Border ContactCard()
    {
        var list = new StackPanel();
        list.Children.Add(ContactRow("Купить в боте", "Оплата звездами. Доступ приходит в чат сразу после оплаты", ChimerionDnsInfo.BotName, ChimerionDnsInfo.BotUrl));
        list.Children.Add(Ui.Separator());
        list.Children.Add(ContactRow("Другой способ оплаты и вопросы", null, ChimerionDnsInfo.ContactName, ChimerionDnsInfo.ContactUrl));
        list.Children.Add(Ui.Separator());
        list.Children.Add(ContactRow("Новости", null, ChimerionDnsInfo.ChannelName, ChimerionDnsInfo.ChannelUrl));
        return Ui.Card(list, bottom: 0);
    }

    private static Grid ContactRow(string title, string? note, string handle, string url)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var logo = new Image { Width = 22, Height = 22, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        logo.SetResourceReference(Image.SourceProperty, "LogoTelegram");
        g.Children.Add(logo);
        var name = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(name, 1);
        g.Children.Add(name);
        // Пояснение на всю ширину под названием, а не в узкой колонке рядом с кнопкой
        if (note != null)
        {
            var hint = Ui.Hint(note, 4);
            Grid.SetRow(hint, 1); Grid.SetColumn(hint, 1); Grid.SetColumnSpan(hint, 2);
            g.Children.Add(hint);
        }
        var link = new Button { Content = handle, Style = (Style)Application.Current.Resources["Ghost"], Padding = new Thickness(10, 5, 10, 5), MinHeight = 0, Margin = new Thickness(0), FontWeight = FontWeights.SemiBold, ToolTip = url, VerticalAlignment = VerticalAlignment.Center }
            .Ref(Control.ForegroundProperty, "Accent");
        link.Click += (_, _) => AutoStart.OpenUrl(url);
        Grid.SetColumn(link, 2);
        g.Children.Add(link);
        return g;
    }
}
