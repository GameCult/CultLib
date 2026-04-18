/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. */

using System;
using System.Globalization;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;

namespace GameCult.Unity.UI.Components
{
    public class Label : LayoutComponent, IFieldHandler, ITextElement
    {
        [SerializeField] protected TextMeshProUGUI? label;
        
        public int Priority => 100;

        public TextMeshProUGUI? Text => label;
        
        public bool CanHandle(Type type, PreferredInspectorAttribute? attr) =>
            attr is InspectableReadOnlyLabelAttribute;

        public void ConfigureForMember(IUIContext context, object target, MemberInfo member, DisplayOptions? displayOptions = null)
        {
            Type memberType;
            Func<object> getValue;
            switch (member)
            {
                case FieldInfo field:
                    memberType = field.FieldType;
                    getValue = () => field.GetValue(target);
                    break;
                case PropertyInfo property:
                    memberType = property.PropertyType;
                    getValue = () => property.GetValue(target);
                    break;
                default:
                    return;
            }

            string FormattedRead()
            {
                var val = getValue();
                if (val == null) return "Null";
                return FormatValue(val, memberType);
            }

            Configure(context, FormattedRead, displayOptions);
        }
        
        private string FormatValue(object val, Type type)
        {
            if (type == typeof(float))
            {
                return ((float)val).ToString("F3", CultureInfo.InvariantCulture);
            }

            if (type == typeof(double))
            {
                return ((double)val).ToString("F3", CultureInfo.InvariantCulture);
            }

            if (type == typeof(decimal))
            {
                return ((decimal)val).ToString("F3", CultureInfo.InvariantCulture);
            }

            return val.ToString();
        }

        public Label Configure(IUIContext context, Func<string> read, DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            if (label is null) return this;
            label.text = read();

            context.Refresh += () => label.text = read();
            
            this.ApplyLayoutOptions(displayOptions);
            this.ApplyTextOptions(displayOptions);
            
            return this;
        }

        public Label Configure(IUIContext context, string labelText, DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            if (label is null) return this;
            label.text = labelText;
            
            this.ApplyLayoutOptions(displayOptions);
            this.ApplyTextOptions(displayOptions);

            return this;
        }
    }

    public static class LabelFieldGeneratorExtension
    {
        public static void AddLabelInspector(this IUIContext context,
            string label,
            Func<string> read) =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context,
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<Label>("Value")?.Configure(context, read));
        
        public static void AddLabel(this IUIContext context, Func<string> read, string? prefabName = null) =>
            context.Resolver.Resolve<Label>(prefabName)?.Configure(context, read);
        
        public static void AddLabel(this IUIContext context, string label, string? prefabName = null) =>
            context.Resolver.Resolve<Label>(prefabName)?.Configure(context, label);
    }
}