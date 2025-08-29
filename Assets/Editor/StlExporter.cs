using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityVolumeRendering;

public static class StlExporter
{
    // ReSharper disable Unity.PerformanceAnalysis
    /// <summary>
    /// Exports a VolumeRenderedObject to an STL file.
    /// </summary>
    /// <param name="isBinary">True to export in binary format, false for ASCII.</param>
    /// <param name="volumeObject">The VolumeRenderedObject to export.</param>
    /// <param name="doubleSided">True to create double-sided geometry.</param>
    /// <param name="upsamplingFactor">The factor by which to upsample the mesh.</param>
    public static void Export(bool isBinary, VolumeRenderedObject volumeObject, bool doubleSided = false, float upsamplingFactor = 1.0f)
    {
        if (!volumeObject)
        {
            EditorUtility.DisplayDialog("Selection Error", "Please select a GameObject with a VolumeRenderedObject component.", "OK");
            return;
        }

        // Get voxel data and dimensions
        var dataset = volumeObject.dataset;
        var voxels = dataset.data;
        int width = dataset.dimX, height = dataset.dimY, depth = dataset.dimZ;

        // Basic validation to catch common issues that lead to empty meshes
        if (voxels == null || voxels.Length != width * height * depth)
        {
            Debug.LogError($"Voxel data invalid: expected {width * height * depth} samples but got {voxels?.Length ?? 0}.");
            EditorUtility.DisplayDialog("Data Error", "Voxel data is missing or has incorrect dimensions. Cannot generate mesh.", "OK");
            return;
        }

        var dataMin = dataset.GetMinDataValue();
        var dataMax = dataset.GetMaxDataValue();
        var visibilityWindow = volumeObject.GetVisibilityWindow();
        var isoLevel = dataMin + visibilityWindow.x * (dataMax - dataMin);
        if (dataMax <= dataMin)
        {
            Debug.LogError($"Dataset min/max invalid: min={dataMin}, max={dataMax}");
            EditorUtility.DisplayDialog("Data Error", "Dataset value range is invalid. Cannot generate mesh.", "OK");
            return;
        }
        if (isoLevel < dataMin || isoLevel > dataMax)
        {
            Debug.LogWarning($"Computed isoLevel {isoLevel} is outside data range [{dataMin},{dataMax}]. This may produce an empty mesh.");
        }

        // Generate isosurface mesh
        var wasCancelled = false;
        var mesh = IsoSurfaceGenerator.BuildMesh(voxels, width, height, depth, isoLevel, true, doubleSided,
            onProgress: (progress) =>
            {
                bool cancelled;
                if (progress < 0f)
                {
                    // Upsampling stage (negative progress indicates upsampling)
                    var p = Mathf.Clamp01(-progress);
                    cancelled = EditorUtility.DisplayCancelableProgressBar("Export STL - Upsampling", "Upsampling voxels…", p);
                }
                else
                {
                    // Mesh generation stage
                    var p = Mathf.Clamp01(progress);
                    cancelled = EditorUtility.DisplayCancelableProgressBar("Export STL - Mesh", "Generating mesh…", p);
                }
                if (cancelled) wasCancelled = true;
                return cancelled;
            },
            upsamplingFactor: upsamplingFactor);
        
        EditorUtility.ClearProgressBar();
        
        if (wasCancelled)
        {
            return;
        }
        
        // Robust empty-mesh check: handle null, zero vertices, or no triangles
        if (mesh == null || mesh.vertexCount == 0 || mesh.triangles == null || mesh.triangles.Length == 0)
        {
            EditorUtility.DisplayDialog("Generation Failed", "The generated mesh is empty or invalid. Please adjust the isoLevel and try again.", "OK");
            return;
        }

        // Apply scale and pivot correction
        var transformScale = volumeObject.transform.lossyScale;
        var transformRotation = volumeObject.transform.rotation * Quaternion.Euler(-90f, 0, 0);
        MeshUtil.ApplyDatasetScaleAndCenter(mesh, dataset, transformScale, transformRotation);

        // Prompt user for the file path
        var typeName = isBinary ? "Binary STL" : "ASCII STL";
        var suffix = doubleSided ? "_double" : "";
        var baseName = volumeObject.name.Replace(".dcm", "");
        var fileName = $"{baseName}{(isBinary ? "_binary" : "_ascii")}{suffix}.stl";
        var path = EditorUtility.SaveFilePanel($"Export {typeName}", Application.dataPath, fileName, "stl");
        if (string.IsNullOrEmpty(path)) return;

        // Export
        if (isBinary)
            WriteBinaryStlOptimized(mesh, path);
        else
            WriteAsciiStl(mesh, path);

        EditorUtility.DisplayDialog("Export Complete", $"{typeName} saved to:\n{path}", "OK");
    }

    /// <summary>
    /// Optimized binary STL writer with improved performance
    /// </summary>
    private static void WriteBinaryStlOptimized(Mesh mesh, string filePath)
    {
        var triangles = mesh.triangles;
        var vertices = mesh.vertices;
        var normals = mesh.normals;
        var triangleCount = triangles.Length / 3;

        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);
        using var writer = new BinaryWriter(stream);

        // Write 80-byte header
        var header = new byte[80];
        Encoding.ASCII.GetBytes("unity_volumerendering").CopyTo(header, 0);
        writer.Write(header);

        // Write triangle count
        writer.Write((uint)triangleCount);

