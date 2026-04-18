using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;
using static GameCult.Unity.UI.ColorConversion;
using static Unity.Mathematics.math;
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

namespace GameCult.Unity.UI.Components
{
    public class ColorModal : Modal
    {
        private static readonly int Mode = Shader.PropertyToID("_Mode");
        private static readonly int H = Shader.PropertyToID("_H");
        private static readonly int S = Shader.PropertyToID("_S");
        private static readonly int V = Shader.PropertyToID("_V");
        private static readonly int Value = Shader.PropertyToID("_Value");
        [SerializeField] private ConstrainedSlider2D colorCircle;
        [SerializeField] private Slider hueSlider;
        [SerializeField] private Slider satSlider;
        [SerializeField] private Slider valSlider;
        [SerializeField] private EnumField modeEnum;
        [SerializeField] private TMP_InputField hexStringField;
        [SerializeField] private Image circleImage;
        [SerializeField] private Image previewImage;
        [SerializeField] private List<Image> sliderBackgrounds = new();
        [SerializeField] private List<Image> sliderHandles = new();
        [SerializeField] private List<Image> sliderHandleOutlines = new();
        [SerializeField] private List<Material> circleMaterials = new();
        [SerializeField] private List<Material> hueSliderMaterials = new();
        [SerializeField] private List<Material> satSliderMaterials = new();
        [SerializeField] private List<Material> valSliderMaterials = new();

        private Color _color = Color.red;
        private float3 _hsv;
        private ColorMode _mode = ColorMode.HSV;
        
        private float3 HSV
        {
            get => _hsv;
            set
            {
                _hsv = value;
                circleImage.material.SetFloat(Value, _hsv.z);
                foreach (var image in sliderBackgrounds)
                {
                    image.material.SetFloat(H, _hsv.x);
                    image.material.SetFloat(S, _hsv.y);
                    image.material.SetFloat(V, _hsv.z);
                }

                colorCircle.Value = PolarToRect(_hsv.xy);
                hueSlider.value = _hsv.x;
                satSlider.value = _hsv.y;
                valSlider.value = _hsv.z;
                RefreshColor();
            }
        }

        private Color Color
        {
            get => _color;
            set
            {
                _color = value;
                foreach (var handle in sliderHandles)
                {
                    handle.color = value;
                }
                previewImage.color = value;
                RefreshHex();
            }
        }

        private float2 RectToPolar(float2 rect)
        {
            var offset = rect * 2 - float2(1,1);
            return float2(atan2(offset.y, -offset.x) / PI2 + .5f, length(offset));
        }

        private float2 PolarToRect(float2 polar)
        {
            float angle = (polar.x - 0.5f) * PI2;
            float r = polar.y;
            var offset = float2(-r * cos(angle), r * sin(angle));
            return (offset + float2(1,1)) / 2;
        }

        private void RefreshColor()
        {
            Color = _mode switch
            {
                ColorMode.HSV => HSVtoRGB(HSV).ToColor().gamma,
                ColorMode.HSL => HSLtoRGB(HSV).ToColor().gamma,
                ColorMode.HCY => HCYtoRGB(HSV).ToColor().gamma,
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        private void RefreshHSV()
        {
            HSV = _mode switch
            {
                ColorMode.HSV => RGBtoHSV(_color.linear.ToFloat3()),
                ColorMode.HSL => RGBtoHSL(_color.linear.ToFloat3()),
                ColorMode.HCY => RGBtoHCY(_color.linear.ToFloat3()),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
        
        private void RefreshHex() => hexStringField.text = '#' + ColorUtility.ToHtmlStringRGB(Color);

        private void SetColorMode(ColorMode mode)
        {
            circleImage.material = circleMaterials[(int)mode];
            sliderBackgrounds[0].material = hueSliderMaterials[(int)mode];
            sliderBackgrounds[1].material = satSliderMaterials[(int)mode];
            sliderBackgrounds[2].material = valSliderMaterials[(int)mode];
        }
        
        // BUG: This should work, but neither setting the mode integer or shader keywords actually affects the material
        // private void SetColorMode(Material mat, ColorMode mode)
        // {
        //     mat.SetInteger("_Mode", (int)mode);
        //     var enumType = typeof(ColorMode);
        //     var names = Enum.GetNames(enumType);
        //     var values = Enum.GetValues(enumType);
        //     for (var i = 0; i < names.Length; i++)
        //     {
        //         if((ColorMode)values.GetValue(i) == mode)
        //         {
        //             mat.EnableKeyword("_Mode_" + names[i]);
        //             Debug.Log($"Enabling keyword {"_Mode_" + names[i]} on {mat.name}");
        //         }
        //         else
        //         {
        //             mat.DisableKeyword("_Mode_" + names[i]);
        //             Debug.Log($"Disabling keyword {"_Mode_" + names[i]} on {mat.name}");
        //         }
        //     }
        // }
        
        private void OnEnable()
        {
            colorCircle.ConstrainFunction = v =>
            {
                var offset = v * 2 - Vector2.one;
                if (offset.sqrMagnitude > 1) offset = offset.normalized;
                return (offset + Vector2.one) / 2;
            };
            colorCircle.OnValueChanged.AddListener(v => HSV = float3(RectToPolar(v),_hsv.z));
            hueSlider.onValueChanged.AddListener(h => HSV = float3(h, _hsv.yz));
            satSlider.onValueChanged.AddListener(s => HSV = float3(_hsv.x, s, _hsv.z));
            valSlider.onValueChanged.AddListener(v => HSV = float3(_hsv.xy, v));
            
            var enumType = typeof(ColorMode);
            var names = Enum.GetNames(enumType);
            var values = Enum.GetValues(enumType);
            modeEnum.Configure(this,
                () => Array.IndexOf(values, _mode),
                i =>
                {
                    _mode = (ColorMode)values.GetValue(i);
                    SetColorMode(_mode);
                    RefreshHSV();
                }, names, new DisplayOptions(placeInContext:false));
            RefreshHSV();
            
            hexStringField.onEndEdit.AddListener(s =>
            {
                if(ColorUtility.TryParseHtmlString(s, out Color color))
                {
                    Color = color;
                    RefreshHSV();
                }
                else
                    RefreshHex();
            });
            RefreshHex();
        }

        enum ColorMode
        {
            HSV,
            HSL,
            HCY
        }
    }
}