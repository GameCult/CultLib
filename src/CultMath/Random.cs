namespace CultMath;

public struct Random
{
    private const uint DefaultSeed = 0x6E624EB7u;

    public uint state;

    public Random(uint seed)
    {
        state = seed == 0 ? DefaultSeed : seed;
    }

    public static Random CreateFromIndex(uint index) => new(index + DefaultSeed);

    public void InitState(uint seed)
    {
        state = seed == 0 ? DefaultSeed : seed;
    }

    public uint NextUInt()
    {
        var x = state == 0 ? DefaultSeed : state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        state = x == 0 ? DefaultSeed : x;
        return state;
    }

    public int NextInt(int max) => NextInt(0, max);

    public int NextInt(int min, int max)
    {
        if (max <= min)
        {
            return min;
        }

        return min + (int)(NextUInt() % (uint)(max - min));
    }

    public float NextFloat() => (NextUInt() >> 8) * (1.0f / 16777216.0f);

    public float NextFloat(float max) => NextFloat() * max;

    public float NextFloat(float min, float max) => min + NextFloat() * (max - min);

    public float2 NextFloat2() => new(NextFloat(), NextFloat());

    public float2 NextFloat2(float min, float max) => new(NextFloat(min, max), NextFloat(min, max));

    public float2 NextFloat2(float2 min, float2 max) => min + NextFloat2() * (max - min);

    public float2 NextFloat2Direction()
    {
        var angle = NextFloat(0.0f, math.TAU);
        return new float2(math.cos(angle), math.sin(angle));
    }

    public float3 NextFloat3(float3 min, float3 max) => min + new float3(NextFloat(), NextFloat(), NextFloat()) * (max - min);
}
