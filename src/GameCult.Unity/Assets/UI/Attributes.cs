using System;

namespace GameCult.Unity.UI
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    public class InspectableAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public abstract class PreferredInspectorAttribute : InspectableAttribute
    {
        public bool ReadOnly { get; }

        protected PreferredInspectorAttribute(bool readOnly = false)
        {
            ReadOnly = readOnly;
        }

        public abstract PreferredInspectorAttribute AsReadOnly();
    }
    
    public class InspectableReadOnlyLabelAttribute : PreferredInspectorAttribute
    {
        public InspectableReadOnlyLabelAttribute() : base(true) { }

        public override PreferredInspectorAttribute AsReadOnly() => this;
    }

    public class InspectableRangedFloatAttribute : PreferredInspectorAttribute
    {
        public readonly float Min, Max;

        public InspectableRangedFloatAttribute(float min, float max, bool readOnly = false) : base(readOnly)
        {
            Min = min;
            Max = max;
        }

        public override PreferredInspectorAttribute AsReadOnly() => new InspectableRangedFloatAttribute(Min, Max, true);
    }

    public class InspectableRangedIntAttribute : PreferredInspectorAttribute
    {
        public readonly int Min, Max;

        public InspectableRangedIntAttribute(int min, int max, bool readOnly = false) : base(readOnly)
        {
            Min = min;
            Max = max;
        }

        public override PreferredInspectorAttribute AsReadOnly() => new InspectableRangedIntAttribute(Min, Max, true);
    }

    public class InspectableIncrementIntAttribute : PreferredInspectorAttribute
    {
        public readonly int Min, Max;

        public InspectableIncrementIntAttribute(int min, int max, bool readOnly = false) : base(readOnly)
        {
            Min = min;
            Max = max;
        }

        public override PreferredInspectorAttribute AsReadOnly() => new InspectableIncrementIntAttribute(Min, Max, true);
    }
}
