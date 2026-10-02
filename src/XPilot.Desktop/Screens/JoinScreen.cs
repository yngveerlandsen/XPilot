using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using XPilot.Desktop.Graphics;
using XPilot.Net;

namespace XPilot.Desktop.Screens;

/// <summary>Lists servers found on the LAN (and the internet, with a master server), or takes an address.</summary>
public sealed class JoinScreen(XPilotGame game) : Screen(game)
{
    private const int MaxAddressLength = 64;

    private readonly ServerBrowser _browser = new(game.Settings.EffectiveMasterServer);
    private string _address = game.Settings.LastAddress;
    private bool _editing;
    /// <summary>0 is the address line, then one row per server, then BACK.</summary>
    private int _selected;
    private float _time;

    private IReadOnlyList<ServerEntry> Servers => _browser.Servers;
    private int RowCount => Servers.Count + 2;

    public override void Leave() => _browser.Dispose();

    public override void Update(float dt)
    {
        _time += dt;
        Game.Background.Update(dt, Game.Settings.ParticleDensity);
        _browser.Poll();
        _selected = Math.Clamp(_selected, 0, RowCount - 1);

        if (_editing)
        {
            if (Input.WasPressed(Keys.Escape)) _editing = false;
            else if (Input.WasPressed(Keys.Enter)) ConnectTo(_address);
            else _address = Input.EditText(_address, MaxAddressLength);
            return;
        }

        if (Input.MenuUp) Move(-1);
        if (Input.MenuDown) Move(1);
        if (Input.WasPressed(Keys.F5)) _browser.Refresh();
        if (Input.MenuBack)
        {
            Game.SetScreen(new MainMenuScreen(Game));
            return;
        }
        if (!Input.MenuSelect) return;

        if (_selected == 0)
        {
            _editing = true;
        }
        else if (_selected == RowCount - 1)
        {
            Game.SetScreen(new MainMenuScreen(Game));
        }
        else
        {
            Join(Servers[_selected - 1]);
        }
    }

    private void Move(int delta)
    {
        _selected = (_selected + delta + RowCount) % RowCount;
        Sounds.Play("select", 0.6f);
    }

    private void ConnectTo(string address)
    {
        address = address.Trim();
        if (address.Length == 0) return;
        Game.Settings.LastAddress = address;
        Game.Settings.Save();
        var connection = new ClientConnection(Game.Settings.PlayerName);
        connection.Connect(address);
        Start(connection);
    }

    private void Join(ServerEntry server)
    {
        var connection = new ClientConnection(Game.Settings.PlayerName);
        if (!server.OnLan && server.ServerId != null && _browser.Master != null)
        {
            connection.ConnectViaMaster(_browser.Master, server.ServerId, server.EndPoint);
        }
        else
        {
            connection.Connect(server.EndPoint);
        }
        Start(connection);
    }

    private void Start(ClientConnection connection)
    {
        Sounds.Play("go", 0.7f);
        Game.SetScreen(new PlayScreen(Game, new NetworkSession(connection, null)));
    }

    public override void Draw(float dt)
    {
        var vp = Game.GraphicsDevice.Viewport;
        float s = vp.Height / 720f;
        float cx = vp.Width / 2f;
        var pb = Primitives;

        Game.Background.Draw(Game, pb, vp);
        pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);
        VectorFont.Draw(pb, "JOIN GAME", new Vector2(cx, 60 * s), 40f * s, Palette.Accent, TextAlign.Center, 3f * s);
        VectorFont.Draw(pb, $"PLAYING AS {Game.Settings.PlayerName.ToUpperInvariant()}", new Vector2(cx, 120 * s), 11f * s, Palette.TextDim, TextAlign.Center);

        float y = 170 * s;
        var color = RowColor(0);
        string address = _editing ? _address + (((int)(_time * 3f) & 1) == 0 ? "_" : " ") : _address.Length > 0 ? _address : "(TYPE AN ADDRESS)";
        VectorFont.Draw(pb, "ADDRESS", new Vector2(cx - 24 * s, y), 16f * s, color, TextAlign.Right);
        VectorFont.Draw(pb, address.ToUpperInvariant(), new Vector2(cx + 24 * s, y), 16f * s, _editing ? Palette.Accent : color);

        y += 60 * s;
        float left = cx - 420 * s;
        VectorFont.Draw(pb, "SERVER", new Vector2(left, y), 10f * s, Palette.TextDim);
        VectorFont.Draw(pb, "MAP", new Vector2(left + 380 * s, y), 10f * s, Palette.TextDim);
        VectorFont.Draw(pb, "PLAYERS", new Vector2(left + 640 * s, y), 10f * s, Palette.TextDim);
        VectorFont.Draw(pb, "WHERE", new Vector2(left + 760 * s, y), 10f * s, Palette.TextDim);
        y += 28 * s;

        if (Servers.Count == 0)
        {
            string searching = "SEARCHING" + new string('.', (int)(_time * 2f) % 4);
            VectorFont.Draw(pb, searching, new Vector2(left, y), 13f * s, Palette.TextDim * 0.8f);
            y += 30 * s;
        }
        for (int i = 0; i < Servers.Count; i++)
        {
            var server = Servers[i];
            var info = server.Info;
            var rowColor = RowColor(i + 1);
            if (_selected == i + 1) VectorFont.Draw(pb, ">", new Vector2(left - 24 * s, y), 13f * s, rowColor);
            VectorFont.Draw(pb, Truncate(info.Name, 28).ToUpperInvariant(), new Vector2(left, y), 13f * s, rowColor);
            VectorFont.Draw(pb, $"{Truncate(info.MapName, 14)} {info.Mode}".ToUpperInvariant(), new Vector2(left + 380 * s, y), 13f * s, rowColor);
            VectorFont.Draw(pb, $"{info.Humans}/{info.Capacity}", new Vector2(left + 640 * s, y), 13f * s, rowColor);
            VectorFont.Draw(pb, server.OnLan ? "LAN" : "INTERNET", new Vector2(left + 760 * s, y), 13f * s, rowColor);
            y += 30 * s;
        }

        y += 16 * s;
        VectorFont.Draw(pb, _selected == RowCount - 1 ? ">  BACK  <" : "BACK", new Vector2(cx, y), 16f * s, RowColor(RowCount - 1), TextAlign.Center);

        string master = _browser.HasMaster
            ? $"INTERNET LIST FROM {Game.Settings.EffectiveMasterServer!.ToUpperInvariant()}"
            : "LAN ONLY - SET A MASTER SERVER IN SETTINGS > NETWORK FOR INTERNET GAMES";
        VectorFont.Draw(pb, master, new Vector2(cx, vp.Height - 80 * s), 10f * s, Palette.TextDim * 0.8f, TextAlign.Center);
        VectorFont.Draw(pb, _editing ? "TYPE HOST OR HOST:PORT   ENTER CONNECT   ESC DONE" : "ARROWS CHOOSE   ENTER JOIN   F5 REFRESH   ESC BACK",
            new Vector2(cx, vp.Height - 50 * s), 10f * s, Palette.TextDim * 0.6f, TextAlign.Center);
        pb.End();
    }

    private Color RowColor(int row)
    {
        if (row != _selected) return Palette.TextDim * 0.8f;
        return Palette.Text * (0.85f + 0.15f * MathF.Sin(_time * 8f));
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + ".";
}
