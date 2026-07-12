using System;

namespace CultMath;

public record struct bool2(bool x, bool y)
{
    public static readonly bool2 @false = new(false, false);
    public static readonly bool2 @true = new(true, true);

    public bool this[int index] => index switch
    {
        0 => x,
        1 => y,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static implicit operator bool2(bool value) => new(value, value);
}
