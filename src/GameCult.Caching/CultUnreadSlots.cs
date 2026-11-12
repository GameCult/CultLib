namespace GameCult.Caching
{
    /// <summary>
    /// What a store load found in a record's payload past the slots this build declares, at any depth: the first type read with extra
    /// slots and how many slots it carried against how many it declares, and how many objects in the record did. It holds no slot
    /// value, so it can be named in a refusal. A record carrying it is read-only (<see cref="CultCache.IsWritable"/>) unless every
    /// mark is explained.
    /// </summary>
    public sealed class CultUnreadSlots
    {
        internal CultUnreadSlots(string typeName, int slotCount, int declaredCount, int occurrences, bool allAtDepthZero, bool explained)
        {
            TypeName = typeName;
            SlotCount = slotCount;
            DeclaredCount = declaredCount;
            Occurrences = occurrences;
            AllAtDepthZero = allAtDepthZero;
            Explained = explained;
        }

        /// <summary>The first type read with extra slots.</summary>
        public string TypeName { get; }

        /// <summary>The slots that object carried.</summary>
        public int SlotCount { get; }

        /// <summary>The slots this build declares for that type: the array its own formatter writes for one instance.</summary>
        public int DeclaredCount { get; }

        /// <summary>The objects in the record that carried extra slots.</summary>
        public int Occurrences { get; }

        /// <summary>
        /// True only when every mark is at depth 0 and the schema comparison under the id the record was stored under reported every
        /// slot the mark carries past the reader (<see cref="SlotCount"/> is at most the highest reported slot plus one): the loss a
        /// compatible-id declaration consents to (cultlib-gaps:ruling:slot-mark-consent). Any other mark leaves the record read-only
        /// whatever is declared.
        /// </summary>
        public bool Explained { get; }

        // Every mark the decode noted sat in the record's own object, the half of "explained" the payload decides.
        internal bool AllAtDepthZero { get; }

        internal CultUnreadSlots Explaining(bool explained) =>
            new(TypeName, SlotCount, DeclaredCount, Occurrences, AllAtDepthZero, explained);
    }

    // A payload decoded by a store kind, with the extra slots the decode noted. A store kind that does not note them reports none.
    internal readonly struct CultDecoded(object document, CultUnreadSlots? unreadSlots)
    {
        public object Document { get; } = document;
        public CultUnreadSlots? UnreadSlots { get; } = unreadSlots;
    }
}
