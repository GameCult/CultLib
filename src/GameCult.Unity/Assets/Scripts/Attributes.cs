using System;

[AttributeUsage(AttributeTargets.All, Inherited = false)]
public class InspectableAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Field)]
public abstract class PreferredInspectorAttribute : InspectableAttribute { }

public class InspectableEnumValuesAttribute : PreferredInspectorAttribute
{
    public Type EnumType;

    public InspectableEnumValuesAttribute(Type enumType)
    {
        EnumType = enumType;
    }
}

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