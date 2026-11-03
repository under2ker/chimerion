using System.IO;
using System.Xml.Linq;

namespace Dnex.Services;

/// <summary>Описание задания посредника для текущего пользователя. Само задание не меняет DNS и не получает прав администратора.</summary>
internal static class DohRelayTask
{
    internal static string TaskName(string channel) => Guid.TryParseExact(channel, "N", out _)
        ? "CHIMERION DoH " + channel : throw new ArgumentException("Некорректный канал посредника", nameof(channel));
    internal const string HelperArgument = "--doh-relay-helper";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static string Xml(string exePath, string userSid, string channel)
    {
        if (!Path.IsPathFullyQualified(exePath) || !string.Equals(Path.GetExtension(exePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Нужен полный путь к программе", nameof(exePath));
        // Канал проверяем здесь же: задание с чужим каналом не сможет найти настройки посредника.
        if (!Guid.TryParseExact(channel, "N", out _))
            throw new ArgumentException("Некорректный канал посредника", nameof(channel));
        string user;
        try { user = new System.Security.Principal.SecurityIdentifier(userSid).Value; }
        catch (ArgumentException) { throw new ArgumentException("Некорректный идентификатор пользователя", nameof(userSid)); }
        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Description", "CHIMERION: локальный DoH")),
            new XElement(Ns + "Triggers", new XElement(Ns + "LogonTrigger", new XElement(Ns + "Enabled", "true"), new XElement(Ns + "UserId", user))),
            new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "User"),
                new XElement(Ns + "UserId", user), new XElement(Ns + "LogonType", "InteractiveToken"), new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings", new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"), new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "StartWhenAvailable", "true"), new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                new XElement(Ns + "RestartOnFailure", new XElement(Ns + "Interval", "PT1M"), new XElement(Ns + "Count", 3))),
            new XElement(Ns + "Actions", new XAttribute("Context", "User"), new XElement(Ns + "Exec",
                // Канал передается заданию, чтобы процесс взял настройки именно этого подключения.
                new XElement(Ns + "Command", exePath), new XElement(Ns + "Arguments", HelperArgument + " " + channel))));
        return task.ToString(SaveOptions.DisableFormatting);
    }
}
