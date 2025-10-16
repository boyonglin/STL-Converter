using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityVolumeRendering;

/// <summary>
/// GPU-accelerated STL error painter that computes surface deviation between STL meshes and DICOM volume data.
/// Uses compute shaders for high-performance vertex-level error calculation and color mapping.
/// </summary>
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
    [Tooltip("Refine iso-surface intersection after initial hit using bracket + bisection to reduce wrong-layer picks")]
    public bool enableRefinedIntersection = true;
    
    [Header("Multi-directional sampling")]
    public bool enableMultiDirectional = true;
    public float[] rayAngleOffsets = {0f, 10f, 20f, 30f, 45f, 60f, 75f, 90f};
    public bool useShortestDistance = true;
    
    [Header("Surface-specific optimization")]
    public bool enableSurfaceSpecificRays = true;
    public float verticalThreshold = 0.75f;
    public float horizontalThreshold = 0.25f;
    public float[] verticalAngles = {0f, 15f, 30f, 45f, 60f, 75f, 90f, 105f, 120f, 135f, -15f, -30f, -45f, -60f, -75f, -90f};
    public float[] horizontalAngles = {0f, 10f, 20f, 30f, 40f, 50f, 60f, 70f, 80f, 90f};
    public float[] obliqueAngles = {0f, 12f, 25f, 37f, 50f, 62f, 75f, 87f, -12f, -25f, -37f, -50f, -62f, -75f};
    
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
    public float[] bandEdgesMm = {0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f};
    public Color[] bandColors = {
        new(0.10f, 0.65f, 1f),
        new(0.25f, 0.80f, 0.9f),
        new(0.40f, 0.90f, 0.60f),
        new(0.85f, 0.95f, 0.20f),
        new(0.98f, 0.80f, 0.20f),
        new(0.98f, 0.55f, 0.20f),
        new(0.95f, 0.30f, 0.20f),
        new(0.85f, 0.15f, 0.15f),
        new(0.70f, 0.10f, 0.10f),
        new(0.55f, 0.05f, 0.05f)
    };
    
    [Header("Color range")]
    public float colorMinMm;
    public float colorMaxMm = 0.3f; // Increased from 3f to allow higher error visualization
    public bool autoBandsFromRange = true;
    public bool reverseColorScale;

    [Header("Stats clipping")]
    [Tooltip("Exclude samples with error above this from per-mesh and global statistics. Colors still clamp to Color range.")]
    public bool clipStatsAboveMaxMm; // Disabled by default to include all samples
    [Tooltip("Maximum error (mm) to include in statistics when clipping is enabled.")]
    public float statsMaxMm = 20f; // Increased from 3f to match color range

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

    // Volume data
    VolumeRenderedObject primaryVolume;
    Texture3D volumeTexture;
    readonly List<float> allErrors = new(1024);
    Texture2D tfTexture;
    Vector2 visibilityWindow = new(0f, 1f);
    int dimX, dimY, dimZ;

    /// <summary>
    /// Initiates GPU-accelerated error calculation and color baking for all STL meshes.
    /// </summary>
    [ContextMenu("GPU Bake Colors")]
    public void BakeGPU()
    {
        StartCoroutine(BakeGPUCoroutine());
    }

    /// <summary>
    /// Main coroutine that handles the complete GPU baking process.
    /// </summary>
    IEnumerator BakeGPUCoroutine()
    {
        Debug.Log("[GPU Painter] Starting GPU bake...");
        
        // 清空前次結果，避免累加舊數據
        allErrors.Clear();
        
        if (!dicomRoot || !stlRoot)
        {
            Debug.LogError("[GPU Painter] Please set dicomRoot / stlRoot");
            yield break;
        }

        if (!errorPainterCompute)
        {
            Debug.LogError("[GPU Painter] Compute shader not assigned and auto-locate failed. Assign Shaders/StlErrorPainterCompute.compute.");
            yield break;
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
        try 
        { 
            tfTexture = primaryVolume.transferFunction != null ? primaryVolume.transferFunction.GetTexture() : null; 
        }
        catch
        {
            // ignored
        }
        
        visibilityWindow = primaryVolume.GetVisibilityWindow();
        dimX = primaryVolume.dataset.dimX; 
        dimY = primaryVolume.dataset.dimY; 
        dimZ = primaryVolume.dataset.dimZ;
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

    // Auto-locate dicomRoot, stlRoot, and compute shader at runtime
    private void Awake()
    {
        LocateSceneRoots();
        LocateComputeShader();
    }

    void LocateSceneRoots()
    {
        if (dicomRoot == null)
        {
            var dicomGo = GameObject.Find("dicom");
            if (dicomGo != null)
            {
                dicomRoot = dicomGo.transform;
                Debug.Log("[GPU Painter] Auto-located dicomRoot: " + dicomRoot.name);
            }
        }
    
        if (stlRoot == null)
        {
            var stlGo = GameObject.Find("stl");
            if (stlGo != null)
            {
                stlRoot = stlGo.transform;
                Debug.Log("[GPU Painter] Auto-located stlRoot: " + stlRoot.name);
            }
        }
    }

    void LocateComputeShader()
    {
        const string path = "Shaders/StlErrorPainterCompute";
        errorPainterCompute = Resources.Load<ComputeShader>(path);
        if (errorPainterCompute != null)
        {
            Debug.Log("[GPU Painter] Auto-located compute shader from Resources/" + path);
        }
    }

    // Runtime coroutine execution
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
            yield return StartCoroutine(ProcessMeshGPUSingle(meshFilter, worldVertices, worldNormals, vertexCount));
        }
    }

    IEnumerator ProcessMeshGPUBatched(MeshFilter meshFilter, Vector3[] worldVertices, Vector3[] worldNormals)
    {
        int totalVertices = worldVertices.Length;
        var meshColors = new Color[totalVertices];
        var meshErrors = new float[totalVertices];

        int batchCount = Mathf.CeilToInt((float)totalVertices / maxVerticesPerBatch);
        Debug.Log($"[GPU Painter] Large mesh detected. Processing {totalVertices} vertices in {batchCount} batches of max {maxVerticesPerBatch} vertices each.");

        for (int batch = 0; batch < batchCount; batch++)
        {
            int startIdx = batch * maxVerticesPerBatch;
            int endIdx = Mathf.Min(startIdx + maxVerticesPerBatch, totalVertices);
            int batchSize = endIdx - startIdx;

            Debug.Log($"[GPU Painter] Processing batch {batch + 1}/{batchCount}: vertices {startIdx}-{endIdx - 1} ({batchSize} vertices)");

            // Process this batch and store results directly
            yield return StartCoroutine(ProcessMeshGPUSingleBatch(worldVertices, worldNormals, startIdx, batchSize, meshColors, meshErrors));

            // Allow frame to breathe between batches
            yield return new WaitForEndOfFrame();
        }

        // Apply final results to mesh
        ApplyResultsToMesh(meshFilter, meshColors, meshErrors);
    }

    IEnumerator ProcessMeshGPUSingleBatch(Vector3[] worldVertices, Vector3[] worldNormals, int startIdx, int batchSize, Color[] meshColors, float[] meshErrors)
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
        int desiredBandCount = bandColors?.Length ?? 0;
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
        Array.Copy(colors, 0, meshColors, startIdx, batchSize);
        Array.Copy(errors, 0, meshErrors, startIdx, batchSize);

        // Cleanup buffers for this batch
        ReleaseBuffers();

        // Return success
        yield return true;
    }

    IEnumerator ProcessMeshGPUSingle(MeshFilter meshFilter, Vector3[] worldVertices, Vector3[] worldNormals, int count)
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
        int desiredBandCount = bandColors?.Length ?? 0;
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
        var meshRenderer = meshFilter.GetComponent<MeshRenderer>();
        var instanceMesh = meshFilter.mesh; // creates instance if needed
        instanceMesh.colors = colors;
        
        // Apply vertex color material
        if (assignVertexColorMaterial && meshRenderer)
        {
            var shader = Shader.Find("Unlit/VertexColor");
            if (shader)
            {
                // Ensure all submeshes have a material that shows vertex colors
                var mats = meshRenderer.sharedMaterials;
                if (mats == null || mats.Length == 0)
                {
                    mats = new Material[instanceMesh.subMeshCount > 0 ? instanceMesh.subMeshCount : 1];
                }
                for (int i = 0; i < mats.Length; i++)
                {
                    if (!mats[i] || mats[i].shader != shader)
                    {
                        var material = new Material(shader) { name = "GPU Vertex Color (STL error)" };
                        mats[i] = material;
                    }
                }
                meshRenderer.sharedMaterials = mats;
            }
            else
            {
                Debug.LogWarning("[GPU Painter] Shader 'Unlit/VertexColor' not found. Material assignment skipped.");
            }
        }

        // Calculate stats for this mesh
        int valid = 0; double sum = 0; int clipped = 0;
        foreach (var e in errors)
        {
            if (e >= 0 && float.IsFinite(e))
            {
                if (clipStatsAboveMaxMm && e > statsMaxMm) { clipped++; continue; }
                allErrors.Add(e);
                valid++; sum += e;
            }
        }
        if (valid > 0)
        {
            double mean = sum / valid;
            if (clipStatsAboveMaxMm && clipped > 0)
                Debug.Log($"[GPU Painter] {meshFilter.name}: valid={valid}/{errors.Length}, mean={mean:F3}mm, clipped>{statsMaxMm:F1}mm: {clipped}");
            else
                Debug.Log($"[GPU Painter] {meshFilter.name}: valid={valid}/{errors.Length}, mean={mean:F3}mm");
        }
        else
        {
            Debug.LogWarning($"[GPU Painter] {meshFilter.name}: No valid samples were found. Check alignment with the volume, increase maxProbeMm (current {maxProbeMm}mm), or adjust densityThreshold (current {densityThreshold}).");
        }
    }

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
        errorPainterCompute.SetBuffer(kernel, Shader.PropertyToID("vertexPositions"), vertexPosBuffer);
        errorPainterCompute.SetBuffer(kernel, Shader.PropertyToID("vertexNormals"), vertexNormalBuffer);
        errorPainterCompute.SetBuffer(kernel, Shader.PropertyToID("vertexColors"), vertexColorBuffer);
        errorPainterCompute.SetBuffer(kernel, Shader.PropertyToID("errorResults"), errorResultBuffer);
        errorPainterCompute.SetBuffer(kernel, Shader.PropertyToID("errorVectors"), errorVectorBuffer);
        errorPainterCompute.SetBuffer(kernel, Shader.PropertyToID("rayAngleOffsets"), rayAngleBuffer);
        errorPainterCompute.SetBuffer(kernel, Shader.PropertyToID("verticalAngles"), verticalAngleBuffer);
        errorPainterCompute.SetBuffer(kernel, Shader.PropertyToID("horizontalAngles"), horizontalAngleBuffer);
        errorPainterCompute.SetBuffer(kernel, Shader.PropertyToID("obliqueAngles"), obliqueAngleBuffer);
        errorPainterCompute.SetBuffer(kernel, Shader.PropertyToID("bandEdges"), bandEdgeBuffer);
        errorPainterCompute.SetBuffer(kernel, Shader.PropertyToID("bandColors"), bandColorBuffer);

        // Set volume texture
        errorPainterCompute.SetTexture(kernel, Shader.PropertyToID("volumeTexture"), volumeTexture);
        if (tfTexture != null)
        {
            errorPainterCompute.SetTexture(kernel, Shader.PropertyToID("tfTexture"), tfTexture);
            errorPainterCompute.SetInt(Shader.PropertyToID("useTransferFunction"), 1);
        }
        else
        {
            errorPainterCompute.SetInt(Shader.PropertyToID("useTransferFunction"), 0);
        }
        errorPainterCompute.SetVector(Shader.PropertyToID("visibilityWindow"), visibilityWindow);
        errorPainterCompute.SetInts(Shader.PropertyToID("volumeDims"), dimX, dimY, dimZ);

        // Use volumeContainerObject transform (matches VolumeRaycaster)
        var container = primaryVolume.volumeContainerObject ? primaryVolume.volumeContainerObject.transform : primaryVolume.transform;
        errorPainterCompute.SetMatrix(Shader.PropertyToID("worldToLocal"), container.worldToLocalMatrix);
        errorPainterCompute.SetMatrix(Shader.PropertyToID("localToWorld"), container.localToWorldMatrix);

        // Probe parameters
        errorPainterCompute.SetFloat(Shader.PropertyToID("mmPerUnityUnit"), mmPerUnityUnit);
        errorPainterCompute.SetFloat(Shader.PropertyToID("surfaceEpsMm"), surfaceEpsMm);
        errorPainterCompute.SetFloat(Shader.PropertyToID("maxProbeMm"), maxProbeMm);
        errorPainterCompute.SetInt(Shader.PropertyToID("tryBothDirections"), tryBothDirections ? 1 : 0);
        errorPainterCompute.SetFloat(Shader.PropertyToID("densityThreshold"), densityThreshold);
        // Explicitly use interpolated sampling, not nearest-voxel parity
        errorPainterCompute.SetInt(Shader.PropertyToID("useNearestVoxel"), 0);
        errorPainterCompute.SetInt(Shader.PropertyToID("enableRefine"), enableRefinedIntersection ? 1 : 0);

        // Sampling parameters
        errorPainterCompute.SetInt(Shader.PropertyToID("rayAngleCount"), rayAngleOffsets.Length);
        errorPainterCompute.SetInt(Shader.PropertyToID("enableMultiDirectional"), enableMultiDirectional ? 1 : 0);
        errorPainterCompute.SetInt(Shader.PropertyToID("useShortestDistance"), useShortestDistance ? 1 : 0);

        // Surface-specific parameters
        errorPainterCompute.SetInt(Shader.PropertyToID("enableSurfaceSpecificRays"), enableSurfaceSpecificRays ? 1 : 0);
        errorPainterCompute.SetInt(Shader.PropertyToID("verticalAngleCount"), verticalAngles.Length);
        errorPainterCompute.SetInt(Shader.PropertyToID("horizontalAngleCount"), horizontalAngles.Length);
        errorPainterCompute.SetInt(Shader.PropertyToID("obliqueAngleCount"), obliqueAngles.Length);
        errorPainterCompute.SetFloat(Shader.PropertyToID("verticalThreshold"), verticalThreshold);
        errorPainterCompute.SetFloat(Shader.PropertyToID("horizontalThreshold"), horizontalThreshold);

        // Aggressive sampling
        errorPainterCompute.SetInt(Shader.PropertyToID("enableAggressiveSampling"), enableAggressiveSampling ? 1 : 0);
        errorPainterCompute.SetInt(Shader.PropertyToID("aggressiveRayCount"), aggressiveRayCount);
        errorPainterCompute.SetFloat(Shader.PropertyToID("aggressiveMaxAngle"), aggressiveMaxAngle);
        errorPainterCompute.SetInt(Shader.PropertyToID("radialDirectionCount"), radialDirectionCount);

        // Color mapping
        errorPainterCompute.SetInt(Shader.PropertyToID("bandCount"), bandCountParam);
        errorPainterCompute.SetFloat(Shader.PropertyToID("colorMinMm"), colorMinMm);
        errorPainterCompute.SetFloat(Shader.PropertyToID("colorMaxMm"), colorMaxMm);
        errorPainterCompute.SetInt(Shader.PropertyToID("reverseColorScale"), reverseColorScale ? 1 : 0);

        // Advanced optimization
        errorPainterCompute.SetInt(Shader.PropertyToID("enableErrorWeighting"), enableErrorWeighting ? 1 : 0);
        errorPainterCompute.SetInt(Shader.PropertyToID("useDistanceWeighting"), useDistanceWeighting ? 1 : 0);
        errorPainterCompute.SetFloat(Shader.PropertyToID("maxWeightDistanceMm"), maxWeightDistanceMm);
        errorPainterCompute.SetInt(Shader.PropertyToID("enableOutlierFiltering"), enableOutlierFiltering ? 1 : 0);
        errorPainterCompute.SetFloat(Shader.PropertyToID("outlierThreshold"), outlierThreshold);
        errorPainterCompute.SetInt(Shader.PropertyToID("minValidSamples"), minValidSamples);

        // Vertex count
        errorPainterCompute.SetInt(Shader.PropertyToID("vertexCount"), vertexCount);
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
        var stats = ComputeGlobalStats(allErrors, meshFilters);
        if (stats.sampleCount == 0)
        {
            Debug.LogWarning("[GPU Painter] No valid error samples captured. Global statistics are unavailable.");
            return;
        }

        Debug.Log($"[GPU Painter] Global statistics across {meshFilters.Length} meshes based on ALL {stats.sampleCount} unclipped error samples (including values > {statsMaxMm:F1}mm visualization threshold).");
        Debug.Log($"[GPU Painter] PER95 = {stats.p95:F3} mm ({stats.per95Pct:F2}% of Lmax)  Mean = {stats.mean:F2} mm  Max = {stats.max:F2} mm  DEV@{0.05f:F2}mm = {stats.cov005*100f:F1}%  DEV@{0.30f:F2}mm = {stats.cov030*100f:F1}%  DEV@{1.00f:F2}mm = {stats.cov100*100f:F1}%");
    }

    struct GlobalStats
    {
        public int sampleCount;
        public float mean;
        public float p95;
        public float max;
        public float lmaxMm;
        public float per95Pct;
        public float cov005;
        public float cov030;
        public float cov100;
    }

    GlobalStats ComputeGlobalStats(List<float> errors, MeshFilter[] meshFilters)
    {
        var stats = new GlobalStats();
        if (errors == null || errors.Count == 0)
        {
            stats.sampleCount = 0;
            return stats;
        }

        // Work on a sorted copy to avoid mutating original order if needed elsewhere
        var sorted = new List<float>(errors);
        sorted.Sort();
        int n = sorted.Count;
        stats.sampleCount = n;

        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double v = sorted[i];
            sum += v;
        }
        stats.mean = (float)(sum / n);
        stats.p95 = sorted[Mathf.Clamp(Mathf.RoundToInt(0.95f * (n - 1)), 0, n - 1)];
        stats.max = sorted[n - 1];

        stats.lmaxMm = ComputeLmaxMm(meshFilters);
        stats.per95Pct = stats.lmaxMm > 1e-6f ? (stats.p95 / stats.lmaxMm * 100f) : 0f;
        stats.cov005 = CoverageBelow(sorted, 0.05f);
        stats.cov030 = CoverageBelow(sorted, 0.30f);
        stats.cov100 = CoverageBelow(sorted, 1.00f);
        return stats;
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
        // 使用最長邊而非對角線作為 Lmax
        float lmaxUnity = Mathf.Max(combined.size.x, combined.size.y, combined.size.z);
        return lmaxUnity * mmPerUnityUnit;
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
