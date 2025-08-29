using System.Collections.Generic;
using UnityEngine;
using MarchingCubesProject;

public static class IsoSurfaceGenerator
{
    public static Mesh BuildMesh(
        float[] voxels, int width, int height, int depth,
        float isoLevel = 0.5f, bool smooth = true, bool doubleSided = false,
        System.Func<float, bool> reportProgressAndCheckCancel = null,
        float upsamplingFactor = 1.0f)
    {
        int origWidth = width, origHeight = height, origDepth = depth;
        var upsample = upsamplingFactor > 1.01f;
        if (upsample)
        {
            // Pass the progress callback into the upsample routine. If it returns true the operation was cancelled.
            var cancelled = UpsampleVoxelsTrilinear(voxels, width, height, depth, upsamplingFactor,
                out voxels, out width, out height, out depth, reportProgressAndCheckCancel);
            if (cancelled)
            {
                return new Mesh();
            }
        }

        var algo = new MarchingCubes(isoLevel);
        var vertices = new List<Vector3>(doubleSided ? 131072 : 65536);
        var triangles = new List<int>(doubleSided ? 262144 : 131072);
        var cube = new float[8];
        var slice = width * height;

        for (var z = 0; z < depth - 1; z++)
        {
            // Report progress and check for cancellation at the start of each z slice
            if (reportProgressAndCheckCancel != null)
            {
                var progress = (float)z / (depth - 1);
                if (reportProgressAndCheckCancel(progress))
                {
                    // Operation was cancelled, return null or empty mesh
                    return new Mesh();
                }
            }

            for (var y = 0; y < height - 1; y++)
            for (var x = 0; x < width - 1; x++)
            {
                var p = x + y * width + z * slice;
                // Voxel values at the eight corners of the cube
                cube[0] = voxels[p];
                cube[1] = voxels[p + 1];
                cube[2] = voxels[p + 1 + width];
                cube[3] = voxels[p + width];
                cube[4] = voxels[p + slice];
                cube[5] = voxels[p + 1 + slice];
                cube[6] = voxels[p + 1 + width + slice];
                cube[7] = voxels[p + width + slice];

                if (doubleSided)
                {
                    var tempVertices = new List<Vector3>();
                    var tempTriangles = new List<int>();
                    algo.PolygoniseCube(x, y, z, cube, tempVertices, tempTriangles);
                    for (var i = 0; i < tempTriangles.Count; i += 3)
                    {
                        var v1 = tempVertices[tempTriangles[i]];
                        var v2 = tempVertices[tempTriangles[i + 1]];
                        var v3 = tempVertices[tempTriangles[i + 2]];
                        AddDoubleSidedTriangle(vertices, triangles, v1, v2, v3);
                    }
                }
                else
                {
                    algo.PolygoniseCube(x, y, z, cube, vertices, triangles);
                }
            }
        }

        // Report 100% completion
        if (reportProgressAndCheckCancel != null)
        {
            reportProgressAndCheckCancel(1.0f);
        }

        var mesh = new Mesh
        {
            indexFormat = vertices.Count > 65_534
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16
        };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        if (smooth) mesh.RecalculateNormals();

        // Normalize mesh size if upsampled
        if (upsample)
        {
            var scaleX = (float)(origWidth - 1) / (width - 1);
            var scaleY = (float)(origHeight - 1) / (height - 1);
            var scaleZ = (float)(origDepth - 1) / (depth - 1);
            var verts = mesh.vertices;
            for (var i = 0; i < verts.Length; i++)
            {
                verts[i] = new Vector3(verts[i].x * scaleX, verts[i].y * scaleY, verts[i].z * scaleZ);
            }
            mesh.vertices = verts;
        }

        return mesh;
    }
    
