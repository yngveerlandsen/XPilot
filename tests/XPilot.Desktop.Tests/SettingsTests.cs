using System.Text.Json;
using Microsoft.Xna.Framework.Input;
using XPilot.Desktop.Input;

namespace XPilot.Desktop.Tests;

public class SettingsTests
{
    [Fact]
    public void Rebind_ToAFreeKey_MakesItTheOnlyKey()
    {
        var settings = new Settings();
        InputBindings.Rebind(settings, GameAction.Fire, Keys.J);
        var bindings = InputBindings.FromSettings(settings);
        Assert.Equal([Keys.J], bindings.KeysFor(GameAction.Fire));
        Assert.Equal([Keys.Up, Keys.W], bindings.KeysFor(GameAction.Thrust));
    }

    [Fact]
    public void Rebind_TakesTheKeyAwayFromAnotherAction()
    {
        var settings = new Settings();
        InputBindings.Rebind(settings, GameAction.Fire, Keys.W);
        var bindings = InputBindings.FromSettings(settings);
        Assert.Equal([Keys.W], bindings.KeysFor(GameAction.Fire));
        Assert.Equal([Keys.Up], bindings.KeysFor(GameAction.Thrust));
    }

    [Fact]
    public void Rebind_SwapsWhenTheOtherActionWouldBeLeftWithNoKeys()
    {
        var settings = new Settings { ControlPreset = InputBindings.ClassicPreset };
        InputBindings.Rebind(settings, GameAction.Fire, Keys.Space);
        var bindings = InputBindings.FromSettings(settings);
        Assert.Equal([Keys.Space], bindings.KeysFor(GameAction.Fire));
        Assert.Equal([Keys.Enter], bindings.KeysFor(GameAction.Shield));
    }

    [Theory]
    [InlineData(Keys.Escape)]
    [InlineData(Keys.Tab)]
    [InlineData(Keys.T)]
    [InlineData(Keys.P)]
    public void GameKeys_AreReserved(Keys key) => Assert.True(InputBindings.IsReserved(key));

    [Fact]
    public void Settings_RoundTripThroughJson_WithoutDerivedValues()
    {
        var settings = new Settings { ScoreLimit = 0, TimeLimitMinutes = 5, Laps = 4, Particles = "High", ScreenShake = 0.5f };
        var json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("TimeLimitSeconds", json);
        Assert.DoesNotContain("ParticleDensity", json);

        var copy = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal(0, copy.ScoreLimit);
        Assert.Equal(300f, copy.TimeLimitSeconds);
        Assert.Equal(4, copy.LapsOrDefault);
        Assert.Equal(1.6f, copy.ParticleDensity);
        Assert.Equal(0.5f, copy.ScreenShake);
    }

    [Theory]
    [InlineData(null, Settings.DefaultMasterServer)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(" example.com:2000 ", "example.com:2000")]
    public void MasterServer_DefaultsToThePublicList_AndCanBeCleared(string? stored, string? effective) =>
        Assert.Equal(effective, new Settings { MasterServer = stored }.EffectiveMasterServer);

    [Fact]
    public void OlderSettingsFiles_WithoutAMasterServer_StayLanOnly()
    {
        var settings = Settings.Parse("""{ "MasterServer": null, "PlayerName": "Old" }""");
        Assert.Null(settings.EffectiveMasterServer);
        Assert.False(settings.ListHostedGames);
        Assert.Equal(Settings.CurrentVersion, settings.Version);
    }

    [Fact]
    public void OlderSettingsFiles_WithTheirOwnMasterServer_KeepListingHostedGames()
    {
        var settings = Settings.Parse("""{ "MasterServer": "games.example.com" }""");
        Assert.Equal("games.example.com", settings.EffectiveMasterServer);
        Assert.True(settings.ListHostedGames);
    }

    [Fact]
    public void CurrentSettingsFiles_AreLeftAsTheyAre()
    {
        var settings = Settings.Parse($$"""{ "Version": {{Settings.CurrentVersion}}, "MasterServer": null }""");
        Assert.Equal(Settings.DefaultMasterServer, settings.EffectiveMasterServer);
        Assert.False(settings.ListHostedGames);
    }

    [Fact]
    public void MatchRuleDefaults_LeaveTheModeAndMapInCharge()
    {
        var settings = new Settings();
        Assert.Null(settings.TimeLimitSeconds);
        Assert.Null(settings.LapsOrDefault);
    }
}
