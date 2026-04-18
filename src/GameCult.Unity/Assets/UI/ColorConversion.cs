using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

namespace GameCult.Unity.UI
{
    // Color conversion functions from https://www.chilliant.com/rgb2hsv.html
    public static class ColorConversion
    {
        private static float Epsilon = 1e-10f;

        public static float3 HUEtoRGB(in float h)
        {
            float R = abs(h * 6 - 3) - 1;
            float G = 2 - abs(h * 6 - 2);
            float B = 2 - abs(h * 6 - 4);
            return saturate(float3(R,G,B));
        }

        public static float3 RGBtoHCV(in float3 rgb)
        {
            // Based on work by Sam Hocevar and Emil Persson
            float4 P = (rgb.y < rgb.z) ? float4(rgb.zy, -1.0f, 2.0f/3.0f) : float4(rgb.yz, 0.0f, -1.0f/3.0f);
            float4 Q = (rgb.x < P.x) ? float4(P.xyw, rgb.x) : float4(rgb.x, P.yzx);
            float C = Q.x - min(Q.w, Q.y);
            float H = abs((Q.w - Q.y) / (6 * C + Epsilon) + Q.z);
            return float3(H, C, Q.x);
        }

        public static float3 HSVtoRGB(in float3 hsv)
        {
            float3 rgb = HUEtoRGB(hsv.x);
            return ((rgb - 1) * hsv.y + 1) * hsv.z;
        }

        public static float3 HSLtoRGB(in float3 hsl)
        {
            float3 RGB = HUEtoRGB(hsl.x);
            float C = (1 - abs(2 * hsl.z - 1)) * hsl.y;
            return (RGB - 0.5f) * C + hsl.z;
        }

        // The weights of RGB contributions to luminance.
        // Should sum to unity.
        private static float3 HCYwts = float3(0.299f, 0.587f, 0.114f);
 
        public static float3 HCYtoRGB(float3 hcy)
        {
            float3 RGB = HUEtoRGB(hcy.x);
            float Z = dot(RGB, HCYwts);
            if (hcy.z < Z)
            {
                hcy.y *= hcy.z / Z;
            }
            else if (Z < 1)
            {
                hcy.y *= (1 - hcy.z) / (1 - Z);
            }
            return (RGB - Z) * hcy.y + hcy.z;
        }

        public static float3 RGBtoHSV(in float3 rgb)
        {
            float3 HCV = RGBtoHCV(rgb);
            float S = HCV.y / (HCV.z + Epsilon);
            return float3(HCV.x, S, HCV.z);
        }

        public static float3 RGBtoHSL(in float3 rgb)
        {
            float3 HCV = RGBtoHCV(rgb);
            float L = HCV.z - HCV.y * 0.5f;
            float S = HCV.y / (1 - abs(L * 2 - 1) + Epsilon);
            return float3(HCV.x, S, L);
        }

        public static float3 RGBtoHCY(in float3 rgb)
        {
            // Corrected by David Schaeffer
            float3 HCV = RGBtoHCV(rgb);
            float Y = dot(rgb, HCYwts);
            float Z = dot(HUEtoRGB(HCV.x), HCYwts);
            if (Y < Z)
            {
                HCV.y *= Z / (Epsilon + Y);
            }
            else
            {
                HCV.y *= (1 - Z) / (Epsilon + 1 - Y);
            }
            return float3(HCV.x, HCV.y, Y);
        }

        public static Color ToColor(this float3 color) => new Color(color.x, color.y, color.z);
        public static float3 ToFloat3(this Color color) => new float3(color.r, color.g, color.b);
    }
}