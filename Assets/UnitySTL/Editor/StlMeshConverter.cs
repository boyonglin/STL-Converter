using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityVolumeRendering;

public class StlMeshConverter : EditorWindow
{
    private PreviewRenderUtility previewRenderer;
    private bool isDoubleSided = true;
    private float cameraDistance = 1.0f;
    private bool useFinerMesh;
    private const float finerFactor = 1.5f;
    private bool useWatertight = true;
    private List<Transform> detectedCutoutBoxes;

    /// <summary>
    /// Initializes the preview renderer.
    /// </summary>
    public void Initialize()
    {
        previewRenderer = new PreviewRenderUtility
        {
            cameraFieldOfView = 60,
            camera =
            {
                clearFlags = CameraClearFlags.Skybox,
                transform =
                {
                    position = new Vector3(0, 0, -1)
                },
                nearClipPlane = 0.1f,
                farClipPlane = 100
            }
        };

        var directionalLights = FindDirectionalLights();
        if (directionalLights.Length > 0)
        {
            previewRenderer.lights[0].transform.rotation = directionalLights[0].transform.rotation;
        }
        previewRenderer.lights[0].intensity = 1;
        for (var i = 1; i < previewRenderer.lights.Length; ++i)
        {
            previewRenderer.lights[i].intensity = 0;
        }
    }

    private static Light[] FindDirectionalLights()
    {
        return FindObjectsByType<Light>(FindObjectsSortMode.None)
            .Where(light => light.type == LightType.Directional)
            .ToArray();
    }

    /// <summary>
    /// Opens the STL Mesh Converter window.
    /// </summary>
    [MenuItem("Tools/STL Mesh Converter")]
    private static void InitializeWindow()
    {
        var window = GetWindow<StlMeshConverter>("STL Mesh Converter", true);
        window.titleContent.tooltip = "STL Mesh Converter";
        window.autoRepaintOnSceneChange = true;
        window.Show();
    }

    private void Update()
    {
        Repaint();
    }

    /// <summary>
    /// Gets all CutoutBoxes on Layer 7 (CutoutBox) in the scene.
    /// </summary>
    private static List<Transform> GetCutoutBoxes()
    {
        var cutoutBoxes = FindObjectsByType<CutoutBox>(FindObjectsSortMode.None);
        return cutoutBoxes
            .Select(cb => cb.transform)
            .Where(t => t.gameObject.layer == 7)
            .ToList();
    }

