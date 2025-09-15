using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

#if UNITY_EDITOR
using UnityEditor;
#endif

using UnityVolumeRendering;

[ExecuteAlways]
public class StlErrorPainterGPU : MonoBehaviour
{
    [Header("Scene Roots")]
    public Transform dicomRoot;
    public Transform stlRoot;

    [Header("Units")]
    public float mmPerUnityUnit = 1f;

    [Header("Probe settings")]
    public float surfaceEpsMm = 0.1f;
    public float maxProbeMm = 15f;
    public bool tryBothDirections = true;
    [Tooltip("Density threshold (0..1) used when detecting the DICOM surface in the volume texture")]
    [Range(0f, 1f)]
    public float densityThreshold = 0.10f;
    
    [Header("Multi-directional sampling")]
    public bool enableMultiDirectional = true;
    public float[] rayAngleOffsets = new float[] {0f, 10f, 20f, 30f, 45f, 60f, 75f, 90f};
    public bool useShortestDistance = true;
    
    [Header("Surface-specific optimization")]
    public bool enableSurfaceSpecificRays = true;
    public float verticalThreshold = 0.75f;
    public float horizontalThreshold = 0.25f;
    public float[] verticalAngles = new float[] {0f, 15f, 30f, 45f, 60f, 75f, 90f, 105f, 120f, 135f, -15f, -30f, -45f, -60f, -75f, -90f};
    public float[] horizontalAngles = new float[] {0f, 10f, 20f, 30f, 40f, 50f, 60f, 70f, 80f, 90f};
    public float[] obliqueAngles = new float[] {0f, 12f, 25f, 37f, 50f, 62f, 75f, 87f, -12f, -25f, -37f, -50f, -62f, -75f};
    
    [Header("Aggressive sampling")]
    public bool enableAggressiveSampling = true;
    public int aggressiveRayCount = 16;
    public float aggressiveMaxAngle = 150f;
    public int radialDirectionCount = 12;
    
    [Header("Advanced optimization")]
    public bool enableErrorWeighting = true;
    public bool useDistanceWeighting = true;
    public float maxWeightDistanceMm = 1.5f;
    public bool enableOutlierFiltering = true;
    public float outlierThreshold = 2.0f;
    public int minValidSamples = 4;

    [Header("Color mapping")]
    public float[] bandEdgesMm = new float[] {0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f};
    public Color[] bandColors = new Color[] {
        new Color(0.10f, 0.65f, 1f),
        new Color(0.25f, 0.80f, 0.9f),
        new Color(0.40f, 0.90f, 0.60f),
        new Color(0.85f, 0.95f, 0.20f),
        new Color(0.98f, 0.80f, 0.20f),
        new Color(0.98f, 0.55f, 0.20f),
        new Color(0.95f, 0.30f, 0.20f),
        new Color(0.85f, 0.15f, 0.15f),
        new Color(0.70f, 0.10f, 0.10f),
        new Color(0.55f, 0.05f, 0.05f)
    };
    
    [Header("Color range")]
    public float colorMinMm = 0f;
    public float colorMaxMm = 3f;
    public bool autoBandsFromRange = true;
    public bool reverseColorScale = false;

    [Header("Stats clipping")]
    [Tooltip("Exclude samples with error above this from per-mesh and global statistics. Colors still clamp to Color range.")]
    public bool clipStatsAboveMaxMm = true;
    [Tooltip("Maximum error (mm) to include in statistics when clipping is enabled.")]
    public float statsMaxMm = 3f;

    [Header("GPU Compute")]
    public ComputeShader errorPainterCompute;
    [Tooltip("Maximum vertices per GPU batch to avoid thread group limits")]
    public int maxVerticesPerBatch = 4000000; // ~62500 thread groups at 64 threads each
    
    [Header("Materials")]
    public bool assignVertexColorMaterial = true;

