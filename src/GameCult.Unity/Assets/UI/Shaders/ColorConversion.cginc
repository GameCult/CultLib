// Color conversion functions from https://www.chilliant.com/rgb2hsv.html
float Epsilon = 1e-6;

float3 HUEtoRGB(in float h)
{
    float R = abs(h * 6.0 - 3.0) - 1.0;
    float G = 2.0 - abs(h * 6.0 - 2.0);
    float B = 2.0 - abs(h * 6.0 - 4.0);
    return saturate(float3(R,G,B));
}

float3 RGBtoHCV(in float3 rgb)
{
    // Based on work by Sam Hocevar and Emil Persson
    float4 P = (rgb.g < rgb.b) ? float4(rgb.bg, -1.0, 2.0/3.0) : float4(rgb.gb, 0.0, -1.0/3.0);
    float4 Q = (rgb.r < P.x) ? float4(P.xyw, rgb.r) : float4(rgb.r, P.yzx);
    float C = Q.x - min(Q.w, Q.y);
    float H = abs((Q.w - Q.y) / (6 * C + Epsilon) + Q.z);
    return float3(H, C, Q.x);
}

float3 HSVtoRGB(in float3 hsv)
{
    float3 rgb = HUEtoRGB(hsv.x);
    return ((rgb - 1) * hsv.y + 1) * hsv.z;
}

float3 HSLtoRGB(in float3 hsl)
{
    float3 RGB = HUEtoRGB(hsl.x);
    float C = (1 - abs(2 * hsl.z - 1)) * hsl.y;
    return (RGB - 0.5) * C + hsl.z;
}

// The weights of RGB contributions to luminance.
// Should sum to unity.
#define HCYwts float3(0.299, 0.587, 0.114)
 
float3 HCYtoRGB(float3 hcy)
{
    float3 RGB = HUEtoRGB(hcy.x);
    float Z = dot(RGB, HCYwts);

    if (hcy.z < Z)
    {
        hcy.y *= hcy.z / Z;
    }
    else if (Z < 1.0)
    {
        hcy.y *= (1.0 - hcy.z) / (1.0 - Z);
    }
    return (RGB - Z) * hcy.y + hcy.z;
}

float3 RGBtoHSV(in float3 rgb)
{
    float3 HCV = RGBtoHCV(rgb);
    float S = HCV.y / (HCV.z + Epsilon);
    return float3(HCV.x, S, HCV.z);
}

float3 RGBtoHSL(in float3 rgb)
{
    float3 HCV = RGBtoHCV(rgb);
    float L = HCV.z - HCV.y * 0.5;
    float S = HCV.y / (1 - abs(L * 2 - 1) + Epsilon);
    return float3(HCV.x, S, L);
}

float3 RGBtoHCY(in float3 rgb)
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