    /// <summary>
    /// Called when the GUI is drawn.
    /// </summary>
    public void OnGUI()
    {
        if (!Selection.activeGameObject)
        {
            EditorGUILayout.LabelField("Please select a VolumeRenderedObject in Hierarchy.");
            return;
        }

        var meshFilters = Selection.activeGameObject.GetComponentsInChildren<MeshFilter>();
        var skinnedMeshRenderers = Selection.activeGameObject.GetComponentsInChildren<SkinnedMeshRenderer>();
        if (meshFilters == null || meshFilters.Length == 0)
        {
            EditorGUILayout.LabelField("Game Object does not contain the required components.");
            return;
        }

        if (previewRenderer == null) Initialize();
        if (previewRenderer == null) return;
        
        detectedCutoutBoxes = GetCutoutBoxes();
        var cutoutBoxCount = detectedCutoutBoxes.Count;
        
        // Handle mouse scroll wheel input for camera distance
        var currentEvent = Event.current;
        if (currentEvent.type == EventType.ScrollWheel)
        {
            var scrollDelta = currentEvent.delta.y / 3;
            cameraDistance = Mathf.Clamp(cameraDistance + scrollDelta * 0.1f, 0.1f, 5.0f);
            currentEvent.Use(); // Consume the event
            Repaint(); // Force a repaint to update the view
        }

        previewRenderer.camera.transform.RotateAround(Vector3.zero, Vector3.up, Time.deltaTime);

        var boundaries = new Rect(0, 0, position.width, position.height);
        previewRenderer.BeginPreview(boundaries, GUIStyle.none);

        foreach (var filter in meshFilters)
        {
            if (filter.TryGetComponent<MeshRenderer>(out var meshRenderer))
            {
                DrawSelectedMesh(filter.sharedMesh, meshRenderer.sharedMaterial, filter.transform);
            }
        }

        foreach (var skin in skinnedMeshRenderers)
        {
            var mesh = new Mesh();
            skin.BakeMesh(mesh);
            DrawSelectedMesh(mesh, skin.sharedMaterial, skin.transform);
        }

        previewRenderer.camera.Render();
        var render = previewRenderer.EndPreview();
        GUI.DrawTexture(new Rect(0, 0, boundaries.width, boundaries.height), render);

        // Update camera position and sync with slider
        cameraDistance = EditorGUILayout.Slider(Mathf.Round(cameraDistance * 10f) / 10f, 0.1f, 5);
        previewRenderer.camera.transform.position = previewRenderer.camera.transform.position.normalized * cameraDistance;

        var whiteTextStyle = new GUIStyle(EditorStyles.label)
        {
            normal = { textColor = Color.white }
        };

        // Select DICOM row
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Select DICOM", whiteTextStyle, GUILayout.Width(88));
        EditorGUILayout.LabelField(Selection.activeGameObject.name, whiteTextStyle);
        EditorGUILayout.EndHorizontal();

        // CutoutBox count row
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Cutout Box", whiteTextStyle, GUILayout.Width(88));
        
        // Count inclusive and exclusive cutout boxes
        var inclusiveCount = detectedCutoutBoxes.Count(t => t.GetComponent<CutoutBox>()?.cutoutType == CutoutType.Inclusive);
        var exclusiveCount = detectedCutoutBoxes.Count(t => t.GetComponent<CutoutBox>()?.cutoutType == CutoutType.Exclusive);
        
        string cutoutBoxText;
        if (cutoutBoxCount == 0)
        {
            cutoutBoxText = "0";
        }
        else
        {
            var parts = new List<string>();
            if (inclusiveCount > 0) parts.Add($"{inclusiveCount} inclusive");
            if (exclusiveCount > 0) parts.Add($"{exclusiveCount} exclusive");
            cutoutBoxText = string.Join(", ", parts);
        }
        
        EditorGUILayout.LabelField(cutoutBoxText, whiteTextStyle);
        EditorGUILayout.EndHorizontal();

        // Options row
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Options", whiteTextStyle, GUILayout.Width(88));
        EditorGUILayout.BeginVertical();
        
        // Make toggles not focusable but keep them enabled and colored
        GUI.SetNextControlName("DoubleSidedToggle");
        isDoubleSided = EditorGUILayout.Toggle("Double Sided", isDoubleSided);
        GUI.SetNextControlName("WatertightToggle");
        useWatertight = EditorGUILayout.Toggle("Watertight Mesh", useWatertight);
        GUI.SetNextControlName("FinerMeshToggle");
        useFinerMesh = EditorGUILayout.Toggle($"Finer Mesh ({finerFactor}x)", useFinerMesh);
        if (GUI.GetNameOfFocusedControl() == "DoubleSidedToggle" ||
            GUI.GetNameOfFocusedControl() == "FinerMeshToggle" ||
            GUI.GetNameOfFocusedControl() == "WatertightToggle")
        {
            GUI.FocusControl(null);
        }
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();

        // Export STL row
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Export STL", whiteTextStyle, GUILayout.Width(88));
        var volumeObject = Selection.activeGameObject.GetComponent<VolumeRenderedObject>() 
                           ?? Selection.activeGameObject.transform.parent?.GetComponent<VolumeRenderedObject>();
        var upsamplingFactor = useFinerMesh ? finerFactor : 1.0f;
        if (GUILayout.Button("Binary (1.0x)", GUILayout.ExpandWidth(true)))
        {
            StlExporter.Export(true, volumeObject, detectedCutoutBoxes, isDoubleSided, upsamplingFactor, useWatertight);
        }
        if (GUILayout.Button("ASCII (4.0x)", GUILayout.ExpandWidth(true)))
        {
            StlExporter.Export(false, volumeObject, detectedCutoutBoxes, isDoubleSided, upsamplingFactor, useWatertight);
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSelectedMesh(Mesh mesh, Material material, Transform transform)
    {
        previewRenderer.DrawMesh(mesh, Matrix4x4.TRS(transform.position, transform.rotation, transform.localScale),
            material, 0);
    }

    /// <summary>
    /// Cleans up the preview renderer when the window is disabled.
    /// </summary>
    private void OnDisable()
    {
        if (previewRenderer != null)
        {
            previewRenderer.Cleanup();
            previewRenderer = null;
        }
    }
}
