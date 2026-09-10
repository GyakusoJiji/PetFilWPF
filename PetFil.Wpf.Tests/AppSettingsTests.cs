using System;
using System.IO;
using Xunit;

namespace PetFil.Wpf.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string path = Path.Combine(
        Path.GetTempPath(), $"petfil-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void SavedAndLastSurviveARoundTrip()
    {
        var written = new AppSettings
        {
            Last = new PetFilPreset { Temp = 215, Speed = 280 },
            Saved = new PetFilPreset { Temp = 200, Speed = 300 },
        };
        Assert.True(written.Save(path));

        var read = AppSettings.Load(path);
        Assert.Equal(215, read.Last!.Temp, 3);
        Assert.Equal(280, read.Last!.Speed, 3);
        Assert.Equal(200, read.Saved!.Temp, 3);
        Assert.Equal(300, read.Saved!.Speed, 3);
    }

    [Fact]
    public void AutoSavingLastLeavesTheSavedPresetAlone()
    {
        // 保存ボタンの値は終了時の自動保存で消えてはいけない。
        var settings = new AppSettings { Saved = new PetFilPreset { Temp = 200, Speed = 300 } };
        settings.Save(path);

        var reopened = AppSettings.Load(path);
        reopened.Last = new PetFilPreset { Temp = 250, Speed = 120 };
        reopened.Save(path);

        var read = AppSettings.Load(path);
        Assert.Equal(200, read.Saved!.Temp, 3);
        Assert.Equal(300, read.Saved!.Speed, 3);
        Assert.Equal(250, read.Last!.Temp, 3);
    }

    [Fact]
    public void MissingFileGivesEmptySettings()
    {
        var read = AppSettings.Load(path);
        Assert.Null(read.Last);
        Assert.Null(read.Saved);
    }

    [Fact]
    public void BrokenFileDoesNotThrow()
    {
        // 壊れた設定ファイルで起動できなくなるより既定値で立ち上がるほうがよい。
        File.WriteAllText(path, "{ this is not json");
        var read = AppSettings.Load(path);
        Assert.Null(read.Last);
        Assert.Null(read.Saved);
    }
}
