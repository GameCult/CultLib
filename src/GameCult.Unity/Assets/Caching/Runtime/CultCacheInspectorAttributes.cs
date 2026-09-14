using System;

namespace GameCult.Unity.Caching
{
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

    // Lower values appear first; members without it keep their key order.
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

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultInspectorAssetPathAttribute : Attribute
    {
        public CultInspectorAssetPathAttribute(Type assetType = null)
        {
            AssetType = assetType;
        }

        public Type AssetType { get; }
    }

    // Marks an editor class implementing ICultInspectorDrawer as the drawer for every member of MemberType,
    // or of any closed form when MemberType is an open generic definition. It wins over the built-in drawers.
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class CultInspectorDrawerAttribute : Attribute
    {
        public CultInspectorDrawerAttribute(Type memberType)
        {
            MemberType = memberType ?? throw new ArgumentNullException(nameof(memberType));
        }

        public Type MemberType { get; }
    }
}
