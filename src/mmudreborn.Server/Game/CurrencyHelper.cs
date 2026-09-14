namespace mmudreborn.Game;

public static class CurrencyHelper
{
    public const int CopperPerSilver = 10;
    public const int CopperPerGold = 100;
    public const int CopperPerPlatinum = 10000;
    public const int CopperPerRunic = 1000000;

    public static long ToCopper(long runic, long platinum, long gold, long silver, long copper)
    {
        return (runic * CopperPerRunic)
             + (platinum * CopperPerPlatinum)
             + (gold * CopperPerGold)
             + (silver * CopperPerSilver)
             + copper;
    }

    public static long ToCopper(Game.Player player)
    {
        return ToCopper(player.Runic, player.Platinum, player.Gold, player.Silver, player.Copper);
    }

    public static void SetFromCopper(Game.Player player, long totalCopper)
    {
        long remaining = Math.Max(0, totalCopper);

        player.Runic = (int)(remaining / CopperPerRunic);
        remaining %= CopperPerRunic;

        player.Platinum = (int)(remaining / CopperPerPlatinum);
        remaining %= CopperPerPlatinum;

        player.Gold = (int)(remaining / CopperPerGold);
        remaining %= CopperPerGold;

        player.Silver = (int)(remaining / CopperPerSilver);
        remaining %= CopperPerSilver;

        player.Copper = (int)remaining;
    }

    public static void Normalize(Game.Player player)
    {
        SetFromCopper(player, ToCopper(player));
    }
}