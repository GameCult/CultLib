using UnityEditor;
using UnityEngine;

namespace CultMath.Unity.Samples.Editor;

public static class CultMathPlanetaryViewerMenu
{
    [MenuItem("GameObject/CultMath/Planetary Field Viewer", false, 10)]
    public static void CreateViewer()
    {
        var root = new GameObject("CultMath Planetary Field Viewer");
        Undo.RegisterCreatedObjectUndo(root, "Create CultMath Planetary Viewer");
        root.AddComponent<CultMathPlanetaryViewer>();
        Selection.activeGameObject = root;

        if (Camera.main != null) return;
        var cameraObject = new GameObject("Planetary Viewer Camera");
        Undo.RegisterCreatedObjectUndo(cameraObject, "Create Planetary Viewer Camera");
        cameraObject.tag = "MainCamera";
        cameraObject.transform.position = new Vector3(0, 0, -24);
        cameraObject.transform.LookAt(root.transform);
        var camera = cameraObject.AddComponent<Camera>();
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 1000;
    }
}
