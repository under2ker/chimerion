namespace Dnex.Services;

/// <summary>На что хватает скорости: понятные пороги вместо голых цифр.</summary>
public static class SpeedVerdict
{
    public sealed record Use(string Title, bool Ok, string Need);

    public static List<Use> For(InternetResult r)
    {
        double down = r.DownMbit ?? 0, up = r.UpMbit ?? 0;
        long ping = r.PingMs ?? long.MaxValue, jitter = r.JitterMs ?? long.MaxValue;
        return new()
        {
            new("Видео в 4K", down >= 25, "от 25 Мбит/с на скачивание"),
            new("Видеозвонки", down >= 5 && up >= 3 && ping <= 150, "от 5 и 3 Мбит/с, пинг до 150 мс"),
            new("Онлайн-игры", ping <= 60 && jitter <= 20 && down >= 3, "пинг до 60 мс, джиттер до 20 мс"),
            new("Отправка больших файлов", up >= 20, "от 20 Мбит/с на отправку"),
        };
    }

    /// <summary>Цвет оценки DNS: быстрый и обычный хорошо, медленный стоит заменить, без ответов плохо.</summary>
    public static string DnsColor(string grade) => grade switch
    {
        "быстрый" => "Ok",
        "обычный" => "Text",
        "медленный" => "Warn",
        _ => "Bad",
    };
}
