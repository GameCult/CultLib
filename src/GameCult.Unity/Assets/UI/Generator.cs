using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using GameCult.Unity.UI.Components;
using UnityEngine;
using ZLinq;

namespace GameCult.Unity.UI
{
    public class Generator : ResolverComponent, IUIContext
    {
        [SerializeField] private float inspectorLabelPriority = 3;
        [SerializeField] private float inspectorFieldPriority = 2;
        [SerializeField] private ReflectiveResolver? resolver;
        [SerializeField] private RectTransform? contentRoot;
        protected event Action<GameObject>? OnPropertyAdded;
        protected event Action? RefreshPropertyValues;
        
        protected List<GameObject> Elements { get; } = new();
        public IUIResolver Resolver => resolver ?? throw new NullReferenceException("Generator doesn't have an assigned resolver.");
        public Action? Refresh
        {
            get => RefreshPropertyValues;
            set => RefreshPropertyValues = value;
        }

        public RectTransform ContentRoot => contentRoot ?? (RectTransform)transform;
        public Canvas Canvas => GetComponentInParent<Canvas>();
        public RectTransform CanvasRoot => (RectTransform)Canvas.transform;
        
        public void Inspect(object obj, bool inspectableOnly = false, bool recursive = true, bool readOnly = false)
        {
            var members = obj.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance);
            foreach (var member in members)
            {
                var inspectable = member.GetCustomAttribute<InspectableAttribute>();
                if (inspectable == null && inspectableOnly) continue;
                var preferred = inspectable as PreferredInspectorAttribute;
                switch (member)
                {
                    case FieldInfo field:
                    {
                        var type = field.FieldType;
                        var usedAttr = GetEffectivePreferredAttribute(preferred, type, readOnly);
                        var handler = Resolver.ResolveField(type, usedAttr);
                        // If there's a handler for this field and attribute type, use that
                        if (handler != null)
                        {
                            var group = Resolver.Resolve<HorizontalGroup>()?.Configure(this);
                            if (group == null) continue;
                            Resolver.Resolve<Label>()?.Configure(group, member.Name.SplitCamelCase());
                            handler.ConfigureForMember(group, obj, field);
                        }
                        else
                        {
                            // No field handler found;
                            // If recursion is enabled and the field is a class marked inspectable or if we don't care,
                            // Generate a foldout and then inspect recursively inside the foldout
                            if (recursive && field.FieldType.IsClass &&
                                (!inspectableOnly || field.FieldType.GetCustomAttribute<InspectableAttribute>() != null))
                            {
                                var foldout = Resolver.Resolve<GeneratorFoldout>();
                                if (foldout != null)
                                {
                                    foldout.Title = member.Name.SplitCamelCase();
                                    foldout.Inspect(field.GetValue(obj), inspectableOnly, recursive, readOnly);
                                }
                            }
                            // If all else fails, just display the ToString
                            else this.AddLabelInspector(member.Name.SplitCamelCase(), () => FormatValue(field.GetValue(obj), type));
                        }
                        continue;
                    }
                    case PropertyInfo property:
                    {
                        var type = property.PropertyType;
                        var isReadOnly = !property.CanWrite;
                        var usedAttr = GetEffectivePreferredAttribute(preferred, type, readOnly || isReadOnly);
                        var handler = Resolver.ResolveField(type, usedAttr);
                        if (handler != null)
                        {
                            var group = Resolver.Resolve<HorizontalGroup>()?.Configure(this);
                            if (group == null) continue;
                            Resolver.Resolve<Label>()?.Configure(group, member.Name.SplitCamelCase());
                            handler.ConfigureForMember(group, obj, property);
                        }
                        else
                        {
                            if (recursive && property.PropertyType.IsClass &&
                                (!inspectableOnly || property.PropertyType.GetCustomAttribute<InspectableAttribute>() != null))
                            {
                                var foldout = Resolver.Resolve<GeneratorFoldout>();
                                if (foldout != null)
                                {
                                    foldout.Title = member.Name.SplitCamelCase();
                                    foldout.Inspect(property.GetValue(obj), inspectableOnly, recursive, readOnly || isReadOnly);
                                }
                            }
                            else this.AddLabelInspector(member.Name.SplitCamelCase(), () => FormatValue(property.GetValue(obj), type));
                        }
                        continue;
                    }
                }
            }
        }

        private static PreferredInspectorAttribute? GetEffectivePreferredAttribute(PreferredInspectorAttribute? preferred, Type type, bool readOnly)
        {
            if (readOnly)
            {
                if (preferred != null) return preferred.AsReadOnly();
                if (IsDisplayablePrimitive(type)) return new InspectableReadOnlyLabelAttribute();
                return null;
            }

            return preferred;
        }

        private static string FormatValue(object? value, Type type)
        {
            if (value == null) return "Null";
            if (type == typeof(float)) return ((float)value).ToString("0.###");
            if (type == typeof(double)) return ((double)value).ToString("0.###");
            if (type == typeof(decimal)) return ((decimal)value).ToString("0.###");
            if (type.IsEnum) return value.ToString()?.SplitCamelCase() ?? "Null";
            return value.ToString() ?? "Null";
        }

        private static bool IsDisplayablePrimitive(Type type)
        {
            return type.IsPrimitive || type == typeof(string) || type == typeof(decimal);
        }
        
        public virtual void Update()
        {
            RefreshValues();
        }
        
        protected void RefreshValues()
        {
            RefreshPropertyValues?.Invoke();
        }
        
        public void Register(GameObject go)
        {
            Elements.Add(go);
            OnPropertyAdded?.Invoke(go);
        }

        public void Clear()
        {
            foreach(var element in Elements)
                Destroy(element);
            Elements.Clear();
        }
    }
}
