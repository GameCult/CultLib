/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace GameCult.Unity.UI.Components
{
    public class InputField : LayoutComponent, IFieldHandler, IMultipleTextElement
    {
        [SerializeField] private TMP_InputField? inputField;
        [SerializeField] private TextMeshProUGUI[]? textMeshes;

        public int Priority => 10;

        public IEnumerable<TextMeshProUGUI> Text => textMeshes ?? Enumerable.Empty<TextMeshProUGUI>();

        public bool CanHandle(Type type, PreferredInspectorAttribute? attr) =>
            (type == typeof(string) || type == typeof(int) || type == typeof(float)) && attr == null;

        public void ConfigureForMember(IUIContext context, object target, MemberInfo member, DisplayOptions? displayOptions = null)
        {
            var type = member switch
            {
                FieldInfo field => field.FieldType,
                PropertyInfo property => property.PropertyType,
                _ => null
            };

            if (type == null) return;

            if (type == typeof(string))
            {
                Func<string> read;
                Action<string> write;
                switch (member)
                {
                    case FieldInfo field:
                        read = () => (string)field.GetValue(target);
                        write = s => field.SetValue(target, s);
                        break;
                    case PropertyInfo property:
                        read = () => (string)property.GetValue(target);
                        write = s => property.SetValue(target, s);
                        break;
                    default:
                        return;
                }
                Configure(context, read, write);
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
                Configure(context, read, write);
            }
            else if (type == typeof(float))
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
                Configure(context, read, write);
            }
        }

        public InputField Configure(IUIContext context, Func<string> read, Action<string> write, DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            if (inputField is null) return this;
            inputField.contentType = TMP_InputField.ContentType.Standard;
            inputField.text = read() ?? string.Empty;
            inputField.onValueChanged.AddListener(v => write(v));

            context.Refresh += () =>
            {
                var val = read() ?? string.Empty;
                if (inputField.text != val) inputField.text = val;
            };
            
            this.ApplyLayoutOptions(displayOptions);
            this.ApplyTextOptions(displayOptions);
            
            return this;
        }

        public InputField Configure(IUIContext context, Func<int> read, Action<int> write, DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            if (inputField is null) return this;
            inputField.contentType = TMP_InputField.ContentType.IntegerNumber;
            inputField.text = read().ToString();
            inputField.onValueChanged.AddListener(v =>
            {
                if (int.TryParse(v, out var i)) write(i);
            });

            context.Refresh += () =>
            {
                var val = read();
                if (int.TryParse(inputField.text, out var existing) && existing != val)
                    inputField.text = val.ToString();
            };
            
            this.ApplyLayoutOptions(displayOptions);
            this.ApplyTextOptions(displayOptions);

            return this;
        }

        public InputField Configure(IUIContext context, Func<float> read, Action<float> write, DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            if (inputField is null) return this;
            inputField.contentType = TMP_InputField.ContentType.DecimalNumber;
            inputField.text = read().ToString("0.###");
            inputField.onValueChanged.AddListener(v => write(float.Parse(v)));

            context.Refresh += () =>
            {
                var val = read();
                if (float.TryParse(inputField.text, out var existing) && Mathf.Abs(existing-val) < .001)
                    inputField.text = val.ToString("0.###");
            };
            
            this.ApplyLayoutOptions(displayOptions);
            this.ApplyTextOptions(displayOptions);

            return this;
        }
    }

    public static class InputFieldGeneratorExtension
    {
        public static void AddStringInspector(this IUIContext context,
            string label,
            Func<string> read,
            Action<string> write,
            string? prefabName = null) =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context,
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<InputField>(prefabName)?.Configure(context, read, write));

        public static void AddIntInspector(this IUIContext context,
            string label,
            Func<int> read,
            Action<int> write,
            string? prefabName = null) =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context,
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<InputField>(prefabName)?.Configure(context, read, write));

        public static void AddFloatInspector(this IUIContext context,
            string label,
            Func<float> read,
            Action<float> write,
            string? prefabName = null) =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context,
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<InputField>(prefabName)?.Configure(context, read, write));
        
        public static void AddStringField(this IUIContext context,
            Func<string> read,
            Action<string> write,
            DisplayOptions? displayOptions = null,
            string? prefabName = null) =>
            context.Resolver.Resolve<InputField>(prefabName)?.Configure(context, read, write);

        public static void AddIntField(this IUIContext context,
            Func<int> read,
            Action<int> write,
            DisplayOptions? displayOptions = null,
            string? prefabName = null) =>
            context.Resolver.Resolve<InputField>(prefabName)?.Configure(context, read, write);

        public static void AddFloatField(this IUIContext context,
            Func<float> read,
            Action<float> write,
            DisplayOptions? displayOptions = null,
            string? prefabName = null) =>
            context.Resolver.Resolve<InputField>(prefabName)?.Configure(context, read, write);
    }
}