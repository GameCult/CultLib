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
    public class BoolField : LayoutComponent, IFieldHandler
    {
        [SerializeField] private Toggle? toggle;

        public int Priority => 10;

        public bool CanHandle(Type type, PreferredInspectorAttribute? attr) =>
            type == typeof(bool) && attr == null;

        public void ConfigureForMember(IUIContext context, object target, MemberInfo member, DisplayOptions? options = null)
        {
            Func<bool> read;
            Action<bool> write;
            switch (member)
            {
                case FieldInfo field:
                    read = () => (bool)field.GetValue(target);
                    write = b => field.SetValue(target, b);
                    break;
                case PropertyInfo property:
                    read = () => (bool)property.GetValue(target);
                    write = b => property.SetValue(target, b);
                    break;
                default:
                    return;
            }
            Configure(context, read, write, options);
        }

        public BoolField Configure(IUIContext context, Func<bool> read, Action<bool> write, DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            if (toggle is null) return this;
            toggle.isOn = read();
            toggle.onValueChanged.AddListener(b => write(b));
            
            toggle.interactable = displayOptions?.Interactable??true;
            this.ApplyLayoutOptions(displayOptions);

            context.Refresh += () => toggle.isOn = read();
            return this;
        }
    }

    public static class BoolFieldGeneratorExtension
    {
        public static void AddBoolInspector(this IUIContext context,
            string label,
            Func<bool> read,
            Action<bool> write,
            string? prefabName = null) =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context, 
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<BoolField>(prefabName)?.Configure(context, read, write));
        
        public static void AddBoolField(this IUIContext context,
            Func<bool> read,
            Action<bool> write,
            string? prefabName = null,
            DisplayOptions? displayOptions = null) =>
            context.Resolver.Resolve<BoolField>(prefabName)?.Configure(context, read, write, displayOptions);
    }
}