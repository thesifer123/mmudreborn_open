using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class PlayerAlignmentTests
{
    // Stock treats the single alignment slot
    // as the single alignment slot — positive = evil, negative = good. C# previously split this
    // across Player.Alignment (inverted, positive = good) and Player.EvilPoints (stock scale,
    // positive = evil). After unification, Player.Alignment is a thin alias for EvilPoints so
    // every consumer reads the same canonical value.
    [Fact]
    public void Alignment_alias_rounds_EvilPoints_to_int()
    {
        var p = new Player { EvilPoints = 250.4f };
        Assert.Equal(250, p.Alignment);

        p.EvilPoints = -49.5f;
        Assert.Equal(-50, p.Alignment); // banker's rounding: .5 → nearest even (50 → 50; -49.5 → -50)
    }

    [Fact]
    public void Alignment_setter_writes_through_to_EvilPoints()
    {
        var p = new Player();
        p.Alignment = -200;
        Assert.Equal(-200f, p.EvilPoints);
        Assert.Equal(-200, p.Alignment);
    }

    // The lawful gate: when the player carries the lawful
    // flag, any positive delta is refused with "To do this action, you
    // must turn unlawful first!". TryAddEvilPoints mirrors that gate: positive delta + IsLawful
    // = no change.
    [Fact]
    public void TryAddEvilPoints_refuses_positive_delta_when_lawful()
    {
        var lawful = new Player { IsLawful = true, EvilPoints = -50f };

        Assert.False(lawful.TryAddEvilPoints(10f));
        Assert.Equal(-50f, lawful.EvilPoints);
    }

    [Fact]
    public void TryAddEvilPoints_allows_negative_delta_when_lawful()
    {
        // A lawful character can still BECOME more good — e.g. quest reward with addevil −20.
        // Only positive deltas are gated by the "must turn unlawful" check.
        var lawful = new Player { IsLawful = true, EvilPoints = -100f };

        Assert.True(lawful.TryAddEvilPoints(-25f));
        Assert.Equal(-125f, lawful.EvilPoints);
    }

    [Fact]
    public void TryAddEvilPoints_applies_positive_delta_when_not_lawful()
    {
        var rogue = new Player { IsLawful = false, EvilPoints = 0f };

        Assert.True(rogue.TryAddEvilPoints(40f));
        Assert.Equal(40f, rogue.EvilPoints);
    }

    [Fact]
    public void TryAddEvilPoints_zero_delta_is_noop()
    {
        var p = new Player { EvilPoints = 12.5f };

        Assert.False(p.TryAddEvilPoints(0f));
        Assert.Equal(12.5f, p.EvilPoints);
    }

    // EvilPoints can go negative — the canonical alignment supports the full -32000..30000 range
    // from the stock slot. This is what makes a Saint reachable through good-action awards
    // (addevil −N) or sysop /good /saint shortcuts; the previous Math.Max(0, ...) clamp lived in
    // WorldTick.cs forgiveness only and never touched the canonical setter.
    [Fact]
    public void EvilPoints_supports_negative_values_directly()
    {
        var p = new Player { EvilPoints = -300f };
        Assert.Equal(-300f, p.EvilPoints);
        Assert.Equal(-300, p.Alignment);
    }
}
