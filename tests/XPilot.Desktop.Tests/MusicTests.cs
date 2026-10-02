using NVorbis;
using XPilot.Desktop.Audio;

namespace XPilot.Desktop.Tests;

public class MusicTests
{
    [Theory]
    [InlineData("zephiramusic-lofi-trance-596433.ogg", "ZEPHIRAMUSIC LOFI TRANCE")]
    [InlineData("openmindaudio-party-trance-iwato-603199.ogg", "OPENMINDAUDIO PARTY TRANCE IWATO")]
    [InlineData("theme_2.ogg", "THEME 2")]
    [InlineData("1999.ogg", "1999")]
    public void DisplayName_DropsSeparatorsAndTrailingIds(string file, string expected) =>
        Assert.Equal(expected, MusicPlayer.DisplayName(file));

    [Fact]
    public void EveryShippedTrack_Decodes()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "music");
        if (!Directory.Exists(dir)) return;
        var buffer = new float[48000];
        foreach (var file in Directory.GetFiles(dir, "*.ogg"))
        {
            using var reader = new VorbisReader(file);
            Assert.InRange(reader.Channels, 1, 2);
            Assert.True(reader.TotalTime > TimeSpan.FromSeconds(30), $"{Path.GetFileName(file)} is only {reader.TotalTime}");
            Assert.True(reader.ReadSamples(buffer, 0, buffer.Length) > 0, $"{Path.GetFileName(file)} decoded nothing");
        }
    }
}
