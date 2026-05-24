using System;

namespace GameCult.Unity.Caching
{
    /// <summary>
    /// Overrides the label shown for a CultCache document member in Unity editor tooling.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Class, Inherited = true)]
    public sealed class CultInspectorLabelAttribute : Attribute
    {
        public CultInspectorLabelAttribute(string label)
        {
            Label = label ?? string.Empty;
        }

        public string Label { get; }
    }

    /// <summary>
    /// Hides a CultCache document member from Unity editor tooling.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultInspectorHiddenAttribute : Attribute
    {
    }

    /// <summary>
    /// Shows a CultCache document member without allowing editor mutation.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultInspectorReadOnlyAttribute : Attribute
    {
    }

    /// <summary>
    /// Controls member ordering in Unity editor tooling. Lower values appear first.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultInspectorOrderAttribute : Attribute
    {
        public CultInspectorOrderAttribute(int order)
        {
            Order = order;
        }

        public int Order { get; }
    }

    /// <summary>
    /// Draws a string member as a multi-line text area.
    /// </summary>
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

    /// <summary>
    /// Draws numeric members with a slider.
    /// </summary>
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

    /// <summary>
    /// Draws a string member as a Unity asset path picker.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultInspectorAssetPathAttribute : Attribute
    {
        public CultInspectorAssetPathAttribute(Type assetType = null)
        {
            AssetType = assetType;
        }

        public Type AssetType { get; }
    }
}
