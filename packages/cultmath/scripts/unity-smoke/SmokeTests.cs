using System;
using CultMath;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

public class SmokeTests
{
    // Compiles by FindKernel and dispatches on the device; false when the shader does not compile or the output is wrong.
    static bool CompilesAndRuns(string path)
    {
        var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
        Assert.IsNotNull(cs, path);
        int k;
        try { k = cs.FindKernel("CSMain"); } catch (Exception e) { Debug.Log("SMOKE " + path + " FindKernel threw: " + e.Message); return false; }
        if (!cs.HasKernel("CSMain") || !cs.IsSupported(k)) { Debug.Log("SMOKE " + path + " kernel unusable"); return false; }
        var buf = new ComputeBuffer(1, 16);
        try
        {
            cs.SetBuffer(k, "Out", buf);
            cs.Dispatch(k, 1, 1, 1);
            var o = new Vector4[1]; buf.GetData(o);
            Debug.Log($"SMOKE {path} out=({o[0].x},{o[0].y},{o[0].z},{o[0].w}) gpu={SystemInfo.graphicsDeviceType}");
            return o[0].x == 4f && o[0].y == 7f && o[0].z <= 8f && o[0].w >= 8f && o[0].w - o[0].z < 1e-3f;
        }
        finally { buf.Release(); }
    }

    [Test] public void CSharpIntervalAndAffineWork()
    {
        var r = math.iv_add(new float2(1, 2), new float2(3, 5));
        Assert.AreEqual(4f, r.x); Assert.AreEqual(7f, r.y);
        Assert.AreEqual(0f, math.snoise(new float3(0, 0, 0)));
    }

    [Test] public void RealShaderCompilesAndDispatches() { Assert.IsTrue(CompilesAndRuns("Assets/Shaders/Good.compute")); }

    [Test] public void BrokenShaderIsDetected()
    {
        LogAssert.ignoreFailingMessages = true;
        Assert.IsFalse(CompilesAndRuns("Assets/Shaders/Broken.compute"));
    }
}
