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
    public class IncrementField : LayoutComponent, IFieldHandler, ITextElement
    {
        [SerializeField] private TextMeshProUGUI? value;
        [SerializeField] private Button? increment;
        [SerializeField] private Button? decrement;

        public int Priority => 100;

        public TextMeshProUGUI? Text => value;

        public bool CanHandle(Type type, PreferredInspectorAttribute? attr) =>
            type == typeof(int) && attr is InspectableIncrementIntAttribute;

        public void ConfigureForMember(IUIContext context, object target, MemberInfo member, DisplayOptions? displayOptions = null)
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
            var range = member.GetCustomAttribute<InspectableIncrementIntAttribute>();
            Configure(context, read, write, range.Min, range.Max, displayOptions);
        }

        public IncrementField Configure(IUIContext context,
            Func<int> read,
            Action<int> write,
            int min,
            int max,
            DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            
            if (value is null || increment is null || decrement is null) return this;
            
            value.text = read().ToString();
            increment.onClick.AddListener(() => write(Mathf.Min(read() + 1, max)));
            decrement.onClick.AddListener(() => write(Mathf.Max(read() - 1, min)));

            context.Refresh += () =>
            {
                var val = read();
                value.text = val.ToString();
                increment.interactable = val < max;
                decrement.interactable = val > min;
            };
            
            this.ApplyLayoutOptions(displayOptions);
            this.ApplyTextOptions(displayOptions);
            
            return this;
        }
    }

    public static class IncrementFieldGeneratorExtension
    {
        public static void AddIncrementIntInspector(this IUIContext context,
            string label,
            Func<int> read,
            Action<int> write,
            int min,
            int max,
            string? prefabName = null) =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context,
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<IncrementField>(prefabName)?.Configure(context, read, write, min, max));
        
        public static void AddIncrementIntField(this IUIContext context,
            string label,
            Func<int> read,
            Action<int> write,
            int min,
            int max,
            string? prefabName = null) =>
            context.Resolver.Resolve<IncrementField>(prefabName)?.Configure(context, read, write, min, max);
    }
}