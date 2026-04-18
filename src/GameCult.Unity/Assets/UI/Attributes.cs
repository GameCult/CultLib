using System;

namespace GameCult.Unity.UI
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    public class InspectableAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public abstract class PreferredInspectorAttribute : InspectableAttribute { }
    
    public class InspectableReadOnlyLabelAttribute : PreferredInspectorAttribute { }

    public class InspectableRangedFloatAttribute : PreferredInspectorAttribute
    {
        public readonly float Min, Max;

        public InspectableRangedFloatAttribute(float min, float max)
        {
            Min = min;
            Max = max;
        }
    }

    public class InspectableRangedIntAttribute : PreferredInspectorAttribute
    {
        public readonly int Min, Max;

        public InspectableRangedIntAttribute(int min, int max)
        {
            Min = min;
            Max = max;
        }
    }

    public class InspectableIncrementIntAttribute : PreferredInspectorAttribute
    {
        public readonly int Min, Max;

        public InspectableIncrementIntAttribute(int min, int max)
        {
            Min = min;
            Max = max;
        }
    }
}