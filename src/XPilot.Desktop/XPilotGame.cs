using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XPilot.Desktop.Audio;
using XPilot.Desktop.Graphics;
using XPilot.Desktop.Input;
using XPilot.Desktop.Screens;

namespace XPilot.Desktop;

public sealed class XPilotGame : Game
{
    private const int WindowedWidth = 1280;
    private const int WindowedHeight = 720;

    private readonly GraphicsDeviceManager _graphics;
    private Screen? _screen;
    private Screen? _pendingScreen;

    public XPilotGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = WindowedWidth,
            PreferredBackBufferHeight = WindowedHeight,
            PreferMultiSampling = true,
            SynchronizeWithVerticalRetrace = true,
            GraphicsProfile = GraphicsProfile.HiDef,
            HardwareModeSwitch = false,
        };
        _graphics.PreparingDeviceSettings += (_, e) => e.GraphicsDeviceInformation.PresentationParameters.MultiSampleCount = 4;
        IsFixedTimeStep = false;
        IsMouseVisible = false;
        Window.Title = "XPilot";
        Window.AllowUserResizing = true;
        Window.ClientSizeChanged += OnClientSizeChanged;
        Window.TextInput += (_, e) => Input.OnTextInput(e.Character);
    }

    public Settings Settings { get; private set; } = new();
    public InputState Input { get; } = new();
    public PrimitiveBatch Primitives { get; private set; } = null!;
    public SoundBank Sounds { get; private set; } = null!;
    public MapCatalog Maps { get; private set; } = null!;
    public Starfield Starfield { get; } = new();

    protected override void Initialize()
    {
        Settings = Settings.Load();
        if (Settings.Fullscreen) SetFullscreen(true);
        base.Initialize();
    }

    protected override void LoadContent()
    {
        Primitives = new PrimitiveBatch(GraphicsDevice);
        Sounds = SoundBank.Create(Settings.Volume);
        Maps = MapCatalog.Load();
        foreach (var error in Maps.Errors) Console.Error.WriteLine($"Map error: {error}");
        SetScreen(QuickStartScreen() ?? new MainMenuScreen(this));
    }

    /// <summary>
    /// Developer shortcuts that skip the menu: <c>XPilot --map arena [--bots 5] [--difficulty hard] [--spectate]</c>
    /// plays locally, and <c>XPilot --connect host[:port] [--name Ace]</c> joins a server.
    /// </summary>
    private Screen? QuickStartScreen()
    {
        var args = Environment.GetCommandLineArgs();
        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Arg("--connect") is { } address)
        {
            var connection = new Net.ClientConnection(Arg("--name") ?? Settings.PlayerName);
            connection.Connect(address);
            return new PlayScreen(this, new NetworkSession(connection, null));
        }

        var mapName = Arg("--map");
        if (mapName == null) return null;
        var map = Maps.Maps.FirstOrDefault(m =>
            string.Equals(m.Name, mapName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(m.SourcePath), mapName, StringComparison.OrdinalIgnoreCase));
        if (map == null)
        {
            Console.Error.WriteLine($"Unknown map '{mapName}'.");
            return null;
        }
        return new PlayScreen(this, new Core.MatchSetup
        {
            Map = map,
            BotCount = int.TryParse(Arg("--bots"), out int bots) ? bots : 3,
            Difficulty = Enum.TryParse<Core.AI.BotDifficulty>(Arg("--difficulty"), true, out var d) ? d : Core.AI.BotDifficulty.Normal,
            IncludePlayer = !args.Contains("--spectate"),
            PlayerName = Settings.PlayerName,
        });
    }

    /// <summary>Switches screens at the start of the next update.</summary>
    public void SetScreen(Screen screen) => _pendingScreen = screen;

    protected override void Update(GameTime gameTime)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        Input.Update(IsActive);

        if (_pendingScreen != null)
        {
            _screen?.Leave();
            _screen = _pendingScreen;
            _pendingScreen = null;
            _screen.Enter();
        }

        if (Input.WasPressed(Keys.F11) || (Input.IsDown(Keys.LeftAlt) && Input.WasPressed(Keys.Enter)))
        {
            SetFullscreen(!_graphics.IsFullScreen);
            Settings.Fullscreen = _graphics.IsFullScreen;
            Settings.Save();
        }

        _screen?.Update(dt);
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Palette.Background);
        _screen?.Draw((float)gameTime.ElapsedGameTime.TotalSeconds);
        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        _screen?.Leave();
        _screen = null;
        Sounds?.Dispose();
        Primitives?.Dispose();
        base.UnloadContent();
    }

    private void SetFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            var mode = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
            _graphics.PreferredBackBufferWidth = mode.Width;
            _graphics.PreferredBackBufferHeight = mode.Height;
        }
        else
        {
            _graphics.PreferredBackBufferWidth = WindowedWidth;
            _graphics.PreferredBackBufferHeight = WindowedHeight;
        }
        _graphics.IsFullScreen = fullscreen;
        _graphics.ApplyChanges();
    }

    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        if (_graphics.IsFullScreen) return;
        var bounds = Window.ClientBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (bounds.Width == _graphics.PreferredBackBufferWidth && bounds.Height == _graphics.PreferredBackBufferHeight) return;
        _graphics.PreferredBackBufferWidth = bounds.Width;
        _graphics.PreferredBackBufferHeight = bounds.Height;
        _graphics.ApplyChanges();
    }
}
