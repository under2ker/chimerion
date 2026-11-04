using System.Windows;
using System.Windows.Controls;
using Dnex.Services;

namespace Dnex;

/// <summary>Сведения о платном CHIMERION DNS. Цены и сроки те же, что в боте и магазине панели.</summary>
internal static class ChimerionDnsInfo
{
    public const string Region = "Нидерланды";
    public const string BotName = "@CHIMERIONDNS_BOT";
    public const string BotUrl = "https://t.me/CHIMERIONDNS_BOT";
    public const string ChannelName = "@chimeriondns";
    public const string ChannelUrl = "https://t.me/chimeriondns";
    public const string ShopUrl = "https://chimerion.dev/buy";

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
    public static void Show() => Build().ShowDialog();

    internal static Window Build()
    {
        var w = DnexDialog.CreateWindow("CHIMERION DNS", Ui.Narrow + 40);
        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "CHIMERION DNS", "Личный Smart DNS от создателей программы", glyph: "star"));

        var body = new StackPanel();
        body.Children.Add(Ui.Section("Преимущества", first: true));
        foreach (var benefit in ChimerionDnsInfo.Benefits)
            body.Children.Add(new TextBlock { Text = "• " + benefit, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

        body.Children.Add(Ui.Section("Регион"));
        body.Children.Add(new TextBlock { Text = "Сервер: " + ChimerionDnsInfo.Region + ". Нейросети видят вас из этой страны" , TextWrapping = TextWrapping.Wrap });

        body.Children.Add(Ui.Section("Сроки и цены"));
        foreach (var plan in ChimerionDnsInfo.Plans)
            body.Children.Add(Ui.Card(Ui.Row(plan.Title, null, new TextBlock { Text = $"{plan.Rub} ₽ · {plan.Stars} ⭐", FontWeight = FontWeights.SemiBold }), bottom: 6));

        body.Children.Add(Ui.Section("Где купить"));
        body.Children.Add(new TextBlock { Text = $"В Telegram-боте {ChimerionDnsInfo.BotName}, оплата звездами. Доступ приходит в чат сразу после оплаты", TextWrapping = TextWrapping.Wrap });

        body.Children.Add(Ui.Section("Связь"));
        body.Children.Add(new TextBlock { Text = $"Вопросы и покупка: {ChimerionDnsInfo.BotName}. Новости: канал {ChimerionDnsInfo.ChannelName}", TextWrapping = TextWrapping.Wrap });
        // Шапка и кнопки занимают около 230: на небольшом экране прокручивается середина, а кнопки остаются видны
        root.Children.Add(Ui.Scroll(body, Math.Min(600, SystemParameters.WorkArea.Height - 230)));

        var buy = Ui.Action("Открыть бота", "Primary", () => AutoStart.OpenUrl(ChimerionDnsInfo.BotUrl));
        var close = Ui.Action("Закрыть", "Ghost", w.Close).With(b => b.IsCancel = true);
        var channel = Ui.Action("Канал", "Ghost", () => AutoStart.OpenUrl(ChimerionDnsInfo.ChannelUrl));
        var footer = Ui.Footer(channel, buy, close);
        footer.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(footer);
        w.Content = root;
        return w;
    }
}
