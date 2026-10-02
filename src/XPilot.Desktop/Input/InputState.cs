using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace XPilot.Desktop.Input;

/// <summary>Keyboard and gamepad state for this frame and the previous one.</summary>
public sealed class InputState
{
    private KeyboardState _keys, _prevKeys;
    private GamePadState _pad, _prevPad;
    private readonly StringBuilder _typing = new();

    public GamePadState Pad => _pad;
    /// <summary>Characters typed since the last frame, from the window's text input (so layouts and shift work).</summary>
    public string Typed { get; private set; } = "";

    public void OnTextInput(char c) => _typing.Append(c);

    public void Update(bool windowActive)
    {
        Typed = _typing.ToString();
        _typing.Clear();
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

    /// <summary>Applies this frame's typing (printable characters and backspace) to a text field.</summary>
    public string EditText(string text, int maxLength)
    {
        bool backspaced = false;
        foreach (char c in Typed)
        {
            if (c == '\b')
            {
                backspaced = true;
                if (text.Length > 0) text = text[..^1];
            }
            else if (c >= ' ' && c < 127 && text.Length < maxLength)
            {
                text += c;
            }
        }
        if (!backspaced && WasPressed(Keys.Back) && text.Length > 0) text = text[..^1];
        return text;
    }
}
