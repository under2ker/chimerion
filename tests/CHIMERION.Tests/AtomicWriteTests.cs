using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Запись через временный файл (аудит P-12): содержимое заменяется целиком, временных файлов не остается.</summary>
public class AtomicWriteTests
{
    [Fact]
    public void Replaces_whole_file_and_leaves_no_temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"chimerion-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "device.lock");
            File.WriteAllText(path, "старое содержимое, длиннее нового");
            Storage.WriteAllBytesAtomic(path, new byte[] { 1, 2, 3 });
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
