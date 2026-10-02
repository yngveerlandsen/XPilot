using XPilot.Desktop.Audio;
using XPilot.Desktop.Graphics;
using XPilot.Desktop.Input;

namespace XPilot.Desktop.Screens;

public abstract class Screen(XPilotGame game)
{
    protected XPilotGame Game { get; } = game;
    protected InputState Input => Game.Input;
    protected PrimitiveBatch Primitives => Game.Primitives;
    protected SoundBank Sounds => Game.Sounds;

    public virtual void Enter()
    {
    }

    /// <summary>Called when another screen replaces this one, or the game exits.</summary>
    public virtual void Leave()
    {
    }

    public abstract void Update(float dt);

    public abstract void Draw(float dt);
}
