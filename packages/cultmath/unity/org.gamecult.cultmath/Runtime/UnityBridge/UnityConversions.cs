using UnityEngine;

namespace CultMath.UnityBridge
{
    /// <summary>
    /// Explicit conversions between UnityEngine and CultMath value types.
    /// C# only admits user-defined conversion operators declared on the source or target
    /// type, and CultMath's core types stay engine-free, so the bridge is extension methods.
    /// </summary>
    public static class UnityConversions
    {
        public static float2 ToCultMath(this Vector2 value) => new float2(value.x, value.y);
        public static float3 ToCultMath(this Vector3 value) => new float3(value.x, value.y, value.z);
        public static float4 ToCultMath(this Vector4 value) => new float4(value.x, value.y, value.z, value.w);
        public static float4 ToCultMath(this Color value) => new float4(value.r, value.g, value.b, value.a);
        public static quaternion ToCultMath(this Quaternion value) => new quaternion(value.x, value.y, value.z, value.w);
        public static int2 ToCultMath(this Vector2Int value) => new int2(value.x, value.y);
        public static int3 ToCultMath(this Vector3Int value) => new int3(value.x, value.y, value.z);

        public static Vector2 ToUnity(this float2 value) => new Vector2(value.x, value.y);
        public static Vector3 ToUnity(this float3 value) => new Vector3(value.x, value.y, value.z);
        public static Vector4 ToUnity(this float4 value) => new Vector4(value.x, value.y, value.z, value.w);
        public static Quaternion ToUnity(this quaternion value) => new Quaternion(value.x, value.y, value.z, value.w);
        public static Vector2Int ToUnity(this int2 value) => new Vector2Int(value.x, value.y);
        public static Vector3Int ToUnity(this int3 value) => new Vector3Int(value.x, value.y, value.z);
        public static Color ToColor(this float4 value) => new Color(value.x, value.y, value.z, value.w);
    }
}
