using System;
using UnityEngine;
using UnityEngine.UI;

namespace GameCult.Unity.UI.Components
{
    public class ProgressField : LayoutComponent, IFieldHandler
    {
        [SerializeField] private Slider? slider;

        public int Priority => 100;

        public bool CanHandle(Type type, PreferredInspectorAttribute? attr) =>
            (type == typeof(float) && attr is InspectableRangedFloatAttribute { ReadOnly: true }) ||
            (type == typeof(int) && attr is InspectableRangedIntAttribute { ReadOnly: true });

        public void ConfigureForMember(IUIContext context, object target, System.Reflection.MemberInfo member, DisplayOptions? displayOptions = null)
        {
            if (member is System.Reflection.FieldInfo field)
            {
                if (field.FieldType == typeof(float))
                {
                    var range = field.GetCustomAttributes(typeof(InspectableRangedFloatAttribute), false);
                    if (range.Length > 0)
                    {
                        var attr = (InspectableRangedFloatAttribute)range[0];
                        Configure(context, () => (float)field.GetValue(target), attr.Min, attr.Max, displayOptions);
                    }
                }
                else if (field.FieldType == typeof(int))
                {
                    var range = field.GetCustomAttributes(typeof(InspectableRangedIntAttribute), false);
                    if (range.Length > 0)
                    {
                        var attr = (InspectableRangedIntAttribute)range[0];
                        Configure(context, () => (int)field.GetValue(target), attr.Min, attr.Max, displayOptions);
                    }
                }
            }
            else if (member is System.Reflection.PropertyInfo property)
            {
                if (property.PropertyType == typeof(float))
                {
                    var range = property.GetCustomAttributes(typeof(InspectableRangedFloatAttribute), false);
                    if (range.Length > 0)
                    {
                        var attr = (InspectableRangedFloatAttribute)range[0];
                        Configure(context, () => (float)property.GetValue(target), attr.Min, attr.Max, displayOptions);
                    }
                }
                else if (property.PropertyType == typeof(int))
                {
                    var range = property.GetCustomAttributes(typeof(InspectableRangedIntAttribute), false);
                    if (range.Length > 0)
                    {
                        var attr = (InspectableRangedIntAttribute)range[0];
                        Configure(context, () => (int)property.GetValue(target), attr.Min, attr.Max, displayOptions);
                    }
                }
            }
        }

        public ProgressField Configure(IUIContext context,
            Func<float> read,
            float min = 0,
            float max = 1,
            DisplayOptions? displayOptions = null)
        {
            if (displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);

            if (slider is null) return this;

            slider.wholeNumbers = false;
            slider.minValue = min;
            slider.maxValue = max;
            slider.interactable = false;
            slider.value = read();

            context.Refresh += () => slider.value = read();
            this.ApplyLayoutOptions(displayOptions);
            return this;
        }

        public ProgressField Configure(IUIContext context,
            Func<int> read,
            int min = 0,
            int max = 1,
            DisplayOptions? displayOptions = null)
        {
            if (displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);

            if (slider is null) return this;

            slider.wholeNumbers = true;
            slider.minValue = min;
            slider.maxValue = max;
            slider.interactable = false;
            slider.value = read();

            context.Refresh += () => slider.value = read();
            this.ApplyLayoutOptions(displayOptions);
            return this;
        }
    }

    public static class ProgressFieldGeneratorExtensions
    {
        public static void AddProgressInspector(this IUIContext context,
            string label,
            Func<float> read,
            float min = 0,
            float max = 1,
            DisplayOptions? displayOptions = null,
            string? prefabName = "Progress") =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context,
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<ProgressField>(prefabName)?.Configure(context, read, min, max, displayOptions));

        public static void AddProgressInspector(this IUIContext context,
            string label,
            Func<int> read,
            int min = 0,
            int max = 1,
            DisplayOptions? displayOptions = null,
            string? prefabName = "Progress") =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context,
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<ProgressField>(prefabName)?.Configure(context, read, min, max, displayOptions));

        public static void AddProgressField(this IUIContext context,
            Func<float> read,
            float min = 0,
            float max = 1,
            DisplayOptions? displayOptions = null,
            string? prefabName = "Progress") =>
            context.Resolver.Resolve<ProgressField>(prefabName)?.Configure(context, read, min, max, displayOptions);

        public static void AddProgressField(this IUIContext context,
            Func<int> read,
            int min = 0,
            int max = 1,
            DisplayOptions? displayOptions = null,
            string? prefabName = "Progress") =>
            context.Resolver.Resolve<ProgressField>(prefabName)?.Configure(context, read, min, max, displayOptions);
    }
}