    // GPU buffers
    ComputeBuffer vertexPosBuffer;
    ComputeBuffer vertexNormalBuffer;
    ComputeBuffer vertexColorBuffer;
    ComputeBuffer errorResultBuffer;
    ComputeBuffer errorVectorBuffer;
    ComputeBuffer rayAngleBuffer;
    ComputeBuffer verticalAngleBuffer;
    ComputeBuffer horizontalAngleBuffer;
    ComputeBuffer obliqueAngleBuffer;
    ComputeBuffer bandEdgeBuffer;
    ComputeBuffer bandColorBuffer;
    // Removed: bestDirBuffer previously used for CPU validation of best ray directions

    // Volume data
    VolumeRenderedObject primaryVolume;
    Texture3D volumeTexture;
    readonly List<float> _allErrors = new List<float>(1024);
    Texture2D tfTexture;
    Vector2 visibilityWindow = new Vector2(0f, 1f);
    int dimX, dimY, dimZ;

    [ContextMenu("GPU Bake Colors")]
    public void BakeGPU()
    {
    RunCoroutineSmart(BakeGPUCoroutine());
    }

    IEnumerator BakeGPUCoroutine()
    {
    Debug.Log("[GPU Painter] Starting GPU bake...");
        if (!dicomRoot || !stlRoot)
        {
            Debug.LogError("[GPU Painter] Please set dicomRoot / stlRoot");
            yield break;
        }

        if (!errorPainterCompute)
        {
            // Try to auto-locate the compute shader in the project (Editor only)
#if UNITY_EDITOR
            var path = "Assets/Shaders/StlErrorPainterCompute.compute";
            errorPainterCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            if (!errorPainterCompute)
            {
                // Fallback: search by name
                string[] guids = AssetDatabase.FindAssets("t:ComputeShader StlErrorPainterCompute");
                if (guids != null && guids.Length > 0)
                {
                    var p = AssetDatabase.GUIDToAssetPath(guids[0]);
                    errorPainterCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(p);
                }
            }
#endif
            if (!errorPainterCompute)
            {
                Debug.LogError("[GPU Painter] Compute shader not assigned and auto-locate failed. Assign Assets/Shaders/StlErrorPainterCompute.compute.");
                yield break;
            }
        }

        // Find primary volume
        primaryVolume = dicomRoot.GetComponentInChildren<VolumeRenderedObject>(false);
        if (!primaryVolume)
        {
            Debug.LogError("[GPU Painter] No VolumeRenderedObject found under dicomRoot");
            yield break;
        }

    // Get volume texture
        volumeTexture = primaryVolume.dataset.GetDataTexture();
    // Get transfer function and visibility window for CPU parity
    tfTexture = null;
    try { tfTexture = primaryVolume.transferFunction != null ? primaryVolume.transferFunction.GetTexture() : null; }
    catch {}
    visibilityWindow = primaryVolume.GetVisibilityWindow();
    dimX = primaryVolume.dataset.dimX; dimY = primaryVolume.dataset.dimY; dimZ = primaryVolume.dataset.dimZ;
        if (!volumeTexture)
        {
            Debug.LogError("[GPU Painter] No volume texture found");
            yield break;
        }

        // Get STL meshes
        var meshFilters = stlRoot.GetComponentsInChildren<MeshFilter>(false);
        if (meshFilters.Length == 0)
        {
            Debug.LogError("[GPU Painter] No MeshFilter found under stlRoot");
            yield break;
        }

        Debug.Log($"[GPU Painter] Processing {meshFilters.Length} meshes with GPU acceleration");

        // Process each mesh
        foreach (var mf in meshFilters)
        {
            if (!mf || !mf.sharedMesh) continue;
            
            yield return StartCoroutine(ProcessMeshGPU(mf));
        }

        // Calculate and log statistics
        CalculateStatistics(meshFilters);

        Debug.Log("[GPU Painter] GPU baking completed!");
    }