    /// <summary>
    /// Creates a double-sided mesh directly during generation (more efficient)
    /// </summary>
    private static void AddDoubleSidedTriangle(
        List<Vector3> vertices, 
        List<int> triangles,
        Vector3 v1, Vector3 v2, Vector3 v3)
    {
        var startIndex = vertices.Count;
        
        // Add vertices twice (for front and back faces)
        vertices.Add(v1);
        vertices.Add(v2);
        vertices.Add(v3);
        vertices.Add(v1);
        vertices.Add(v2);
        vertices.Add(v3);

        // Front face triangle (normal winding)
        triangles.Add(startIndex);
        triangles.Add(startIndex + 1);
        triangles.Add(startIndex + 2);

        // Back face triangle (flipped winding)
        triangles.Add(startIndex + 5);
        triangles.Add(startIndex + 4);
        triangles.Add(startIndex + 3);
    }

    /// <summary>
    /// Trilinear upsampling of voxel data to a finer grid.
    /// Reports progress via the optional callback and supports cancellation.
    /// Returns true if the operation was cancelled (dst will be set to the original src and new dims to the originals).
    /// </summary>
    private static bool UpsampleVoxelsTrilinear(float[] src, int w, int h, int d, float factor,
        out float[] dst, out int newW, out int newH, out int newD,
        System.Func<float, bool> reportProgressAndCheckCancel = null)
    {
        newW = Mathf.RoundToInt((w - 1) * factor) + 1;
        newH = Mathf.RoundToInt((h - 1) * factor) + 1;
        newD = Mathf.RoundToInt((d - 1) * factor) + 1;
        dst = new float[newW * newH * newD];
        for (var z = 0; z < newD; z++)
        {
            // Report progress for upsampling (progress of this step alone: 0..1)
            if (reportProgressAndCheckCancel != null && (z & 7) == 0) // throttle calls (every 8 slices)
            {
                var prog = (float)z / (newD - 1);
                // Report negative progress to indicate "upsampling" stage to the caller
                if (reportProgressAndCheckCancel(-prog))
                {
                    // Cancellation requested: return original data/dimensions and indicate cancellation
                    dst = src;
                    newW = w;
                    newH = h;
                    newD = d;
                    return true;
                }
            }

            var gz = (float)z / (newD - 1) * (d - 1);
            var z0 = Mathf.FloorToInt(gz);
            var z1 = Mathf.Min(z0 + 1, d - 1);
            var fz = gz - z0;
            for (var y = 0; y < newH; y++)
            {
                var gy = (float)y / (newH - 1) * (h - 1);
                var y0 = Mathf.FloorToInt(gy);
                var y1 = Mathf.Min(y0 + 1, h - 1);
                var fy = gy - y0;
                for (var x = 0; x < newW; x++)
                {
                    var gx = (float)x / (newW - 1) * (w - 1);
                    var x0 = Mathf.FloorToInt(gx);
                    var x1 = Mathf.Min(x0 + 1, w - 1);
                    var fx = gx - x0;
                    
                    // Trilinear interpolation
                    var c000 = src[x0 + y0 * w + z0 * w * h];
                    var c100 = src[x1 + y0 * w + z0 * w * h];
                    var c010 = src[x0 + y1 * w + z0 * w * h];
                    var c110 = src[x1 + y1 * w + z0 * w * h];
                    var c001 = src[x0 + y0 * w + z1 * w * h];
                    var c101 = src[x1 + y0 * w + z1 * w * h];
                    var c011 = src[x0 + y1 * w + z1 * w * h];
                    var c111 = src[x1 + y1 * w + z1 * w * h];
                    var c00 = Mathf.Lerp(c000, c100, fx);
                    var c01 = Mathf.Lerp(c001, c101, fx);
                    var c10 = Mathf.Lerp(c010, c110, fx);
                    var c11 = Mathf.Lerp(c011, c111, fx);
                    var c0 = Mathf.Lerp(c00, c10, fy);
                    var c1 = Mathf.Lerp(c01, c11, fy);
                    var c = Mathf.Lerp(c0, c1, fz);
                    dst[x + y * newW + z * newW * newH] = c;
                }
            }
        }

        // Final progress callback (100% for upsampling)
        if (reportProgressAndCheckCancel != null)
            reportProgressAndCheckCancel(-1.0f);

        return false;
    }
}