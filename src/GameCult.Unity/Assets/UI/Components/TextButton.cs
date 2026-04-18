using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static Unity.Mathematics.math;

namespace GameCult.Unity.UI.Components
{
    public class TextButton : LayoutComponent, IPointerEnterHandler, IPointerExitHandler, ITextElement
    {
        [SerializeField] private Button? button;
        [SerializeField] private TextMeshProUGUI? label;
        [SerializeField] private float animationDuration;
        [SerializeField] private float hoverSpacing;

        private float _lerp;
        private bool _hovering;

        public TextMeshProUGUI? Text => label;

        private void Update()
        {
            _lerp = saturate(_lerp + Time.deltaTime / animationDuration * (button is { interactable: true } && _hovering ? 1 : -1));
            if (label is not null) label.characterSpacing = hoverSpacing * _lerp;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovering = true;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovering = false;
        }

        public TextButton Configure(IUIContext context, string labelText, Action onClick, DisplayOptions? displayOptions = null)
        {
            if(displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            if(button is null || label is null) return this;
            
            label.text = labelText;
            button.onClick.AddListener(() => onClick());
            
            button.interactable = displayOptions?.Interactable??true;
            this.ApplyLayoutOptions(displayOptions);
            this.ApplyTextOptions(displayOptions);

            return this;
        }
    }

    public static class TextButtonGeneratorExtensions
    {
        public static void AddTextButton(this IUIContext context, string label, Action onClick, DisplayOptions? displayOptions = null) =>
            context.Resolver.Resolve<TextButton>()?.Configure(context, label, onClick, displayOptions);
    }
}