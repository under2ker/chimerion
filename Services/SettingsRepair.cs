using System.Collections;
using System.Reflection;

namespace Dnex.Services;

/// <summary>
/// Пустые значения (null) вместо списков в файле настроек: "sources": null и подобное. System.Text.Json кладет null
/// в свойство-список, и первое же обращение к нему роняло запуск (аудит P-03). Такие списки заменяются значениями
/// по умолчанию; то же внутри элементов списков (сервисы, источники). Остальные значения не трогаются.
/// </summary>
internal static class SettingsRepair
{
    /// <summary>Заменить null-коллекции в obj значениями из defaults того же типа. Возвращает число замен.</summary>
    public static int FillNullCollections(object obj, object defaults) => Fill(obj, defaults, 0);

    private static int Fill(object obj, object? defaults, int depth)
    {
        if (depth > 4) return 0;
        var fixedCount = 0;
        foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0 || !IsCollection(p.PropertyType)) continue;
            var value = p.GetValue(obj);
            if (value == null)
            {
                if (!p.CanWrite) continue;
                var def = defaults != null ? p.GetValue(defaults) : null;
                value = def != null ? Clone(def) : Create(p.PropertyType);
                if (value == null) continue;
                p.SetValue(obj, value);
                fixedCount++;
            }
            // Элементы списка (сервисы, источники): их собственные списки тоже могли прийти пустыми значениями
            if (value is IList list && list.GetType().IsGenericType)
            {
                var itemType = list.GetType().GetGenericArguments()[0];
                if (itemType == typeof(string) || itemType.IsValueType || itemType.GetConstructor(Type.EmptyTypes) == null) continue;
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] == null) { list.RemoveAt(i); fixedCount++; continue; }
                    fixedCount += Fill(list[i]!, Activator.CreateInstance(itemType), depth + 1);
                }
            }
        }
        return fixedCount;
    }

    private static bool IsCollection(Type t) => t != typeof(string) && typeof(IEnumerable).IsAssignableFrom(t);

    private static object? Create(Type t)
    {
        try { return t.IsInterface || t.IsAbstract ? null : Activator.CreateInstance(t); } catch (Exception) { return null; }
    }

    /// <summary>Копия значения по умолчанию через JSON: правка настроек не должна менять общий экземпляр.</summary>
    private static object? Clone(object value)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(value, value.GetType(), Storage.Options), value.GetType(), Storage.Options); }
        catch (Exception) { return Create(value.GetType()); }
    }
}
