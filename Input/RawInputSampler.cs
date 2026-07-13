using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Phyxel.Input;

public sealed class RawInputSampler
{
    private MouseState previousMouse;
    private KeyboardState previousKeyboard;

    public RawInputSnapshot Sample(GameTime gameTime)
    {
        MouseState mouse = Mouse.GetState();
        KeyboardState keyboard = Keyboard.GetState();
        bool leftDown = mouse.LeftButton == ButtonState.Pressed;
        bool rightDown = mouse.RightButton == ButtonState.Pressed;
        bool controlDown = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
        RawInputSnapshot snapshot = new(
            new Point(mouse.X, mouse.Y),
            mouse.ScrollWheelValue - previousMouse.ScrollWheelValue,
            leftDown,
            rightDown,
            leftDown && previousMouse.LeftButton == ButtonState.Released,
            !leftDown && previousMouse.LeftButton == ButtonState.Pressed,
            rightDown && previousMouse.RightButton == ButtonState.Released,
            !rightDown && previousMouse.RightButton == ButtonState.Pressed,
            keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift),
            keyboard.IsKeyDown(Keys.Escape) && previousKeyboard.IsKeyUp(Keys.Escape),
            controlDown && keyboard.IsKeyDown(Keys.S) && previousKeyboard.IsKeyUp(Keys.S),
            controlDown && keyboard.IsKeyDown(Keys.L) && previousKeyboard.IsKeyUp(Keys.L),
            Math.Clamp((float)gameTime.ElapsedGameTime.TotalSeconds, 0f, 1f / 20f));
        previousMouse = mouse;
        previousKeyboard = keyboard;
        return snapshot;
    }
}
