using System;
using System.Collections.Generic;
using System.Reflection;
using GameCult.Unity.UI.Components;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GameCult.Unity.UI
{
    public interface IFieldHandler
    {
        int Priority { get; }
        bool CanHandle(Type type, PreferredInspectorAttribute? attr);
        void ConfigureForMember(IUIContext context, object target, MemberInfo member, DisplayOptions? displayOptions = null);
    }

    public interface IUIResolver
    {
        // For general field generation (reflection, attributes, etc.)
        IFieldHandler? ResolveField(Type type, PreferredInspectorAttribute? attr);

        // For declarative, known-type composition
        T? Resolve<T>(string? prefabName = null) where T : ResolverComponent;
    }

    public interface IUIContext
    {
        IUIResolver Resolver { get; }
        RectTransform ContentRoot { get; }
        Canvas Canvas { get; }
        RectTransform CanvasRoot { get; }
        void Register(GameObject go);
        Action? Refresh { get; set; }
        // DisplayOptions InspectorLabelDisplayOptions { get; }
        // DisplayOptions InspectorFieldDisplayOptions { get; }
    }
    
    public interface ILayoutElement
    {
        public LayoutElement? LayoutElement { get; }
    }

    public interface ITextElement
    {
        public TextMeshProUGUI? Text { get; }
    }

    public interface IMultipleTextElement
    {
        public IEnumerable<TextMeshProUGUI> Text { get; }
    }

    public static class DisplayOptionsExtensions
    {
        public static void ApplyLayoutOptions(this ILayoutElement layoutElement, DisplayOptions? displayOptions)
        {
            if(displayOptions == null || layoutElement.LayoutElement == null) return;
            
            if(displayOptions.LayoutPriority != null) layoutElement.LayoutElement.flexibleWidth = displayOptions.LayoutPriority.Value;
            if(displayOptions.LayoutHeight != null) layoutElement.LayoutElement.minHeight = displayOptions.LayoutHeight.Value;
        }
        
        public static void ApplyTextOptions(this ITextElement textElement, DisplayOptions? displayOptions)
        {
            if(displayOptions == null || textElement.Text is null) return;
            
            if(displayOptions.TextAlignment != null) textElement.Text.alignment = displayOptions.TextAlignment.Value;
            if(displayOptions.VerticalAlignment != null) textElement.Text.verticalAlignment = displayOptions.VerticalAlignment.Value;
            if(displayOptions.TextSize != null) textElement.Text.fontSize = displayOptions.TextSize.Value;
        }
        
        public static void ApplyTextOptions(this IMultipleTextElement textElement, DisplayOptions? displayOptions)
        {
            if(displayOptions == null) return;
            
            foreach(var text in textElement.Text)
            {
                if (displayOptions.TextAlignment != null) text.alignment = displayOptions.TextAlignment.Value;
                if (displayOptions.VerticalAlignment != null) text.verticalAlignment = displayOptions.VerticalAlignment.Value;
                if (displayOptions.TextSize != null) text.fontSize = displayOptions.TextSize.Value;
            }
        }
    }
}