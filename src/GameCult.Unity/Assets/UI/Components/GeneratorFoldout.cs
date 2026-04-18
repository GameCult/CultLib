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
    public class GeneratorFoldout : Generator, IFieldHandler, ILayoutElement, ITextElement
    {
        [SerializeField] private TextMeshProUGUI? label;
        [SerializeField] private Image? foldoutIcon;
        [SerializeField] private Button? button;
        [SerializeField] private VerticalLayoutGroup? layoutGroup;
        [SerializeField] private LayoutElement? layoutElement;
        [SerializeField] private float foldoutRotationDamping = 10f;
        [SerializeField] private int foldedPadding = -8;
        [SerializeField] private int expandedPadding = 8;
        public event Action<bool>? OnExpand;

        private bool _expanded;
        private float _targetFoldoutRotation;
        private float _foldoutRotation;

        public TextMeshProUGUI? Text => label;

        public LayoutElement? LayoutElement => layoutElement;

        public string Title
        {
            set
            {
                if (label != null) label.text = value;
            }
        }

        public bool Expanded => _expanded;

        public new void Awake()
        {
            button?.onClick.AddListener(ToggleExpand);
            OnPropertyAdded += go => go.SetActive(_expanded);
        }

        public override void Update()
        {
            RefreshValues();
            _foldoutRotation =
                Mathf.Lerp(_foldoutRotation, _targetFoldoutRotation, foldoutRotationDamping * Time.deltaTime);
            if (foldoutIcon is not null) foldoutIcon.transform.localRotation = Quaternion.Euler(0, 0, _foldoutRotation);
        }

        public void ToggleExpand() => SetExpanded(!_expanded, false);

        public void SetExpanded(bool expanded, bool force)
        {
            _expanded = expanded;
            if (layoutGroup != null)
            {
                var padding = layoutGroup.padding;
                padding = new RectOffset(padding.left, padding.right, padding.top, _expanded ? expandedPadding : foldedPadding);
                layoutGroup.padding = padding;
            }

            foreach (var property in Elements) property.SetActive(_expanded);
            _targetFoldoutRotation = _expanded ? -90 : 0;
            if (force)
            {
                _foldoutRotation = _targetFoldoutRotation;
                if (foldoutIcon != null) foldoutIcon.transform.localRotation = Quaternion.Euler(0, 0, _foldoutRotation);
            }
            OnExpand?.Invoke(_expanded);
        }

        public int Priority => 10;
        
        public bool CanHandle(Type type, PreferredInspectorAttribute? attr) => 
            type.IsEnum && type.GetCustomAttribute<FlagsAttribute>() != null;

        public void ConfigureForMember(IUIContext context, object target, MemberInfo member, DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            if (label is null || button is null) return;
            var type = member is FieldInfo fi ? fi.FieldType : ((PropertyInfo)member).PropertyType;
            label.text = type.Name.SplitCamelCase();
            var names = Enum.GetNames(type);
            var values = Enum.GetValues(type);

            // Get current bitfield as ulong for flexibility
            Func<ulong> read = () => Convert.ToUInt64(member is FieldInfo f ? f.GetValue(target)
                : ((PropertyInfo)member).GetValue(target));

            // Build one checkbox per flag
            foreach (var val in values)
            {
                ulong bit = Convert.ToUInt64(val);
                string flagName = Enum.GetName(type, val) ?? "Default";

                if (bit == 0)
                {
                    // Handle "None" specially
                    this.AddBoolInspector(flagName,
                        () => Convert.ToUInt64(read()) == 0,
                        b =>
                        {
                            if (b)
                            {
                                // Setting "None" clears all bits
                                var newEnum = Enum.ToObject(type, 0);
                                if (member is FieldInfo f2) f2.SetValue(target, newEnum);
                                else if (member is PropertyInfo p2) p2.SetValue(target, newEnum);
                            }
                            // Unsetting None is a no-op
                        });
                }
                else
                {
                    this.AddBoolInspector(flagName,
                        () => (read() & bit) != 0,
                        b =>
                        {
                            ulong value = Convert.ToUInt64(read());
                            if (b) value |= bit;
                            else value &= ~bit;

                            var newEnum = Enum.ToObject(type, value);
                            if (member is FieldInfo f2) f2.SetValue(target, newEnum);
                            else if (member is PropertyInfo p2) p2.SetValue(target, newEnum);
                        });
                }
            }
            
            button.interactable = displayOptions?.Interactable??true;
            this.ApplyLayoutOptions(displayOptions);
            this.ApplyTextOptions(displayOptions);
        }
        
        public GeneratorFoldout Configure<TEnum>(IUIContext context, Func<TEnum> read, Action<TEnum> write, DisplayOptions? displayOptions = null) where TEnum : Enum
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            if (label is null || button is null) return this;
            label.text = typeof(TEnum).Name.SplitCamelCase();

            foreach (TEnum val in Enum.GetValues(typeof(TEnum)))
            {
                ulong bit = Convert.ToUInt64(val);
                string flagName = Enum.GetName(typeof(TEnum), val) ?? "Default";

                if (bit == 0)
                {
                    this.AddBoolInspector(flagName,
                        () => Convert.ToUInt64(read()) == 0,
                        b =>
                        {
                            if (b)
                            {
                                write((TEnum)Enum.ToObject(typeof(TEnum), 0UL));
                            }
                        });
                }
                else
                {
                    this.AddBoolInspector(flagName,
                        () => (Convert.ToUInt64(read()) & bit) != 0,
                        b =>
                        {
                            ulong value = Convert.ToUInt64(read());
                            if (b) value |= bit;
                            else value &= ~bit;
                            write((TEnum)Enum.ToObject(typeof(TEnum), value));
                        });
                }
            }
            
            button.interactable = displayOptions?.Interactable??true;
            this.ApplyLayoutOptions(displayOptions);
            this.ApplyTextOptions(displayOptions);

            return this;
        }

        public GeneratorFoldout Configure(IUIContext context, string labelText, DisplayOptions? displayOptions = null, params LayoutComponent[] children)
        {
            transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            
            if (label is null || button is null) return this;
            label.text = labelText;
            button.interactable = displayOptions?.Interactable??true;
            this.ApplyLayoutOptions(displayOptions);
            this.ApplyTextOptions(displayOptions);
            
            foreach (var component in children)
            {
                if (component != null) AddComponent(component);
            }
            
            return this;
        }

        public void AddComponent(LayoutComponent component)
        {
            component.transform.SetParent(ContentRoot, false);
        }
    }
    public static class EnumGeneratorExtensions
    {
        public static void AddFlagsEnumInspector<TEnum>(
            this IUIContext context, string label,
            Func<TEnum> read, Action<TEnum> write)
            where TEnum : Enum =>
            context.Resolver.Resolve<HorizontalGroup>()?.Configure(context, 
                context.Resolver.Resolve<Label>()?.Configure(context, label),
            context.Resolver.Resolve<GeneratorFoldout>()?.Configure(context, read, write));
    }
}