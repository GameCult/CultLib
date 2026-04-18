/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. */

using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GameCult.Unity.UI.Components
{
    public class SliderField : LayoutComponent, IFieldHandler
    {
        [SerializeField] private Slider? slider;

        public int Priority => 100;

        public bool CanHandle(Type type, PreferredInspectorAttribute? attr) =>
            (type == typeof(float) && attr is InspectableRangedFloatAttribute { ReadOnly: false }) ||
            (type == typeof(int) && attr is InspectableRangedIntAttribute { ReadOnly: false });

        public void ConfigureForMember(IUIContext context, object target, MemberInfo member, DisplayOptions? displayOptions = null)
        {
            var type = member switch
            {
                FieldInfo field => field.FieldType,
                PropertyInfo property => property.PropertyType,
                _ => null
            };

            if (type == null) return;
            
            if (type == typeof(float))
            {
                Func<float> read;
                Action<float> write;
                switch (member)
                {
                    case FieldInfo field:
                        read = () => (float)field.GetValue(target);
                        write = f => field.SetValue(target, f);
                        break;
                    case PropertyInfo property:
                        read = () => (float)property.GetValue(target);
                        write = f => property.SetValue(target, f);
                        break;
                    default:
                        return;
                }

                var range = member.GetCustomAttribute<InspectableRangedFloatAttribute>();
                Configure(context, read, write, range.Min, range.Max, displayOptions);
            }
            else if (type == typeof(int))
            {
                Func<int> read;
                Action<int> write;
                switch (member)
                {
                    case FieldInfo field:
                        read = () => (int)field.GetValue(target);
                        write = i => field.SetValue(target, i);
                        break;
                    case PropertyInfo property:
                        read = () => (int)property.GetValue(target);
                        write = i => property.SetValue(target, i);
                        break;
                    default:
                        return;
                }

                var range = member.GetCustomAttribute<InspectableRangedIntAttribute>();
                Configure(context, read, write, range.Min, range.Max, displayOptions);
            }
        }

        public SliderField Configure(IUIContext context, Func<float> read, Action<float> write, float min, float max, DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            if (slider is null) return this;
            slider.wholeNumbers = false;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = read();
            slider.onValueChanged.AddListener(f => write(f));

            context.Refresh += () => slider.value = read();
            
            this.ApplyLayoutOptions(displayOptions);
            
            return this;
        }
        public SliderField Configure(IUIContext context, Func<int> read, Action<int> write, int min, int max, DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            if (slider is null) return this;
            slider.wholeNumbers = true;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = read();
            slider.onValueChanged.AddListener(f => write(Mathf.RoundToInt(f)));

            context.Refresh += () => slider.value = read();
            
            this.ApplyLayoutOptions(displayOptions);
            
            return this;
        }
    }

    public static class RangedFloatFieldGeneratorExtension
    {
        public static void AddRangedFloatInspector(this IUIContext context,
            string label,
            Func<float> read,
            Action<float> write,
            float min,
            float max,
            string? prefabName = null) =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context,
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<SliderField>(prefabName)?.Configure(context, read, write, min, max));

        public static void AddRangedIntInspector(this IUIContext context,
            string label,
            Func<int> read,
            Action<int> write,
            int min,
            int max,
            string? prefabName = null) =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context,
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<SliderField>(prefabName)?.Configure(context, read, write, min, max));
        
        public static void AddRangedFloatField(this IUIContext context,
            Func<float> read,
            Action<float> write,
            float min,
            float max,
            DisplayOptions? displayOptions = null,
            string? prefabName = null) =>
            context.Resolver.Resolve<SliderField>(prefabName)?.Configure(context, read, write, min, max, displayOptions);
        
        public static void AddRangedIntField(this IUIContext context,
            Func<int> read,
            Action<int> write,
            int min,
            int max,
            DisplayOptions? displayOptions = null,
            string? prefabName = null) =>
            context.Resolver.Resolve<SliderField>(prefabName)?.Configure(context, read, write, min, max, displayOptions);

    }
}
