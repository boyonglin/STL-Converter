using System;
using System.Collections.Generic;
using UnityEngine;
using MarchingCubesProject;
using System.Threading;
using System.Threading.Tasks;

public static class IsoSurfaceGenerator
{
    // Thread-local buffers helper for parallel marching cubes
    private class LocalBuffers { public List<Vector3> verts; public List<int> tris; public LocalBuffers() { verts = new List<Vector3>(); tris = new List<int>(); } public LocalBuffers(int estVerts, int estTris) { verts = new List<Vector3>(Math.Max(16, estVerts)); tris = new List<int>(Math.Max(48, estTris)); } }

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

        // Parallel marching cubes: process z-slices in parallel and accumulate thread-local buffers.
        var slice = width * height;

        // Thread-local buffers to avoid locking in the hot path
        // Estimate per-thread capacities to reduce reallocations
        int estVertsPerThread = Math.Max(1024, (width * height) / 8);
        int estTrisPerThread = estVertsPerThread * 3; // triangle indices
        // Track all thread-local values so we can safely read buffersTL.Values after the parallel loop
        var buffersTL = new ThreadLocal<LocalBuffers>(() => new LocalBuffers(estVertsPerThread, estTrisPerThread), trackAllValues: true);
        var mcTL = new ThreadLocal<MarchingCubes>(() => new MarchingCubes(isoLevel));

        int processedSlices = 0;
        int cancelFlag = 0; // 0 == running, 1 == cancel
        Exception backgroundException = null;

        var options = new ParallelOptions { MaxDegreeOfParallelism = System.Environment.ProcessorCount };

        // Run generation on background threads, poll progress on caller thread below
        var generationTask = Task.Run(() =>
        {
            try
            {
                Parallel.For(0, depth - 1, options, (z, state) =>
                {
                    if (Interlocked.CompareExchange(ref cancelFlag, 0, 0) != 0)
                    {
                        state.Stop();
                        return;
                    }

                    var localBuf = buffersTL.Value;
                    var localVerts = localBuf.verts;
                    var localTris = localBuf.tris;

                    var cube = new float[8];
                    for (var y = 0; y < height - 1; y++)
                    for (var x = 0; x < width - 1; x++)
                    {
                        var p = x + y * width + z * slice;
                        cube[0] = voxels[p];
                        cube[1] = voxels[p + 1];
                        cube[2] = voxels[p + 1 + width];
                        cube[3] = voxels[p + width];
                        cube[4] = voxels[p + slice];
                        cube[5] = voxels[p + 1 + slice];
                        cube[6] = voxels[p + 1 + width + slice];
                        cube[7] = voxels[p + width + slice];

                        // Polygonise into the thread-local buffers
                        mcTL.Value.PolygoniseCube(x, y, z, cube, localVerts, localTris);
                    }

                    Interlocked.Increment(ref processedSlices);
                });
            }
            catch (Exception ex)
            {
                backgroundException = ex;
                Interlocked.Exchange(ref cancelFlag, 1);
            }
        });

        // Poll progress on calling thread so reportProgressAndCheckCancel (likely a Unity call) runs on main thread
        while (!generationTask.IsCompleted)
        {
            if (reportProgressAndCheckCancel != null)
            {
                var processed = Interlocked.CompareExchange(ref processedSlices, 0, 0);
                var progress = (depth - 1 > 0) ? (float)processed / (depth - 1) : 1f;
                if (reportProgressAndCheckCancel(progress))
                {
                    Interlocked.Exchange(ref cancelFlag, 1);
                    break;
                }
            }
            Thread.Sleep(10);
        }

        try { generationTask.Wait(); } catch (AggregateException) { }

        if (backgroundException != null || Interlocked.CompareExchange(ref cancelFlag, 0, 0) != 0)
        {
            // Cancelled or errored: return empty mesh
            return new Mesh();
        }

        // Merge thread-local buffers into final lists
        var vertices = new List<Vector3>(doubleSided ? 131072 : 65536);
        var triangles = new List<int>(doubleSided ? 262144 : 131072);

        foreach (var buf in buffersTL.Values)
        {
            if (buf == null) continue;
            var offset = vertices.Count;
            if (buf.verts.Count > 0)
                vertices.AddRange(buf.verts);
            if (buf.tris.Count > 0)
            {
                for (int i = 0; i < buf.tris.Count; i++)
                    triangles.Add(buf.tris[i] + offset);
            }
        }

        // Dispose thread-local containers to release thread-local memory
        try { buffersTL.Dispose(); } catch { }
        try { mcTL.Dispose(); } catch { }

