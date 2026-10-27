namespace Dnex.Services;

/// <summary>
/// Здоровье подключения по каждому ИИ-сервису. Сторож следит, что DNS вообще отвечает (обычные сайты открываются),
/// а этот класс следит за тем, ради чего DNS подключали: открываются ли ChatGPT, Claude и Gemini через текущий
/// сервер. Smart DNS часто «падает» наполовину: обычные домены резолвит, а адреса ИИ перестает подменять, или его
/// прокси умирает. Сторож такого не видит.
///
/// Падением сервиса считаются FailsToAlert неудачных проверок подряд (разовая неудача бывает у любого сервиса).
/// Восстановлением — первая удачная проверка после падения. Отдельно отслеживается, отвечает ли сам DNS-сервер на
/// прямой запрос: Windows при молчащем сервере тихо переходит на запасной DNS, и сайты открываются, а ИИ нет.
/// Без окна и без сети: на вход результаты проверок, на выход изменения, о которых нужно сообщить.
/// </summary>
public sealed class ServiceHealth
{
    public const int FailsToAlert = 2;

    public enum Problem { None, NoDns, NotProxied, Closed, NoResponse }

    /// <summary>Изменение для уведомления: сервис упал (Down) или снова работает.</summary>
    public sealed record Change(string Service, bool Down, Problem Problem);

    /// <param name="EverOk">Сервис хоть раз открылся в этом подключении. Если он был закрыт с самого начала (например,
    /// Gemini на сервере, который его не поддерживает), «перестал открываться» было бы неправдой: о нем не сообщаем.</param>
    private sealed class State { public int Fails; public bool Down; public Problem Last; public bool EverOk; }

    private readonly Dictionary<string, State> _services = new(StringComparer.OrdinalIgnoreCase);
    private int _serverFails;

    /// <summary>Сейчас считается упавшим: сам DNS-сервер не отвечает.</summary>
    public bool ServerDown { get; private set; }

    /// <summary>Есть неподтвержденная неудача: стоит проверить раньше обычного, чтобы быстрее узнать о падении.</summary>
    public bool Suspicious => _serverFails > 0 && !ServerDown || _services.Values.Any(s => s.Fails > 0 && !s.Down && s.EverOk);

    /// <summary>Какие сервисы сейчас упали.</summary>
    public IReadOnlyList<string> DownServices => _services.Where(kv => kv.Value.Down).Select(kv => kv.Key).ToList();

    /// <summary>Новое подключение или другой сервер: прежняя история не про него.</summary>
    public void Reset() { _services.Clear(); _serverFails = 0; ServerDown = false; }

    /// <summary>Причина неудачи сервиса по результату проверки подключения. None — сервис открывается.</summary>
    public static Problem Classify(ServiceState s)
    {
        if (s.Access.StartsWith('✓')) return Problem.None;
        if (s.ResolvedIp is "не резолвится" or "—" or "") return Problem.NoDns;
        if (!s.Proxied) return Problem.NotProxied;
        return s.Access.Contains("закрыт") ? Problem.Closed : Problem.NoResponse;
    }

    public static string Describe(Problem p) => p switch
    {
        Problem.NoDns => "DNS не отвечает на адрес сервиса",
        Problem.NotProxied => "сервер перестал подменять адреса",
        Problem.Closed => "доступ закрыт, прокси сервера попал под блокировку",
        Problem.NoResponse => "прокси сервера не отвечает",
        _ => "работает",
    };

    /// <summary>Результаты очередной проверки сервисов. Возвращает изменения, о которых нужно сообщить.</summary>
    public List<Change> Update(IEnumerable<(string Name, Problem Problem)> results)
    {
        var changes = new List<Change>();
        foreach (var (name, problem) in results)
        {
            if (!_services.TryGetValue(name, out var st)) _services[name] = st = new State();
            if (problem == Problem.None)
            {
                if (st.Down) changes.Add(new Change(name, false, Problem.None));
                st.Fails = 0; st.Down = false; st.Last = Problem.None; st.EverOk = true;
                continue;
            }
            st.Fails++;
            st.Last = problem;
            if (!st.Down && st.EverOk && st.Fails >= FailsToAlert)
            {
                st.Down = true;
                changes.Add(new Change(name, true, problem));
            }
        }
        return changes;
    }

    /// <summary>Ответил ли сам DNS-сервер на прямой запрос. Возвращает изменение: упал (true), поднялся (false) или ничего (null).</summary>
    public bool? UpdateServer(bool answered)
    {
        if (answered)
        {
            _serverFails = 0;
            if (!ServerDown) return null;
            ServerDown = false;
            return false;
        }
        _serverFails++;
        if (ServerDown || _serverFails < FailsToAlert) return null;
        ServerDown = true;
        return true;
    }

    /// <summary>
    /// Текст уведомления о падении: «kayonedns: ChatGPT и Claude перестали открываться (сервер перестал подменять адреса)».
    /// Сервисы с одной причиной объединяются.
    /// </summary>
    public static string DownText(string server, IReadOnlyList<Change> down)
    {
        var groups = down.GroupBy(c => c.Problem).Select(g =>
        {
            var names = g.Select(c => c.Service).ToList();
            var who = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " и " + names[^1];
            var verb = names.Count == 1 ? "перестал открываться" : "перестали открываться";
            return $"{who} {verb} ({Describe(g.Key)})";
        });
        return $"{server}: {string.Join("; ", groups)}";
    }

    public static string UpText(string server, IReadOnlyList<Change> up)
    {
        var names = up.Select(c => c.Service).ToList();
        var who = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " и " + names[^1];
        return $"{server}: {who} {(names.Count == 1 ? "снова открывается" : "снова открываются")}";
    }
}
