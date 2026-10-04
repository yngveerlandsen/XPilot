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
    private float _fps;
    private bool _screenshotRequested;
    private double _elapsed;
    /// <summary>Seconds after start to take a screenshot and quit (the --screenshot-after developer option).</summary>
    private double? _screenshotThenQuitAt;
    private string? _notice;
    private float _noticeTimer;

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
        _graphics.PreparingDeviceSettings += (_, e) =>
            e.GraphicsDeviceInformation.PresentationParameters.MultiSampleCount = Settings.Antialiasing ? 4 : 0;
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
    public MusicPlayer Music { get; private set; } = null!;
    public MapCatalog Maps { get; private set; } = null!;
    public Starfield Starfield { get; } = new();
    /// <summary>Background updates for installed copies.</summary>
    public GameUpdater Updater { get; } = GameUpdater.Start();
    /// <summary>The bot fight behind the menus.</summary>
    public MenuBackground Background { get; private set; } = null!;

    protected override void Initialize()
    {
        Settings = Settings.Load();
        ApplyVideoSettings();
        base.Initialize();
    }

    protected override void LoadContent()
    {
        Primitives = new PrimitiveBatch(GraphicsDevice);
        Sounds = SoundBank.Create(Settings.Volume);
        Music = MusicPlayer.Load();
        Maps = MapCatalog.Load();
        foreach (var error in Maps.Errors) Console.Error.WriteLine($"Map error: {error}");
        Background = MenuBackground.Create(Maps);
        var args = Environment.GetCommandLineArgs();
        int shotArg = Array.IndexOf(args, "--screenshot-after");
        if (shotArg >= 0 && shotArg + 1 < args.Length && double.TryParse(args[shotArg + 1], out double seconds)) _screenshotThenQuitAt = seconds;
        SetScreen(QuickStartScreen() ?? new MainMenuScreen(this));
    }

    /// <summary>
    /// Developer shortcuts that skip the menu: <c>XPilot --map arena [--bots 5] [--difficulty hard] [--spectate]</c>
    /// plays locally, <c>XPilot --connect host[:port] [--name Ace]</c> joins a server, and <c>XPilot --join</c>
    /// opens the join screen.
    /// </summary>
    private Screen? QuickStartScreen()
    {
        var args = Environment.GetCommandLineArgs();
        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (args.Contains("--join")) return new JoinScreen(this);

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

    /// <summary>Applies the fullscreen, VSync and antialiasing settings, keeping the window's size unless fullscreen changes.</summary>
    public void ApplyVideoSettings()
    {
        _graphics.SynchronizeWithVerticalRetrace = Settings.VSync;
        _graphics.PreferMultiSampling = Settings.Antialiasing;
        if (_graphics.IsFullScreen != Settings.Fullscreen) SetFullscreen(Settings.Fullscreen);
        else _graphics.ApplyChanges();
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

        bool audible = IsActive || !Settings.MuteInBackground;
        Sounds.Volume = audible ? Settings.Volume : 0f;
        Music.Volume = audible ? Settings.MusicVolume : 0f;
        if (Settings.MusicVolume > 0f && Sounds.Enabled) Music.Update();
        else if (Music.NowPlaying != null) Music.Stop();
        _elapsed += dt;
        if (Input.WasPressed(Keys.F12) || _elapsed >= _screenshotThenQuitAt) _screenshotRequested = true;
        _noticeTimer = MathF.Max(0f, _noticeTimer - dt);

        _screen?.Update(dt);
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Palette.Background);
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _screen?.Draw(dt);
        if (Settings.ShowFps && dt > 0f)
        {
            _fps = _fps <= 0f ? 1f / dt : MathHelper.Lerp(_fps, 1f / dt, 0.05f);
            var vp = GraphicsDevice.Viewport;
            float s = vp.Height / 720f;
            Primitives.Begin(Matrix.Identity, PrimitiveBatch.Additive);
            VectorFont.Draw(Primitives, $"{_fps:0} FPS", new Vector2(vp.Width / 2f, vp.Height - 18 * s), 9f * s, Palette.TextDim, TextAlign.Center);
            Primitives.End();
        }
        if (_screenshotRequested)
        {
            _screenshotRequested = false;
            SaveScreenshot();
            if (_elapsed >= _screenshotThenQuitAt) Exit();
        }
        if (_noticeTimer > 0f && _notice != null)
        {
            var vp = GraphicsDevice.Viewport;
            float s = vp.Height / 720f;
            Primitives.Begin(Matrix.Identity, PrimitiveBatch.Additive);
            VectorFont.Draw(Primitives, _notice, new Vector2(vp.Width / 2f, 40 * s), 11f * s, Palette.Accent * MathF.Min(1f, _noticeTimer), TextAlign.Center);
            Primitives.End();
        }
        base.Draw(gameTime);
    }

    /// <summary>Saves what is on screen as a PNG in the Pictures folder (F12).</summary>
    private void SaveScreenshot()
    {
        try
        {
            var pp = GraphicsDevice.PresentationParameters;
            int w = pp.BackBufferWidth, h = pp.BackBufferHeight;
            var pixels = new Color[w * h];
            GraphicsDevice.GetBackBufferData(pixels);
            // The game draws additively, which leaves the alpha channel meaningless; a screenshot is opaque.
            for (int i = 0; i < pixels.Length; i++) pixels[i].A = 255;
            using var texture = new Texture2D(GraphicsDevice, w, h);
            texture.SetData(pixels);

            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pictures)) pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = Path.Combine(pictures, "XPilot");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"xpilot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            using (var file = File.Create(path)) texture.SaveAsPng(file, w, h);
            Console.WriteLine($"Screenshot saved: {path}");
            _notice = $"SCREENSHOT SAVED TO {dir.ToUpperInvariant()}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            Console.Error.WriteLine($"Screenshot failed: {ex.Message}");
            _notice = "SCREENSHOT FAILED";
        }
        _noticeTimer = 2.5f;
    }

    protected override void UnloadContent()
    {
        _screen?.Leave();
        _screen = null;
        Music?.Dispose();
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
