using Microsoft.Xna.Framework.Input;
using XPilot.Core.Simulation;

namespace XPilot.Desktop.Input;

public enum GameAction { TurnLeft, TurnRight, Thrust, Fire, Shield, Grab }

/// <summary>Maps keys to ship controls. Presets can be overridden per action in settings.json.</summary>
public sealed class InputBindings
{
    public const string ModernPreset = "Modern";
    public const string ClassicPreset = "Classic";

    private readonly Dictionary<GameAction, Keys[]> _keys;

    private InputBindings(string name, Dictionary<GameAction, Keys[]> keys)
    {
        Name = name;
        _keys = keys;
    }

    public string Name { get; }

    public static InputBindings Modern() => new(ModernPreset, new()
    {
        [GameAction.TurnLeft] = [Keys.Left, Keys.A],
        [GameAction.TurnRight] = [Keys.Right, Keys.D],
        [GameAction.Thrust] = [Keys.Up, Keys.W],
        [GameAction.Fire] = [Keys.Space, Keys.LeftControl, Keys.RightControl],
        [GameAction.Shield] = [Keys.Down, Keys.S, Keys.LeftShift],
        [GameAction.Grab] = [Keys.E, Keys.RightShift],
    });

    /// <summary>The original XPilot layout: A/S turn, Shift thrusts, Enter fires, Space shields.</summary>
    public static InputBindings Classic() => new(ClassicPreset, new()
    {
        [GameAction.TurnLeft] = [Keys.A],
        [GameAction.TurnRight] = [Keys.S],
        [GameAction.Thrust] = [Keys.LeftShift, Keys.RightShift],
        [GameAction.Fire] = [Keys.Enter],
        [GameAction.Shield] = [Keys.Space],
        [GameAction.Grab] = [Keys.LeftControl, Keys.RightControl],
    });

    public static InputBindings FromSettings(Settings settings)
    {
        var bindings = settings.ControlPreset == ClassicPreset ? Classic() : Modern();
        if (settings.CustomBindings == null) return bindings;
        foreach (var (actionName, keyNames) in settings.CustomBindings)
        {
            if (!Enum.TryParse<GameAction>(actionName, true, out var action)) continue;
            var keys = keyNames
                .Select(k => Enum.TryParse<Keys>(k, true, out var key) ? key : Keys.None)
                .Where(k => k != Keys.None)
                .ToArray();
            if (keys.Length > 0) bindings._keys[action] = keys;
        }
        return bindings;
    }

    public IReadOnlyList<Keys> KeysFor(GameAction action) => _keys[action];

    /// <summary>Keys the game itself uses (pause, scores, chat, fullscreen), which can't be bound to actions.</summary>
    public static bool IsReserved(Keys key) => key is Keys.Escape or Keys.P or Keys.Tab or Keys.T or Keys.F11 or Keys.F5;

    /// <summary>
    /// Makes <paramref name="key"/> the only key for <paramref name="action"/>. An action that used the key
    /// loses it, or takes over the rebound action's old keys if it had no others, so nothing ends up unbound.
    /// </summary>
    public static void Rebind(Settings settings, GameAction action, Keys key)
    {
        var current = FromSettings(settings);
        var custom = settings.CustomBindings ?? [];
        foreach (var other in Enum.GetValues<GameAction>())
        {
            if (other == action || !current._keys[other].Contains(key)) continue;
            var remaining = current._keys[other].Where(k => k != key).ToArray();
            if (remaining.Length == 0) remaining = current._keys[action].Where(k => k != key).ToArray();
            custom[other.ToString()] = remaining.Select(k => k.ToString()).ToArray();
        }
        custom[action.ToString()] = [key.ToString()];
        settings.CustomBindings = custom;
    }

    public static string ActionName(GameAction action) => action switch
    {
        GameAction.TurnLeft => "TURN LEFT",
        GameAction.TurnRight => "TURN RIGHT",
        GameAction.Grab => "GRAB BALL",
        _ => action.ToString().ToUpperInvariant(),
    };

    public string Describe(GameAction action) => string.Join("/", _keys[action].Select(KeyName));

    public bool IsDown(InputState input, GameAction action) => _keys[action].Any(input.IsDown);

    public ShipInput Read(InputState input)
    {
        float turn = (IsDown(input, GameAction.TurnRight) ? 1f : 0f) - (IsDown(input, GameAction.TurnLeft) ? 1f : 0f);
        bool thrust = IsDown(input, GameAction.Thrust);
        bool fire = IsDown(input, GameAction.Fire);
        bool shield = IsDown(input, GameAction.Shield);
        bool grab = IsDown(input, GameAction.Grab);

        var pad = input.Pad;
        if (pad.IsConnected)
        {
            float stick = pad.ThumbSticks.Left.X;
            if (MathF.Abs(stick) > 0.25f) turn += stick;
            if (pad.IsButtonDown(Buttons.DPadLeft)) turn -= 1f;
            if (pad.IsButtonDown(Buttons.DPadRight)) turn += 1f;
            thrust |= pad.Triggers.Right > 0.3f || pad.IsButtonDown(Buttons.A);
            fire |= pad.IsButtonDown(Buttons.RightShoulder) || pad.IsButtonDown(Buttons.X);
            shield |= pad.Triggers.Left > 0.3f || pad.IsButtonDown(Buttons.LeftShoulder) || pad.IsButtonDown(Buttons.B);
            grab |= pad.IsButtonDown(Buttons.Y);
        }

        return new ShipInput { Turn = Math.Clamp(turn, -1f, 1f), Thrust = thrust, Fire = fire, Shield = shield, Grab = grab };
    }

    public static string KeyName(Keys key) => key switch
    {
        Keys.Left => "LEFT",
        Keys.Right => "RIGHT",
        Keys.Up => "UP",
        Keys.Down => "DOWN",
        Keys.LeftControl => "CTRL",
        Keys.RightControl => "RCTRL",
        Keys.LeftShift => "SHIFT",
        Keys.RightShift => "RSHIFT",
        _ => key.ToString().ToUpperInvariant(),
    };
}
