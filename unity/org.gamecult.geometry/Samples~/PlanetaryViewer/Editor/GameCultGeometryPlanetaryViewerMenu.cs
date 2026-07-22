using UnityEditor;
using UnityEngine;
using GameCult.Geometry.Unity.Samples;

namespace GameCult.Geometry.Unity.Samples.Editor
{

public static class GameCultGeometryPlanetaryViewerMenu
{
    [MenuItem("GameObject/GameCult.Geometry/Planetary Field Viewer", false, 10)]
    public static void CreateViewer()
    {
        var root = new GameObject("GameCult.Geometry Planetary Field Viewer");
        Undo.RegisterCreatedObjectUndo(root, "Create GameCult.Geometry Planetary Viewer");
        root.AddComponent<GameCultGeometryPlanetaryViewer>();
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
}
