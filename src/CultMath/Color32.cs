namespace CultMath;

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public readonly record struct Color32(byte r, byte g, byte b, byte a = 255);
