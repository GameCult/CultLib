// E:\Projects\CultLib\src\GameCult.Unity\Assets\UI\Components\ButtonField.cs

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
    public class ButtonField : LayoutComponent, IFieldHandler, ITextElement
    {
        [SerializeField] private TextMeshProUGUI? buttonLabel;
        [SerializeField] private Button? button;

        public int Priority => 10;

        public TextMeshProUGUI? Text => buttonLabel;

        public bool CanHandle(Type type, PreferredInspectorAttribute? attr) =>
            type == typeof(Action);

        public void ConfigureForMember(IUIContext context, object target, MemberInfo member, DisplayOptions? displayOptions = null)
        {
            Action action;
            switch (member)
            {
                case FieldInfo field:
                    action = (Action)field.GetValue(target);
                    break;
                case PropertyInfo property:
                    action = (Action)property.GetValue(target);
                    break;
                default:
                    return;
            }
            Configure(context, member.Name, action, displayOptions);
        }

        public ButtonField Configure(IUIContext context, string labelText, Action onClick, DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            if (buttonLabel is null || button is null) return this;
            buttonLabel.text = labelText;
            button.onClick.AddListener(() => onClick());
            
            button.interactable = displayOptions?.Interactable??true;
            this.ApplyLayoutOptions(displayOptions);
            this.ApplyTextOptions(displayOptions);

            return this;
        }

        public Component Component => this;
    }

    public static class ButtonFieldGeneratorExtension
    {
        public static void AddButtonInspector(this IUIContext context,
            string label,
            string buttonLabel,
            Action onClick,
            string? prefabName = null) =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context, 
                context.Resolver.Resolve<Label>()?.Configure(context, label),
                context.Resolver.Resolve<ButtonField>(prefabName)?.Configure(context, buttonLabel, onClick));

        public static void AddButton(this IUIContext context,
            string label,
            Action onClick,
            DisplayOptions? displayOptions = null,
            string? prefabName = null) =>
            context.Resolver.Resolve<ButtonField>(prefabName)?.Configure(context, label, onClick, displayOptions);
    }
}