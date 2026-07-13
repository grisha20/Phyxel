using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Phyxel.Input;

public readonly record struct RawInputSnapshot(
    Point MousePosition,
    int WheelDelta,
    bool LeftDown,
    bool RightDown,
    bool LeftPressed,
    bool LeftReleased,
    bool RightPressed,
    bool RightReleased,
    bool ShiftDown,
    bool EscapePressed,
    bool SavePressed,
    bool LoadPressed,
    float DeltaSeconds);
