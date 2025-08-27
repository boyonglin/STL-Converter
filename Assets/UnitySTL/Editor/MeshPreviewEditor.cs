using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityVolumeRendering;

public class MeshPreviewEditor : EditorWindow
{
    private PreviewRenderUtility previewRenderer;
    private bool isDoubleSided = true;

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

    private Light[] FindDirectionalLights()
    {
        return FindObjectsByType<Light>(FindObjectsSortMode.None)
            .Where(light => light.type == LightType.Directional)
            .ToArray();
    }

    /// <summary>
    /// Opens the Mesh Preview Editor window.
    /// </summary>
    [MenuItem("Tools/Mesh Preview Editor")]
    private static void InitializeWindow()
    {
        var window = GetWindow<MeshPreviewEditor>("Mesh Preview Editor", true);
        window.titleContent.tooltip = "Mesh Preview Editor";
        window.autoRepaintOnSceneChange = true;
        window.Show();
    }

    private void Update()
    {
        Repaint();
    }

    /// <summary>
    /// Called when the GUI is drawn.
    /// </summary>
    public void OnGUI()
    {
        if (Selection.activeGameObject == null)
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

        if (previewRenderer == null)
        {
            Initialize();
        }
        
        previewRenderer.camera.transform.RotateAround(Vector3.zero, Vector3.up, Time.deltaTime);

        var boundaries = new Rect(0, 0, position.width, position.height);
        previewRenderer.BeginPreview(boundaries, GUIStyle.none);

        foreach (var filter in meshFilters)
        {
            var meshRenderer = filter.GetComponent<MeshRenderer>();
            if (meshRenderer)
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

        previewRenderer.camera.transform.position = previewRenderer.camera.transform.position.normalized *
                                                    EditorGUILayout.Slider(
                                                        Mathf.Round(
                                                            previewRenderer.camera.transform.position.magnitude * 10f) /
                                                        10f, 0.1f, 5);

        var whiteTextStyle = new GUIStyle(EditorStyles.label)
        {
            normal = { textColor = Color.white }
        };

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Select DICOM", whiteTextStyle, GUILayout.Width(88));
        EditorGUILayout.LabelField(Selection.activeGameObject.name, whiteTextStyle);
        EditorGUILayout.EndHorizontal();

        // Add double-sided checkbox
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Options", whiteTextStyle, GUILayout.Width(88));
        isDoubleSided = EditorGUILayout.Toggle("Double-Sided", isDoubleSided);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Export STL", whiteTextStyle, GUILayout.Width(88));
        var volumeObject = Selection.activeGameObject.GetComponent<VolumeRenderedObject>() 
                           ?? Selection.activeGameObject.transform.parent?.GetComponent<VolumeRenderedObject>();
        if (GUILayout.Button("Binary (1.0x)", GUILayout.ExpandWidth(true)))
        {
            StlExporter.Export(true, volumeObject, isDoubleSided);
        }
        if (GUILayout.Button("ASCII (4.0x)", GUILayout.ExpandWidth(true)))
        {
            StlExporter.Export(false, volumeObject, isDoubleSided);
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