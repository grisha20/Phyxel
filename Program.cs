using System;

namespace Phyxel;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        using PhyxelGame game = new();
        game.Run();
    }
}