        // If double-sided requested, convert to double-sided geometry (duplicates vertices per triangle)
        if (doubleSided)
        {
            var dsVerts = new List<Vector3>(vertices.Count * 2);
            var dsTris = new List<int>(triangles.Count * 2);
            for (var i = 0; i < triangles.Count; i += 3)
            {
                var a = triangles[i];
                var b = triangles[i + 1];
                var c = triangles[i + 2];
                var v1 = vertices[a];
                var v2 = vertices[b];
                var v3 = vertices[c];
                AddDoubleSidedTriangle(dsVerts, dsTris, v1, v2, v3);
            }
            vertices = dsVerts;
            triangles = dsTris;
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

        // Precompute constants
        float oldWMinus1 = (w - 1);
        float oldHMinus1 = (h - 1);
        float oldDMinus1 = (d - 1);
        float newWMinus1 = (newW - 1);
        float newHMinus1 = (newH - 1);
        float newDMinus1 = (newD - 1);
        int oldSlice = w * h;
        int newSlice = newW * newH;

        // Create local copies to avoid capturing out-parameters inside lambda
        var srcLocal = src;
        var dstLocal = dst;
        int wLocal = w, hLocal = h, dLocal = d;
        int newWLocal = newW, newHLocal = newH, newDLocal = newD;
        float oldWMinus1Local = oldWMinus1, oldHMinus1Local = oldHMinus1, oldDMinus1Local = oldDMinus1;
        float newWMinus1Local = newWMinus1, newHMinus1Local = newHMinus1, newDMinus1Local = newDMinus1;
        int oldSliceLocal = oldSlice, newSliceLocal = newSlice;

        // Progress/cancellation state for parallel execution
        int processedSlices = 0;
        int cancelFlag = 0; // 0 == running, 1 == cancel

        var options = new ParallelOptions { MaxDegreeOfParallelism = System.Environment.ProcessorCount };

        // Run the heavy work on background threads but poll progress on this (main) thread.
        Exception backgroundException = null;
        var backgroundTask = Task.Run(() =>
        {
            try
            {
                Parallel.For(0, newDLocal, options, (z, state) =>
                {
                    // If cancellation observed, stop early
                    if (Interlocked.CompareExchange(ref cancelFlag, 0, 0) != 0)
                    {
                        state.Stop();
                        return;
                    }

                    // Map z coordinate once per slice
                    var gz = (newDMinus1Local > 0f) ? (z / newDMinus1Local) * oldDMinus1Local : 0f;
                    var z0 = Mathf.FloorToInt(gz);
                    var z1 = Mathf.Min(z0 + 1, dLocal - 1);
                    var fz = gz - z0;

                    // Local copies for hot loops
                    var localSrc = srcLocal;
                    var localDst = dstLocal;
                    var localW = wLocal;
                    var localNewW = newWLocal;
                    var localNewH = newHLocal;
                    var localOldSlice = oldSliceLocal;
                    var localNewSlice = newSliceLocal;

                    int z0OffsetOld = z0 * localOldSlice;
                    int z1OffsetOld = z1 * localOldSlice;
                    int zOffsetNew = z * localNewSlice;

                    for (int y = 0; y < localNewH; y++)
                    {
                        var gy = (newHMinus1Local > 0f) ? (y / newHMinus1Local) * oldHMinus1Local : 0f;
                        var y0 = Mathf.FloorToInt(gy);
                        var y1 = Mathf.Min(y0 + 1, hLocal - 1);
                        var fy = gy - y0;
                        var y0W = y0 * localW;
                        var y1W = y1 * localW;
                        var destYBase = zOffsetNew + y * localNewW;

                        for (int x = 0; x < localNewW; x++)
                        {
                            var gx = (newWMinus1Local > 0f) ? (x / newWMinus1Local) * oldWMinus1Local : 0f;
                            var x0 = Mathf.FloorToInt(gx);
                            var x1 = Mathf.Min(x0 + 1, wLocal - 1);
                            var fx = gx - x0;

                            // Trilinear interpolation using precomputed offsets
                            var c000 = localSrc[x0 + y0W + z0OffsetOld];
                            var c100 = localSrc[x1 + y0W + z0OffsetOld];
                            var c010 = localSrc[x0 + y1W + z0OffsetOld];
                            var c110 = localSrc[x1 + y1W + z0OffsetOld];
                            var c001 = localSrc[x0 + y0W + z1OffsetOld];
                            var c101 = localSrc[x1 + y0W + z1OffsetOld];
                            var c011 = localSrc[x0 + y1W + z1OffsetOld];
                            var c111 = localSrc[x1 + y1W + z1OffsetOld];

                            var c00 = Mathf.Lerp(c000, c100, fx);
                            var c01 = Mathf.Lerp(c001, c101, fx);
                            var c10 = Mathf.Lerp(c010, c110, fx);
                            var c11 = Mathf.Lerp(c011, c111, fx);
                            var c0 = Mathf.Lerp(c00, c10, fy);
                            var c1 = Mathf.Lerp(c01, c11, fy);
                            var c = Mathf.Lerp(c0, c1, fz);

                            localDst[destYBase + x] = c;
                        }
                    }

                    // Mark this slice as processed for progress reporting
                    Interlocked.Increment(ref processedSlices);
                });
            }
            catch (Exception ex)
            {
                backgroundException = ex;
                Interlocked.Exchange(ref cancelFlag, 1);
            }
        });

        // Poll progress on this (calling) thread. This allows safe Unity API calls from the callback.
        while (!backgroundTask.IsCompleted)
        {
            if (reportProgressAndCheckCancel != null)
            {
                var processed = Interlocked.CompareExchange(ref processedSlices, 0, 0);
                var prog = (newDLocal > 1) ? (float)processed / (newDLocal - 1) : 1f;
                // Report negative progress to indicate upsampling stage
                if (reportProgressAndCheckCancel(-prog))
                {
                    Interlocked.Exchange(ref cancelFlag, 1);
                    break;
                }
            }
            Thread.Sleep(10); // small sleep to avoid busy-waiting
        }

        // Wait for background task to finish and observe any exceptions
        try
        {
            backgroundTask.Wait();
        }
        catch (AggregateException)
        {
            // backgroundException handled below
        }

        if (backgroundException != null || Interlocked.CompareExchange(ref cancelFlag, 0, 0) != 0)
        {
            // Cancellation or error: return original data/dimensions and indicate cancellation
            dst = src;
            newW = w;
            newH = h;
            newD = d;
            return true;
        }

        // Final progress callback (100% for upsampling)
        if (reportProgressAndCheckCancel != null)
            reportProgressAndCheckCancel(-1.0f);

        return false;
    }
}