        // Pre-allocate arrays to avoid repeated allocations
        var normalBuffer = new byte[12];
        var vertexBuffer = new byte[36]; // 3 vertices * 3 floats * 4 bytes
        
        // Use unsafe context for fastest float-to-byte conversion
        unsafe
        {
            fixed (byte* normalPtr = normalBuffer)
            fixed (byte* vertexPtr = vertexBuffer)
            {
                var normalFloatPtr = (float*)normalPtr;
                var vertexFloatPtr = (float*)vertexPtr;

                // Process triangles in batches to reduce method call overhead
                for (var i = 0; i < triangles.Length; i += 3)
                {
                    var a = triangles[i];
                    var b = triangles[i + 1]; 
                    var c = triangles[i + 2];

                    // Calculate or use existing normal
                    Vector3 normal;
                    if (normals.Length == vertices.Length)
                    {
                        // Use vertex normal (average of shared vertex normals)
                        normal = normals[a];
                    }
                    else
                    {
                        // Calculate face normal
                        var v1 = vertices[b] - vertices[a];
                        var v2 = vertices[c] - vertices[a];
                        normal = Vector3.Cross(v1, v2).normalized;
                    }

                    // Write normal directly to buffer
                    normalFloatPtr[0] = normal.x;
                    normalFloatPtr[1] = normal.y;
                    normalFloatPtr[2] = normal.z;

                    // Write vertices directly to buffer
                    var va = vertices[a];
                    var vb = vertices[b];
                    var vc = vertices[c];

                    vertexFloatPtr[0] = va.x; vertexFloatPtr[1] = va.y; vertexFloatPtr[2] = va.z;
                    vertexFloatPtr[3] = vb.x; vertexFloatPtr[4] = vb.y; vertexFloatPtr[5] = vb.z;
                    vertexFloatPtr[6] = vc.x; vertexFloatPtr[7] = vc.y; vertexFloatPtr[8] = vc.z;

                    // Write buffers to stream
                    writer.Write(normalBuffer);
                    writer.Write(vertexBuffer);
                    writer.Write((ushort)0); // attribute byte count
                }
            }
        }
    }

    /// <summary>
    /// Fallback safe binary STL writer for platforms without unsafe code support
    /// </summary>
    private static void WriteBinaryStlSafe(Mesh mesh, string filePath)
    {
        var triangles = mesh.triangles;
        var vertices = mesh.vertices;
        var normals = mesh.normals;

        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);
        using var writer = new BinaryWriter(stream);

        // Write 80-byte header
        var header = new byte[80];
        Encoding.ASCII.GetBytes("unity_volumerendering").CopyTo(header, 0);
        writer.Write(header);

        // Write triangle count
        writer.Write((uint)(triangles.Length / 3));

        // Pre-convert vertices to reduce repeated array access
        var vertexData = new Vector3[vertices.Length];
        vertices.CopyTo(vertexData, 0);

        // Write triangles
        for (var i = 0; i < triangles.Length; i += 3)
        {
            var a = triangles[i];
            var b = triangles[i + 1];
            var c = triangles[i + 2];

            // Calculate or use existing normal
            Vector3 normal;
            if (normals.Length == vertices.Length)
            {
                normal = normals[a];
            }
            else
            {
                var v1 = vertexData[b] - vertexData[a];
                var v2 = vertexData[c] - vertexData[a];
                normal = Vector3.Cross(v1, v2).normalized;
            }

            WriteVector3(writer, normal);
            WriteVector3(writer, vertexData[a]);
            WriteVector3(writer, vertexData[b]);
            WriteVector3(writer, vertexData[c]);

            writer.Write((ushort)0); // attribute byte count
        }
    }

    /// <summary>
    /// Optimized Vector3 writing using BitConverter for better performance
    /// </summary>
    private static void WriteVector3(BinaryWriter writer, Vector3 vector)
    {
        writer.Write(vector.x);
        writer.Write(vector.y);
        writer.Write(vector.z);
    }
    
    /// <summary>
    /// Optimized ASCII STL writer with StringBuilder pre-sizing
    /// </summary>
    private static void WriteAsciiStl(Mesh mesh, string filePath)
    {
        var triangles = mesh.triangles;
        var vertices = mesh.vertices;
        var normals = mesh.normals;
        var triangleCount = triangles.Length / 3;

        // Pre-size StringBuilder to reduce memory allocations
        // Estimate: ~200 characters per triangle on average
        var sb = new StringBuilder(triangleCount * 200 + 100);
        
        sb.AppendLine("solid unity_volumerendering");

        for (var i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            var normal = normals.Length == vertices.Length
                ? normals[a]
                : Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).normalized;

            sb.AppendLine($"facet normal {normal.x:F6} {normal.y:F6} {normal.z:F6}");
            sb.AppendLine("outer loop");
            
            var va = vertices[a];
            var vb = vertices[b]; 
            var vc = vertices[c];
            
            sb.AppendLine($"vertex {va.x:F6} {va.y:F6} {va.z:F6}");
            sb.AppendLine($"vertex {vb.x:F6} {vb.y:F6} {vb.z:F6}");
            sb.AppendLine($"vertex {vc.x:F6} {vc.y:F6} {vc.z:F6}");
            sb.AppendLine("endloop");
            sb.AppendLine("endfacet");
        }

        sb.AppendLine("endsolid unity_volumerendering");
        
        // Write to file with optimal buffer size
        using var writer = new StreamWriter(filePath, false, Encoding.ASCII, bufferSize: 65536);
        writer.Write(sb.ToString());
    }
}
