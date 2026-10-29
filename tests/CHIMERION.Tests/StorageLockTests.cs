using System.IO;
using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>settings.json временно занят другой программой (антивирус, резервное копирование).</summary>
[Collection("scenarios")]
public class StorageLockTests : IDisposable
{
    private readonly TimeSpan _read = Storage.ReadWait, _pending = Storage.PendingFor;
    private const string File1 = "lock-test.json";
    private static string PathOf => Storage.PathOf(File1);

    public StorageLockTests()
    {
        Storage.ReadWait = TimeSpan.FromMilliseconds(400);
        Storage.PendingFor = TimeSpan.FromSeconds(10);
        if (File.Exists(PathOf)) File.Delete(PathOf);
    }

    public void Dispose()
    {
        Storage.FlushPending(TimeSpan.FromSeconds(5));
        Storage.ReadWait = _read; Storage.PendingFor = _pending;
        if (File.Exists(PathOf)) File.Delete(PathOf);
    }

    private static FileStream LockFile() => new(PathOf, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public void Save_While_Locked_Is_Written_After_Release()
    {
        Storage.Save(File1, new AppSettings { TrayCheckMinutes = 7 });
        var lk = LockFile();
        Storage.Save(File1, new AppSettings { TrayCheckMinutes = 33 }); // не бросает, запись отложена
        Assert.True(Storage.HasPending);
        lk.Dispose();
        Assert.True(Storage.FlushPending(TimeSpan.FromSeconds(8)));
        Assert.Equal(33, Storage.LoadOrCreate(File1, () => new AppSettings()).TrayCheckMinutes);
    }

    [Fact]
    public void Latest_Of_Several_Locked_Saves_Wins()
    {
        Storage.Save(File1, new AppSettings { TrayCheckMinutes = 7 });
        var lk = LockFile();
        Storage.Save(File1, new AppSettings { TrayCheckMinutes = 20 });
        Storage.Save(File1, new AppSettings { TrayCheckMinutes = 25 });
        lk.Dispose();
        Assert.True(Storage.FlushPending(TimeSpan.FromSeconds(8)));
        Assert.Equal(25, Storage.LoadOrCreate(File1, () => new AppSettings()).TrayCheckMinutes);
    }

    [Fact]
    public void Defaults_Read_During_Lock_Do_Not_Overwrite_Real_Settings()
    {
        Storage.Save(File1, new AppSettings { TrayCheckMinutes = 42 });
        AppSettings loaded;
        using (LockFile()) loaded = Storage.LoadOrCreate(File1, () => new AppSettings()); // занят: значения по умолчанию
        Assert.Equal(new AppSettings().TrayCheckMinutes, loaded.TrayCheckMinutes);
        // Файл уже свободен, но прочитан он не был: запись значений по умолчанию не должна затереть 42 без повторного чтения.
        // Здесь чтение проходит, поэтому запись разрешается: проверяем, что до чтения она откладывалась
        string? reread = null;
        Storage.Reread = f => reread = f;
        try
        {
            Storage.Save(File1, loaded); // файл уже свободен, но в памяти значения по умолчанию
            Storage.FlushPending(TimeSpan.FromSeconds(8));
            Assert.Equal(File1, reread);
            Assert.Equal(42, Storage.LoadOrCreate(File1, () => new AppSettings()).TrayCheckMinutes);
        }
        finally { Storage.Reread = null; }
    }

    [Fact]
    public async Task Load_Waits_For_Short_Lock_And_Gets_Real_Values()
    {
        Storage.Save(File1, new AppSettings { TrayCheckMinutes = 42 });
        Storage.ReadWait = TimeSpan.FromSeconds(3);
        var lk = LockFile();
        var release = Task.Run(async () => { await Task.Delay(600); lk.Dispose(); });
        var loaded = Storage.LoadOrCreate(File1, () => new AppSettings());
        await release;
        Assert.Equal(42, loaded.TrayCheckMinutes);
    }
    [Fact]
    public async Task Old_Pending_Snapshot_Does_Not_Overwrite_Newer_Save()
    {
        Storage.Save(File1, new AppSettings { TrayCheckMinutes = 7 });
        using var captured = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        try
        {
            Storage.PendingSnapshot = path =>
            {
                if (path == PathOf) { captured.Set(); resume.Wait(TimeSpan.FromSeconds(10)); }
            };
            using (LockFile()) Storage.Save(File1, new AppSettings { TrayCheckMinutes = 20 });
            Assert.True(await Task.Run(() => captured.Wait(TimeSpan.FromSeconds(8))));
            Storage.Save(File1, new AppSettings { TrayCheckMinutes = 33 });
        }
        finally { resume.Set(); Storage.PendingSnapshot = null; }
        Assert.True(await Task.Run(() => Storage.FlushPending(TimeSpan.FromSeconds(8))));
        Assert.Equal(33, Storage.LoadOrCreate(File1, () => new AppSettings()).TrayCheckMinutes);
    }

    [Fact]
    public void Required_Save_Fails_When_File_Is_Locked_Without_Queueing()
    {
        Storage.Save(File1, new AppSettings { TrayCheckMinutes = 7 });
        using (LockFile())
            Assert.Throws<IOException>(() => Storage.SaveRequired(File1, new AppSettings { TrayCheckMinutes = 33 }));
        Assert.False(Storage.HasPending);
        Assert.Equal(7, Storage.LoadOrCreate(File1, () => new AppSettings()).TrayCheckMinutes);
    }
}
