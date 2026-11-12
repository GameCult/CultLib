using System;
using GameCult.Caching;

namespace GameCult.Caching.MessagePack;

/// <summary>
/// The marks one payload decode noted: the guard's slot formatter reports every object whose array held more slots than its type
/// declares, with the nesting depth it sat at, and DeserializeMarked reads the finding out. Thread-bound and open only around the one
/// decode, so a decode outside it (a CultMesh read, a codec) notes nothing.
/// </summary>
internal sealed class CultSlotScope : IDisposable
{
    [ThreadStatic] private static CultSlotScope? current;

    private readonly CultSlotScope? previous;
    private string? typeName;
    private int slotCount;
    private int declaredCount;
    private int occurrences;
    private bool allAtDepthZero = true;

    private CultSlotScope() => previous = current;

    internal static CultSlotScope? Current => current;

    internal static CultSlotScope Open() => current = new CultSlotScope();

    // 0 for the document's own object; each object nested inside it adds one (a union adds none, its arm is the object).
    internal int Depth { get; set; }

    internal void Note(string name, int slots, int declared)
    {
        if (occurrences++ == 0)
        {
            typeName = name;
            slotCount = slots;
            declaredCount = declared;
        }

        if (Depth > 0) allAtDepthZero = false;
    }

    internal CultUnreadSlots? Finding() =>
        occurrences == 0 ? null : new CultUnreadSlots(typeName!, slotCount, declaredCount, occurrences, allAtDepthZero, explained: false);

    public void Dispose() => current = previous;
}