    // --- Allow running in Editor or Play Mode ---
    void RunCoroutineSmart(IEnumerator co)
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) { StartEditorCoroutine(co); return; }
#endif
        StartCoroutine(co);
    }

#if UNITY_EDITOR
    IEnumerator _editorCo;
    void StartEditorCoroutine(IEnumerator co)
    {
        _editorCo = co;
        EditorApplication.update += EditorUpdate;
    }
    void EditorUpdate()
    {
        if (_editorCo == null) { EditorApplication.update -= EditorUpdate; return; }
        try { if (!_editorCo.MoveNext()) { _editorCo = null; EditorApplication.update -= EditorUpdate; } }
        catch { _editorCo = null; EditorApplication.update -= EditorUpdate; }
    }
#endif

    IEnumerator ProcessMeshGPU(MeshFilter meshFilter)
    {
        var mesh = meshFilter.sharedMesh;
        var vertices = mesh.vertices;
        var normals = mesh.normals;

        if (normals == null || normals.Length != vertices.Length)
        {
            mesh.RecalculateNormals();
            normals = mesh.normals;
        }

        int vertexCount = vertices.Length;

        // Transform vertices to world space
        var worldVertices = new Vector3[vertexCount];
        var worldNormals = new Vector3[vertexCount];
        
        for (int i = 0; i < vertexCount; i++)
        {
            worldVertices[i] = meshFilter.transform.TransformPoint(vertices[i]);
            worldNormals[i] = meshFilter.transform.TransformDirection(normals[i]).normalized;
        }

        // Check if we need to batch process due to size
        if (vertexCount > maxVerticesPerBatch)
        {
            yield return StartCoroutine(ProcessMeshGPUBatched(meshFilter, worldVertices, worldNormals));
        }
        else
        {
            yield return StartCoroutine(ProcessMeshGPUSingle(meshFilter, worldVertices, worldNormals, 0, vertexCount));
        }
    }

    IEnumerator ProcessMeshGPUBatched(MeshFilter meshFilter, Vector3[] worldVertices, Vector3[] worldNormals)
    {
        int totalVertices = worldVertices.Length;
        var allColors = new Color[totalVertices];
        var allErrors = new float[totalVertices];
        // Removed: per-vertex best ray direction aggregation (debug-only)

        int batchCount = Mathf.CeilToInt((float)totalVertices / maxVerticesPerBatch);
        Debug.Log($"[GPU Painter] Large mesh detected. Processing {totalVertices} vertices in {batchCount} batches of max {maxVerticesPerBatch} vertices each.");

        for (int batch = 0; batch < batchCount; batch++)
        {
            int startIdx = batch * maxVerticesPerBatch;
            int endIdx = Mathf.Min(startIdx + maxVerticesPerBatch, totalVertices);
            int batchSize = endIdx - startIdx;

            Debug.Log($"[GPU Painter] Processing batch {batch + 1}/{batchCount}: vertices {startIdx}-{endIdx - 1} ({batchSize} vertices)");

            // Process this batch and store results directly
            yield return StartCoroutine(ProcessMeshGPUSingleBatch(worldVertices, worldNormals, startIdx, batchSize, allColors, allErrors));

            // Allow frame to breathe between batches
            yield return new WaitForEndOfFrame();
        }

        // Apply final results to mesh
        ApplyResultsToMesh(meshFilter, allColors, allErrors);
    }

    IEnumerator ProcessMeshGPUSingleBatch(Vector3[] worldVertices, Vector3[] worldNormals, int startIdx, int batchSize, Color[] allColors, float[] allErrors)
    {
        // Create batch arrays
        var batchVertices = new Vector3[batchSize];
        var batchNormals = new Vector3[batchSize];
        
        Array.Copy(worldVertices, startIdx, batchVertices, 0, batchSize);
        Array.Copy(worldNormals, startIdx, batchNormals, 0, batchSize);

        // Create GPU buffers for this batch
        CreateBuffers(batchSize);

        // Upload data to GPU
        vertexPosBuffer.SetData(batchVertices);
        vertexNormalBuffer.SetData(batchNormals);
        
        // Upload sampling parameters
        rayAngleBuffer.SetData(rayAngleOffsets);
        verticalAngleBuffer.SetData(verticalAngles);
        horizontalAngleBuffer.SetData(horizontalAngles);
        obliqueAngleBuffer.SetData(obliqueAngles);
        
        // Upload color mapping data
        var edges = autoBandsFromRange ? GenerateAutoEdges() : bandEdgesMm;
        bandEdgeBuffer.SetData(edges);
        
        var colors4 = bandColors.Select(c => new Vector4(c.r, c.g, c.b, c.a)).ToArray();
        bandColorBuffer.SetData(colors4);

        // Validate bands to avoid GPU OOB reads
        int desiredBandCount = bandColors != null ? bandColors.Length : 0;
        int effectiveBandCount = Mathf.Min(desiredBandCount, edges != null ? (edges.Length + 1) : 0);
        if (!autoBandsFromRange && bandEdgesMm != null && desiredBandCount > 0 && (bandEdgesMm.Length < desiredBandCount - 1))
        {
            Debug.LogWarning($"[GPU Painter] bandEdgesMm has {bandEdgesMm.Length} edges but needs at least {desiredBandCount - 1}. Using effective bandCount={effectiveBandCount}.");
        }

        // Set compute shader parameters
        SetComputeParameters(batchSize, effectiveBandCount);

        // Dispatch compute shader with safety check
        int kernel = errorPainterCompute.FindKernel("CSMain");
        int groups = Mathf.CeilToInt(batchSize / 64.0f);
        if (groups <= 0) groups = 1;
        
        if (groups > 65535)
        {
            Debug.LogError($"[GPU Painter] Batch size {batchSize} still too large (would need {groups} thread groups, max is 65535). Reduce maxVerticesPerBatch.");
            ReleaseBuffers();
            yield return false;
        }

        errorPainterCompute.Dispatch(kernel, groups, 1, 1);

        // Wait for GPU completion
        yield return new WaitForEndOfFrame();

        // Read back results
        var colors = new Color[batchSize];
        var colorData = new Vector4[batchSize];
        var errors = new float[batchSize];
        
        vertexColorBuffer.GetData(colorData);
        errorResultBuffer.GetData(errors);
        
        for (int i = 0; i < batchSize; i++)
        {
            colors[i] = new Color(colorData[i].x, colorData[i].y, colorData[i].z, colorData[i].w);
        }

        // Copy results back to main arrays
        Array.Copy(colors, 0, allColors, startIdx, batchSize);
        Array.Copy(errors, 0, allErrors, startIdx, batchSize);

        // Cleanup buffers for this batch
        ReleaseBuffers();

        // Return success
        yield return true;
    }

    IEnumerator ProcessMeshGPUSingle(MeshFilter meshFilter, Vector3[] worldVertices, Vector3[] worldNormals, int startIdx, int count)
    {
        // Create GPU buffers
        CreateBuffers(count);

        // Upload data to GPU
        vertexPosBuffer.SetData(worldVertices);
        vertexNormalBuffer.SetData(worldNormals);
        
        // Upload sampling parameters
        rayAngleBuffer.SetData(rayAngleOffsets);
        verticalAngleBuffer.SetData(verticalAngles);
        horizontalAngleBuffer.SetData(horizontalAngles);
        obliqueAngleBuffer.SetData(obliqueAngles);
        
        // Upload color mapping data
        var edges = autoBandsFromRange ? GenerateAutoEdges() : bandEdgesMm;
        bandEdgeBuffer.SetData(edges);
        
        var colors4 = bandColors.Select(c => new Vector4(c.r, c.g, c.b, c.a)).ToArray();
        bandColorBuffer.SetData(colors4);

        // Validate bands to avoid GPU OOB reads
        int desiredBandCount = bandColors != null ? bandColors.Length : 0;
        int effectiveBandCount = Mathf.Min(desiredBandCount, edges != null ? (edges.Length + 1) : 0);
        if (!autoBandsFromRange && bandEdgesMm != null && desiredBandCount > 0 && (bandEdgesMm.Length < desiredBandCount - 1))
        {
            Debug.LogWarning($"[GPU Painter] bandEdgesMm has {bandEdgesMm.Length} edges but needs at least {desiredBandCount - 1}. Using effective bandCount={effectiveBandCount}.");
        }

        // Set compute shader parameters
        SetComputeParameters(count, effectiveBandCount);

        // Dispatch compute shader
        int kernel = errorPainterCompute.FindKernel("CSMain");
        int groups = Mathf.CeilToInt(count / 64.0f);
        if (groups <= 0) groups = 1;
        
        if (groups > 65535)
        {
            Debug.LogError($"[GPU Painter] Vertex count {count} too large (would need {groups} thread groups, max is 65535). Use batched processing.");
            ReleaseBuffers();
            yield break;
        }

        errorPainterCompute.Dispatch(kernel, groups, 1, 1);

        // Wait for GPU completion
        yield return new WaitForEndOfFrame();

        // Read back results
        var colors = new Color[count];
        var colorData = new Vector4[count];
        var errors = new float[count];
        vertexColorBuffer.GetData(colorData);
        errorResultBuffer.GetData(errors);
        
        for (int i = 0; i < count; i++)
        {
            colors[i] = new Color(colorData[i].x, colorData[i].y, colorData[i].z, colorData[i].w);
        }

        // Apply results to mesh
        ApplyResultsToMesh(meshFilter, colors, errors);

        // Cleanup buffers
        ReleaseBuffers();
    }

    void ApplyResultsToMesh(MeshFilter meshFilter, Color[] colors, float[] errors)
    {
        // Apply colors to mesh (use instance to avoid changing shared mesh across duplicates)
        var renderer = meshFilter.GetComponent<MeshRenderer>();
        Mesh instanceMesh = meshFilter.mesh; // creates instance if needed
        instanceMesh.colors = colors;
        
        // Apply vertex color material
        if (assignVertexColorMaterial && renderer)
        {
            var shader = Shader.Find("Unlit/VertexColor");
            if (shader)
            {
                // Ensure all submeshes have a material that shows vertex colors
                var mats = renderer.sharedMaterials;
                if (mats == null || mats.Length == 0)
                {
                    mats = new Material[instanceMesh.subMeshCount > 0 ? instanceMesh.subMeshCount : 1];
                }
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || mats[i].shader != shader)
                    {
                        var material = new Material(shader) { name = "GPU Vertex Color (STL error)" };
                        mats[i] = material;
                    }
                }
                renderer.sharedMaterials = mats;
            }
            else
            {
                Debug.LogWarning($"[GPU Painter] Shader 'Unlit/VertexColor' not found. Material assignment skipped.");
            }
        }
        else
        {
            Debug.LogWarning($"[GPU Painter] Renderer or material missing for {meshFilter.name}. Vertex color assignment skipped.");
        }

        // Calculate stats for this mesh
        int valid = 0; double sum = 0, sum2 = 0; int clipped = 0;
        for (int i = 0; i < errors.Length; i++)
        {
            float e = errors[i];
            if (e >= 0 && float.IsFinite(e))
            {
                if (clipStatsAboveMaxMm && e > statsMaxMm) { clipped++; continue; }
                valid++; sum += e; sum2 += (double)e * e; _allErrors.Add(e);
            }
        }
        if (valid > 0)
        {
            double mean = sum / valid;
            double rms = Math.Sqrt(sum2 / valid);
            if (clipStatsAboveMaxMm && clipped > 0)
                Debug.Log($"[GPU Painter] {meshFilter.name}: valid={valid}/{errors.Length}, mean={mean:F3}mm, rms={rms:F3}mm, clipped>{statsMaxMm:F1}mm: {clipped}");
            else
                Debug.Log($"[GPU Painter] {meshFilter.name}: valid={valid}/{errors.Length}, mean={mean:F3}mm, rms={rms:F3}mm");
        }
        else
        {
            Debug.LogWarning($"[GPU Painter] {meshFilter.name}: No valid samples were found. Check alignment with the volume, increase maxProbeMm (current {maxProbeMm}mm), or adjust densityThreshold (current {densityThreshold}).");
        }
    }

    // Removed legacy helper previously used during CPU validation

    void CreateBuffers(int vertexCount)
    {
        ReleaseBuffers();

        vertexPosBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        vertexNormalBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        vertexColorBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 4);
        errorResultBuffer = new ComputeBuffer(vertexCount, sizeof(float));
        errorVectorBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        
        rayAngleBuffer = new ComputeBuffer(rayAngleOffsets.Length, sizeof(float));
        verticalAngleBuffer = new ComputeBuffer(verticalAngles.Length, sizeof(float));
        horizontalAngleBuffer = new ComputeBuffer(horizontalAngles.Length, sizeof(float));
        obliqueAngleBuffer = new ComputeBuffer(obliqueAngles.Length, sizeof(float));
        
        var edgeCount = autoBandsFromRange ? bandColors.Length - 1 : bandEdgesMm.Length;
        bandEdgeBuffer = new ComputeBuffer(edgeCount, sizeof(float));
        bandColorBuffer = new ComputeBuffer(bandColors.Length, sizeof(float) * 4);
    }

    void SetComputeParameters(int vertexCount, int bandCountParam)
    {
        int kernel = errorPainterCompute.FindKernel("CSMain");

        // Set buffers
        errorPainterCompute.SetBuffer(kernel, "vertexPositions", vertexPosBuffer);
        errorPainterCompute.SetBuffer(kernel, "vertexNormals", vertexNormalBuffer);
        errorPainterCompute.SetBuffer(kernel, "vertexColors", vertexColorBuffer);
        errorPainterCompute.SetBuffer(kernel, "errorResults", errorResultBuffer);
        errorPainterCompute.SetBuffer(kernel, "errorVectors", errorVectorBuffer);
        errorPainterCompute.SetBuffer(kernel, "rayAngleOffsets", rayAngleBuffer);
        errorPainterCompute.SetBuffer(kernel, "verticalAngles", verticalAngleBuffer);
        errorPainterCompute.SetBuffer(kernel, "horizontalAngles", horizontalAngleBuffer);
        errorPainterCompute.SetBuffer(kernel, "obliqueAngles", obliqueAngleBuffer);
        errorPainterCompute.SetBuffer(kernel, "bandEdges", bandEdgeBuffer);
        errorPainterCompute.SetBuffer(kernel, "bandColors", bandColorBuffer);

        // Set volume texture
        errorPainterCompute.SetTexture(kernel, "volumeTexture", volumeTexture);
        if (tfTexture != null)
        {
            errorPainterCompute.SetTexture(kernel, "tfTexture", tfTexture);
            errorPainterCompute.SetInt("useTransferFunction", 1);
        }
        else
        {
            errorPainterCompute.SetInt("useTransferFunction", 0);
        }
        errorPainterCompute.SetVector("visibilityWindow", visibilityWindow);
        errorPainterCompute.SetInts("volumeDims", new int[]{dimX, dimY, dimZ});

        // Use volumeContainerObject transform (matches VolumeRaycaster)
        var container = primaryVolume.volumeContainerObject != null ? primaryVolume.volumeContainerObject.transform : primaryVolume.transform;
        errorPainterCompute.SetMatrix("worldToLocal", container.worldToLocalMatrix);
        errorPainterCompute.SetMatrix("localToWorld", container.localToWorldMatrix);

        // Probe parameters
        errorPainterCompute.SetFloat("mmPerUnityUnit", mmPerUnityUnit);
        errorPainterCompute.SetFloat("surfaceEpsMm", surfaceEpsMm);
        errorPainterCompute.SetFloat("maxProbeMm", maxProbeMm);
        errorPainterCompute.SetInt("tryBothDirections", tryBothDirections ? 1 : 0);
        errorPainterCompute.SetFloat("densityThreshold", densityThreshold);
        // Explicitly use interpolated sampling, not nearest-voxel parity
        errorPainterCompute.SetInt("useNearestVoxel", 0);

        // Sampling parameters
        errorPainterCompute.SetInt("rayAngleCount", rayAngleOffsets.Length);
        errorPainterCompute.SetInt("enableMultiDirectional", enableMultiDirectional ? 1 : 0);
        errorPainterCompute.SetInt("useShortestDistance", useShortestDistance ? 1 : 0);

        // Surface-specific parameters
        errorPainterCompute.SetInt("enableSurfaceSpecificRays", enableSurfaceSpecificRays ? 1 : 0);
        errorPainterCompute.SetInt("verticalAngleCount", verticalAngles.Length);
        errorPainterCompute.SetInt("horizontalAngleCount", horizontalAngles.Length);
        errorPainterCompute.SetInt("obliqueAngleCount", obliqueAngles.Length);
        errorPainterCompute.SetFloat("verticalThreshold", verticalThreshold);
        errorPainterCompute.SetFloat("horizontalThreshold", horizontalThreshold);

        // Aggressive sampling
        errorPainterCompute.SetInt("enableAggressiveSampling", enableAggressiveSampling ? 1 : 0);
        errorPainterCompute.SetInt("aggressiveRayCount", aggressiveRayCount);
        errorPainterCompute.SetFloat("aggressiveMaxAngle", aggressiveMaxAngle);
        errorPainterCompute.SetInt("radialDirectionCount", radialDirectionCount);

        // Color mapping
        errorPainterCompute.SetInt("bandCount", bandCountParam);
        errorPainterCompute.SetFloat("colorMinMm", colorMinMm);
        errorPainterCompute.SetFloat("colorMaxMm", colorMaxMm);
        errorPainterCompute.SetInt("reverseColorScale", reverseColorScale ? 1 : 0);

        // Advanced optimization
        errorPainterCompute.SetInt("enableErrorWeighting", enableErrorWeighting ? 1 : 0);
        errorPainterCompute.SetInt("useDistanceWeighting", useDistanceWeighting ? 1 : 0);
        errorPainterCompute.SetFloat("maxWeightDistanceMm", maxWeightDistanceMm);
        errorPainterCompute.SetInt("enableOutlierFiltering", enableOutlierFiltering ? 1 : 0);
        errorPainterCompute.SetFloat("outlierThreshold", outlierThreshold);
        errorPainterCompute.SetInt("minValidSamples", minValidSamples);

        // Vertex count
        errorPainterCompute.SetInt("vertexCount", vertexCount);
    }

    float[] GenerateAutoEdges()
    {
        float lo = Mathf.Min(colorMinMm, colorMaxMm);
        float hi = Mathf.Max(colorMinMm, colorMaxMm);
        int edgeCount = Mathf.Max(1, bandColors.Length - 1);
        
        var edges = new float[edgeCount];
        float step = (hi - lo) / edgeCount;
        
        for (int i = 0; i < edgeCount; i++)
        {
            edges[i] = lo + step * (i + 1);
        }
        
        return edges;
    }

    void CalculateStatistics(MeshFilter[] meshFilters)
    {
        int n = _allErrors.Count;
        if (n == 0)
        {
            Debug.LogWarning("[GPU Painter] No valid error samples captured. Global statistics are unavailable.");
            return;
        }

        _allErrors.Sort();
        double sum = 0, sum2 = 0;
        for (int i = 0; i < n; i++) { double v = _allErrors[i]; sum += v; sum2 += v * v; }
        double mean = sum / n;
        double rms = Math.Sqrt(sum2 / n);
        float p95 = _allErrors[(int)Mathf.Clamp(Mathf.RoundToInt(0.95f * (n - 1)), 0, n - 1)];
        float p90 = _allErrors[(int)Mathf.Clamp(Mathf.RoundToInt(0.90f * (n - 1)), 0, n - 1)];
        float max = _allErrors[n - 1];

        Debug.Log($"[GPU Painter] Global stats across {meshFilters.Length} meshes and {n} samples, clipping values > {statsMaxMm:F1}mm were excluded from stats.");

        // Final consolidated metric log
        float lmaxMm = ComputeLmaxMm(meshFilters);
        float per95 = p95;
        float per95Pct = lmaxMm > 1e-6f ? per95 / lmaxMm * 100f : 0f;
        float cov030 = CoverageBelow(_allErrors, 0.30f);
        float cov050 = CoverageBelow(_allErrors, 0.50f);
        float cov100 = CoverageBelow(_allErrors, 1.00f);
        Debug.Log($"[GPU Painter] PER95 = {per95:F3} mm ({per95Pct:F2}% of Lmax)  Mean = {mean:F2} mm  DEV@{0.30f:F2}mm = {cov030*100f:F1}%  DEV@{0.50f:F2}mm = {cov050*100f:F1}%  DEV@{1.00f:F2}mm = {cov100*100f:F1}%");
    }

    float CoverageBelow(List<float> sortedAsc, float thresholdMm)
    {
        if (sortedAsc == null || sortedAsc.Count == 0) return 0f;
        // assumes sorted ascending
        int n = sortedAsc.Count;
        int lo = 0, hi = n - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (sortedAsc[mid] <= thresholdMm) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans < 0 ? 0f : ((ans + 1) / (float)n);
    }

    float ComputeLmaxMm(MeshFilter[] meshFilters)
    {
        if (meshFilters == null || meshFilters.Length == 0) return 0f;
        bool hasBounds = false;
        Bounds combined = new Bounds(Vector3.zero, Vector3.zero);
        foreach (var mf in meshFilters)
        {
            if (!mf) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr)
            {
                if (!hasBounds) { combined = mr.bounds; hasBounds = true; }
                else combined.Encapsulate(mr.bounds);
            }
            else
            {
                var mesh = mf.sharedMesh;
                if (!mesh) continue;
                var local = mesh.bounds;
                var worldCenter = mf.transform.TransformPoint(local.center);
                var ext = local.extents;
                var axisX = mf.transform.TransformVector(ext.x, 0, 0);
                var axisY = mf.transform.TransformVector(0, ext.y, 0);
                var axisZ = mf.transform.TransformVector(0, 0, ext.z);
                var worldExt = new Vector3(
                    Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                    Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                    Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z)
                );
                var b = new Bounds(worldCenter, worldExt * 2f);
                if (!hasBounds) { combined = b; hasBounds = true; }
                else combined.Encapsulate(b);
            }
        }
        if (!hasBounds) return 0f;
        float diagUnity = combined.size.magnitude; // world units
        return diagUnity * mmPerUnityUnit;
    }

    void ReleaseBuffers()
    {
        vertexPosBuffer?.Release();
        vertexNormalBuffer?.Release();
        vertexColorBuffer?.Release();
        errorResultBuffer?.Release();
        errorVectorBuffer?.Release();
        rayAngleBuffer?.Release();
        verticalAngleBuffer?.Release();
        horizontalAngleBuffer?.Release();
        obliqueAngleBuffer?.Release();
        bandEdgeBuffer?.Release();
        bandColorBuffer?.Release();
    }

    void OnDestroy()
    {
        ReleaseBuffers();
    }

    void OnDisable()
    {
        ReleaseBuffers();
    }
}
