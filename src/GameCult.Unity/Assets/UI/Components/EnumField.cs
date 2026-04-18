/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. */

using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace GameCult.Unity.UI.Components
{
    public class EnumField : LayoutComponent, IFieldHandler, ITextElement
    {
        [SerializeField] private Button? dropdown;
        [SerializeField] private TextMeshProUGUI? dropdownLabel;

        public TextMeshProUGUI? Text => dropdownLabel;
        
        public int Priority => 10;
        
        public bool CanHandle(Type type, PreferredInspectorAttribute? attr) =>
            type.IsEnum && type.GetCustomAttribute<FlagsAttribute>() == null;

        public void ConfigureForMember(IUIContext context, object target, MemberInfo member, DisplayOptions? displayOptions = null)
        {
            var fieldType = member is FieldInfo fi ? fi.FieldType : ((PropertyInfo)member).PropertyType;
            var names = Enum.GetNames(fieldType);
            var values = Enum.GetValues(fieldType);
            
            Configure(context, () =>
            {
                if(member is FieldInfo field) return Array.IndexOf(values, field.GetValue(target));
                if(member is PropertyInfo property) return Array.IndexOf(values, property.GetValue(target));
                return 0;
            },i =>
            {
                if(member is FieldInfo field) field.SetValue(target, values.GetValue(i));
                if(member is PropertyInfo property) property.SetValue(target, values.GetValue(i));
            }, names, displayOptions);
        }

        public EnumField Configure(IUIContext context, Func<int> read, Action<int> write, string[] options, DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            
            if (dropdownLabel is null || dropdown is null) return this;
            
            dropdown.onClick.AddListener(() =>
            {
                var modal = context.ShowModal();
                if (modal == null) return;
                for (var i = 0; i < options.Length; i++)
                {
                    var i1 = i;
                    modal.Resolver.Resolve<ButtonField>("Enum")?.Configure(modal, options[i], () =>
                    {
                        write(i1);
                        Destroy(modal.gameObject);
                    });
                }
                modal.AlignVertical((RectTransform)dropdown.transform);
            });

            dropdown.interactable = displayOptions?.Interactable ?? true;
            this.ApplyLayoutOptions(displayOptions);
            this.ApplyTextOptions(displayOptions);
            
            context.Refresh += () => dropdownLabel.text = options[read()];
            return this;
        }
    }

    public static class EnumFieldGeneratorExtension
    {
        public static void AddEnumInspector(this IUIContext context,
            string label,
            Func<int> read,
            Action<int> write,
            string[] options,
            string? prefabName = null) =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context, 
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<EnumField>(prefabName)?.Configure(context, read, write, options));

        public static void AddEnumInspector<TEnum>(this IUIContext context,
            string label,
            Func<TEnum> read,
            Action<TEnum> write,
            string? prefabName = null) where TEnum : Enum
        {
            var enumType = typeof(TEnum);
            var names = Enum.GetNames(enumType);
            var values = Enum.GetValues(enumType);
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context,
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<EnumField>(prefabName)?.Configure(context,
                    () => Array.IndexOf(values, read()),
                    i => write((TEnum)values.GetValue(i)), names));
        }
        
        public static void AddEnumField(this IUIContext context,
            Func<int> read,
            Action<int> write,
            string[] options,
            string? prefabName = null) =>
            context.Resolver.Resolve<EnumField>(prefabName)?.Configure(context, read, write, options);

        public static void AddEnumField<TEnum>(this IUIContext context,
            Func<TEnum> read,
            Action<TEnum> write,
            string? prefabName = null) where TEnum : Enum
        {
            var enumType = typeof(TEnum);
            var names = Enum.GetNames(enumType);
            var values = Enum.GetValues(enumType);
            context.Resolver.Resolve<EnumField>(prefabName)?.Configure(context, 
                () => Array.IndexOf(values, read()),
                i => write((TEnum)values.GetValue(i)), names);
        }
    }
}