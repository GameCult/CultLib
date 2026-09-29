using System;
using System.Collections.Generic;

namespace GameCult.Caching
{
    // Inspection metadata for document members. Engine-free: headless models carry it, and every inspector lowering
    // (the Unity editor Studio, a runtime CultUI panel) reads it through CultInspectorModel.

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class, Inherited = true)]
    public sealed class CultInspectorLabelAttribute : Attribute
    {
        public CultInspectorLabelAttribute(string label)
        {
            Label = label ?? string.Empty;
        }

        public string Label { get; }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultInspectorHiddenAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultInspectorReadOnlyAttribute : Attribute
    {
    }

    // Lower values appear first; members without it keep their slot order.
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultInspectorOrderAttribute : Attribute
    {
        public CultInspectorOrderAttribute(int order)
        {
            Order = order;
        }

        public int Order { get; }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultInspectorTextAreaAttribute : Attribute
    {
        public CultInspectorTextAreaAttribute(int minLines = 3, int maxLines = 12)
        {
            MinLines = minLines;
            MaxLines = maxLines;
        }

        public int MinLines { get; }
        public int MaxLines { get; }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultInspectorRangeAttribute : Attribute
    {
        public CultInspectorRangeAttribute(float min, float max)
        {
            Min = min;
            Max = max;
        }

        public float Min { get; }
        public float Max { get; }
    }

    // A string member holding an engine asset's stable identity; in Unity, the `.meta` GUID. AssetType is the
    // engine's asset type, named by the consumer (typeof(UnityEngine.Texture2D) in a Unity assembly); a lowering
    // that does not know the type ignores it.
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultInspectorAssetGuidAttribute : Attribute
    {
        public CultInspectorAssetGuidAttribute(Type? assetType = null)
        {
            AssetType = assetType;
        }

        public Type? AssetType { get; }
    }

    // Groups a document type's record list by the named members, outermost first: the argument order is the nesting order.
    // The nearest declaration up the inheritance chain wins, so a derived type may re-declare, and an empty declaration
    // opts out of a base's. A member must be a visible, editable string, integer, bool, non-[Flags] enum or CultRecordRef;
    // anything else gives a flat list and a notice. Presentation only: no schema, index or stored byte changes.
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public sealed class CultInspectorGroupByAttribute : Attribute
    {
        public CultInspectorGroupByAttribute(params string[] members)
        {
            Members = members ?? throw new ArgumentNullException(nameof(members));
        }

        public IReadOnlyList<string> Members { get; }
    }

    // Marks a lowering's drawer class as the drawer for a claimed type. Claimed is either an Attribute subclass (the
    // drawer draws every member carrying that attribute) or a value type (every value of that type, or of any closed
    // form when Claimed is an open generic definition). Attribute claims win over type claims, which win over the
    // built-in kinds. A claim two drawers make is used by neither.
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class CultInspectorDrawerAttribute : Attribute
    {
        public CultInspectorDrawerAttribute(Type claimed)
        {
            Claimed = claimed ?? throw new ArgumentNullException(nameof(claimed));
        }

        public Type Claimed { get; }

        public bool ClaimsAttribute => typeof(Attribute).IsAssignableFrom(Claimed);
    }
}
