using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace XPilot.Desktop.Input;

/// <summary>Keyboard and gamepad state for this frame and the previous one.</summary>
public sealed class InputState
{
    private KeyboardState _keys, _prevKeys;
    private GamePadState _pad, _prevPad;

    public GamePadState Pad => _pad;

    public void Update(bool windowActive)
    {
        _prevKeys = _keys;
        _prevPad = _pad;
        _keys = windowActive ? Keyboard.GetState() : default;
        _pad = windowActive ? GamePad.GetState(PlayerIndex.One) : default;
    }

    public bool IsDown(Keys key) => _keys.IsKeyDown(key);
    public bool WasPressed(Keys key) => _keys.IsKeyDown(key) && !_prevKeys.IsKeyDown(key);
    public bool IsDown(Buttons button) => _pad.IsConnected && _pad.IsButtonDown(button);
    public bool WasPressed(Buttons button) => _pad.IsConnected && _pad.IsButtonDown(button) && !_prevPad.IsButtonDown(button);

    public bool MenuUp => WasPressed(Keys.Up) || WasPressed(Keys.W) || WasPressed(Buttons.DPadUp) || WasPressed(Buttons.LeftThumbstickUp);
    public bool MenuDown => WasPressed(Keys.Down) || WasPressed(Keys.S) || WasPressed(Buttons.DPadDown) || WasPressed(Buttons.LeftThumbstickDown);
    public bool MenuLeft => WasPressed(Keys.Left) || WasPressed(Keys.A) || WasPressed(Buttons.DPadLeft) || WasPressed(Buttons.LeftThumbstickLeft);
    public bool MenuRight => WasPressed(Keys.Right) || WasPressed(Keys.D) || WasPressed(Buttons.DPadRight) || WasPressed(Buttons.LeftThumbstickRight);
    public bool MenuSelect => WasPressed(Keys.Enter) || WasPressed(Keys.Space) || WasPressed(Buttons.A) || WasPressed(Buttons.Start);
    public bool MenuBack => WasPressed(Keys.Escape) || WasPressed(Buttons.B) || WasPressed(Buttons.Back);
    public bool Pause => WasPressed(Keys.Escape) || WasPressed(Keys.P) || WasPressed(Buttons.Start);
}
