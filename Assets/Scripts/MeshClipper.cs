using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityVolumeRendering;

public static class MeshClipper
{
    /// <summary>
    /// Clips a world-space mesh against a unit cube centered on <paramref name="box"/> 
    /// (box-local extents [-0.5, 0.5] per axis after TRS). 
    /// Triangles are polygon-clipped against 6 half-spaces, then fan-triangulated.
    /// </summary>
    /// <param name="worldMesh">Input mesh in world space (vertices + triangles used).</param>
    /// <param name="box">Transform defining the clipping cube (its local unit cube after TRS).</param>
    /// <param name="eps">Inside tolerance (default 1e-4).</param>
    /// <param name="cutoutType">Cutout type: Inclusive (keep inside) or Exclusive (keep outside).</param>
    /// <returns>New mesh of clipped geometry; empty mesh if fully outside.</returns>
    private static Mesh ClipByBoxWorld(Mesh worldMesh, Transform box, float eps = 1e-4f, CutoutType cutoutType = CutoutType.Inclusive)
    {
        if (worldMesh == null || worldMesh.vertexCount == 0) return worldMesh;

        // world → box-local / box-local → world
        var toBox = box.worldToLocalMatrix;
        var toWorld = box.localToWorldMatrix;

        var srcVertices  = worldMesh.vertices;
        var srcTriangles = worldMesh.triangles;
        if (srcVertices == null || srcVertices.Length == 0 ||
            srcTriangles == null || srcTriangles.Length == 0)
            return new Mesh();

        // Output buffers
        var outVertices = new List<Vector3>(srcVertices.Length);
        var outIndices  = new List<int>(srcTriangles.Length);

        if (cutoutType == CutoutType.Exclusive)
        {
            ClipExclusiveParallel(srcVertices, srcTriangles, toBox, toWorld, eps, outVertices, outIndices);
        }
        else
        {
            // Working polygon buffers for inclusive clipping
            var polygon = new List<Vector3>(8);
            var scratch = new List<Vector3>(8);

            for (var tri = 0; tri < srcTriangles.Length; tri += 3)
            {
                LoadTriangleBoxLocal(toBox, srcVertices, srcTriangles, tri, polygon);

                // Clip against ±X, ±Y, ±Z
                for (var axis = 0; axis < 3 && polygon.Count > 0; axis++)
                {
                    ClipAxis(polygon, scratch, axis, +1, 0.5f, eps, cutoutType);
                    if (polygon.Count == 0) break;
                    ClipAxis(polygon, scratch, axis, -1, 0.5f, eps, cutoutType);
                }
                if (polygon.Count < 3) continue;

                AppendPolygon(polygon, toWorld, outVertices, outIndices);
            }
        }

        if (outIndices.Count == 0) return new Mesh();

        var clipped = new Mesh
        {
            indexFormat = (outVertices.Count > 65535)
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16
        };
        clipped.SetVertices(outVertices);
        clipped.SetTriangles(outIndices, 0, true);
        clipped.RecalculateNormals();
        clipped.RecalculateBounds();
        return clipped;
    }

    /// <summary>
    /// Clips a world-space mesh against multiple cutout boxes sequentially.
    /// Each box applies its clipping operation to the result of the previous box.
    /// </summary>
    public static Mesh ClipByMultipleBoxesWorld(Mesh worldMesh, List<Transform> boxes)
    {
        if (worldMesh == null || worldMesh.vertexCount == 0 || boxes == null || boxes.Count == 0)
            return worldMesh;

        var inclusive = new List<Transform>();
        var exclusive = new List<Transform>();
        foreach (var box in boxes)
        {
            var type = box.GetComponent<CutoutBox>()?.cutoutType ?? CutoutType.Inclusive;
            (type == CutoutType.Inclusive ? inclusive : exclusive).Add(box);
        }

        var current = worldMesh;
        foreach (var box in inclusive)
        {
            current = ClipByBoxWorld(current, box);
            if (current == null || current.vertexCount == 0)
                return current;
        }

        if (exclusive.Count == 0)
            return current;

        foreach (var box in exclusive)
        {
            current = ClipByBoxWorld(current, box, cutoutType: CutoutType.Exclusive);
            if (current == null || current.vertexCount == 0)
                return current;
        }

        return current;
    }

    // ========================================
    // SECTION: CORE HELPER METHODS
    // ========================================

    private static float DistanceToPlane(Vector3 p, int axis, int sign, float half, CutoutType cutoutType)
    {
        var val = axis == 0 ? p.x : axis == 1 ? p.y : p.z;
        
        // For Inclusive mode: keep points inside the box (distance < 0 when inside)
        // For Exclusive mode: keep points outside the box (invert the distance calculation)
        if (cutoutType == CutoutType.Inclusive)
        {
            return (sign > 0) ? (val - half) : (-val - half);
        }
        else // Exclusive
        {
            // Invert the half-space to keep outside instead of inside
            return (sign > 0) ? (half - val) : (val + half);
        }
    }

    private static Vector3 Intersect(Vector3 a, Vector3 b, float distA, float distB)
    {
        var t = distA / (distA - distB);
        return a + t * (b - a);
    }
    
    private static bool ShouldAddVertex(Vector3 vertex, List<Vector3> polygon)
    {
        return polygon.Count == 0 || (vertex - polygon[^1]).sqrMagnitude > 1e-6f;
    }

    private static void LoadTriangleBoxLocal(Matrix4x4 toBox, Vector3[] vertices, int[] triangles, int triStart, List<Vector3> polygon)
    {
        polygon.Clear();
        polygon.Add(toBox.MultiplyPoint3x4(vertices[triangles[triStart]]));
        polygon.Add(toBox.MultiplyPoint3x4(vertices[triangles[triStart + 1]]));
        polygon.Add(toBox.MultiplyPoint3x4(vertices[triangles[triStart + 2]]));
    }

    private static void AppendPolygon(List<Vector3> polygon, Matrix4x4 toWorld, List<Vector3> outVertices, List<int> outIndices)
    {
        if (polygon == null || polygon.Count < 3)
            return;

        var baseIndex = outVertices.Count;
        foreach (var t in polygon)
        {
            outVertices.Add(toWorld.MultiplyPoint3x4(t));
        }
        for (var k = 2; k < polygon.Count; ++k)
        {
            outIndices.Add(baseIndex);
            outIndices.Add(baseIndex + k - 1);
            outIndices.Add(baseIndex + k);
        }
    }

    // ========================================
    // SECTION: INCLUSIVE CLIPPING
    // ========================================

    // Clip polygon in-place against a single half-space defined by axis/sign/half.
    private static void ClipAxis(List<Vector3> poly, List<Vector3> outPoly, int axis, int sign, float half, float eps, CutoutType cutoutType)
    {
        outPoly.Clear();
        if (poly.Count == 0) return;

        var prev = poly[^1];
        var distPrev = DistanceToPlane(prev, axis, sign, half, cutoutType);
        var insidePrev = distPrev <= eps;

        foreach (var curr in poly)
        {
            var distCurr = DistanceToPlane(curr, axis, sign, half, cutoutType);
            var insideCurr = distCurr <= eps;

            if (insidePrev && insideCurr)
            {
                if (ShouldAddVertex(curr, outPoly))
                    outPoly.Add(curr);
            }
            else if (insidePrev && !insideCurr)
            {
                var intersection = Intersect(prev, curr, distPrev, distCurr);
                if (ShouldAddVertex(intersection, outPoly))
                    outPoly.Add(intersection);
            }
            else if (!insidePrev && insideCurr)
            {
                var intersection = Intersect(prev, curr, distPrev, distCurr);
                if (ShouldAddVertex(intersection, outPoly))
                    outPoly.Add(intersection);

                if (ShouldAddVertex(curr, outPoly))
                    outPoly.Add(curr);
            }

            prev = curr;
            distPrev = distCurr;
            insidePrev = insideCurr;
        }
        poly.Clear();
        poly.AddRange(outPoly);
    }

    // ========================================
    // SECTION: EXCLUSIVE CLIPPING - SEQUENTIAL
    // ========================================

    private static void ClipTriangleExclusive(List<Vector3> triangle, Matrix4x4 toWorld, float eps, List<Vector3> outVertices, List<int> outIndices)
    {
        if (triangle == null || triangle.Count < 3)
            return;

        var remaining = new List<List<Vector3>>
        {
            new(triangle)
        };

        var outsideBuffer = new List<Vector3>(8);
        var insideBuffer = new List<Vector3>(8);

        for (var axis = 0; axis < 3 && remaining.Count > 0; ++axis)
        {
            for (var s = 0; s < 2 && remaining.Count > 0; ++s)
            {
                var sign = (s == 0) ? +1 : -1;
                var next = new List<List<Vector3>>();

                foreach (var poly in remaining)
                {
                    SplitPolygon(poly, axis, sign, 0.5f, eps, outsideBuffer, insideBuffer);

                    if (outsideBuffer.Count >= 3)
                        AppendPolygon(outsideBuffer, toWorld, outVertices, outIndices);

                    if (insideBuffer.Count >= 3)
                        next.Add(new List<Vector3>(insideBuffer));

                    outsideBuffer.Clear();
                    insideBuffer.Clear();
                }

                remaining = next;
            }
        }
    }

    private static void SplitPolygon(List<Vector3> poly, int axis, int sign, float half, float eps, List<Vector3> outside, List<Vector3> inside)
    {
        outside.Clear();
        inside.Clear();
        if (poly == null || poly.Count == 0)
            return;

        // Clamp inclusive tolerance so exclusive clipping never treats slightly-outside points as inside.
        var insideThreshold = Mathf.Min(0f, eps);

        var prev = poly[^1];
        var distPrev = DistanceToPlane(prev, axis, sign, half, CutoutType.Inclusive);
        var insidePrev = distPrev <= insideThreshold;

        foreach (var curr in poly)
        {
            var distCurr = DistanceToPlane(curr, axis, sign, half, CutoutType.Inclusive);
            var insideCurr = distCurr <= insideThreshold;

            if (!insidePrev && !insideCurr)
            {
                if (ShouldAddVertex(curr, outside))
                    outside.Add(curr);
            }
            else if (!insidePrev && insideCurr)
            {
                var intersection = Intersect(prev, curr, distPrev, distCurr);
                if (ShouldAddVertex(intersection, outside))
                    outside.Add(intersection);
                if (ShouldAddVertex(intersection, inside))
                    inside.Add(intersection);
                if (ShouldAddVertex(curr, inside))
                    inside.Add(curr);
            }
            else if (insidePrev && !insideCurr)
            {
                var intersection = Intersect(prev, curr, distPrev, distCurr);
                if (ShouldAddVertex(intersection, inside))
                    inside.Add(intersection);
                if (ShouldAddVertex(intersection, outside))
                    outside.Add(intersection);
                if (ShouldAddVertex(curr, outside))
                    outside.Add(curr);
            }
            else // insidePrev && insideCurr
            {
                if (ShouldAddVertex(curr, inside))
                    inside.Add(curr);
            }

            prev = curr;
            distPrev = distCurr;
            insidePrev = insideCurr;
        }
    }

    // ========================================
    // SECTION: EXCLUSIVE CLIPPING - PARALLEL
    // ========================================

    private static void ClipExclusiveParallel(Vector3[] srcVertices, int[] srcTriangles, Matrix4x4 toBox, Matrix4x4 toWorld, float eps, List<Vector3> outVertices, List<int> outIndices)
    {
        var triangleCount = srcTriangles.Length / 3;
        if (triangleCount == 0)
            return;

        // For small workloads, stick to sequential execution to avoid thread overhead.
        if (triangleCount < 64)
        {
            var polygon = new List<Vector3>(8);
            for (var tri = 0; tri < srcTriangles.Length; tri += 3)
            {
                LoadTriangleBoxLocal(toBox, srcVertices, srcTriangles, tri, polygon);
                ClipTriangleExclusive(polygon, toWorld, eps, outVertices, outIndices);
            }
            return;
        }

        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var suggestedChunk = Math.Max(64, triangleCount / (processorCount * 4));
        var partitioner = Partitioner.Create(0, triangleCount, suggestedChunk);
        var sync = new object();

        Parallel.ForEach(partitioner,
            () => new ExclusiveClipWorker(),
            (range, _, worker) =>
            {
                worker.BeginRange();

                for (var tri = range.Item1; tri < range.Item2; ++tri)
                {
                    var triStart = tri * 3;
                    worker.StartTriangle(toBox, srcVertices, srcTriangles, triStart);
                    ClipTriangleExclusiveWorker(worker, toWorld, eps);
                }

                FlushWorker(worker, outVertices, outIndices, sync);
                return worker;
            },
            worker =>
            {
                FlushWorker(worker, outVertices, outIndices, sync);
            });
    }

    private static void ClipTriangleExclusiveWorker(ExclusiveClipWorker worker, Matrix4x4 toWorld, float eps)
    {
        var remaining = worker.remaining;
        var next = worker.next;

        remaining.Add(worker.ClonePolygon(worker.polygon));

        for (var axis = 0; axis < 3 && remaining.Count > 0; ++axis)
        {
            for (var s = 0; s < 2 && remaining.Count > 0; ++s)
            {
                var sign = (s == 0) ? +1 : -1;

                foreach (var poly in remaining)
                {
                    SplitPolygon(poly, axis, sign, 0.5f, eps, worker.outsideBuffer, worker.insideBuffer);

                    if (worker.outsideBuffer.Count >= 3)
                        AppendPolygon(worker.outsideBuffer, toWorld, worker.localVertices, worker.localIndices);

                    if (worker.insideBuffer.Count >= 3)
                        next.Add(worker.ClonePolygon(worker.insideBuffer));

                    worker.outsideBuffer.Clear();
                    worker.insideBuffer.Clear();

                    worker.ReturnPolygon(poly);
                }

                remaining.Clear();

                (remaining, next) = (next, remaining);
            }
        }

        foreach (var poly in remaining)
            worker.ReturnPolygon(poly);
        remaining.Clear();

        foreach (var poly in next)
            worker.ReturnPolygon(poly);
        next.Clear();
    }

    private static void FlushWorker(ExclusiveClipWorker worker, List<Vector3> outVertices, List<int> outIndices, object sync)
    {
        if (worker.localVertices.Count == 0)
            return;

        lock (sync)
        {
            var baseIndex = outVertices.Count;
            outVertices.AddRange(worker.localVertices);
            outIndices.AddRange(worker.localIndices.Select(t => t + baseIndex));
        }

        worker.localVertices.Clear();
        worker.localIndices.Clear();
    }

    // Worker class for parallel exclusive clipping
    private sealed class ExclusiveClipWorker
    {
        public readonly List<Vector3> polygon = new(8);
        public readonly List<Vector3> localVertices = new(512);
        public readonly List<int> localIndices = new(1024);
        public readonly List<List<Vector3>> remaining = new();
        public readonly List<List<Vector3>> next = new();
        public readonly Stack<List<Vector3>> polygonPool = new();
        public readonly List<Vector3> outsideBuffer = new(8);
        public readonly List<Vector3> insideBuffer = new(8);

        public void BeginRange()
        {
            localVertices.Clear();
            localIndices.Clear();
            if (remaining.Count > 0)
            {
                foreach (var poly in remaining)
                    ReturnPolygon(poly);
                remaining.Clear();
            }
            if (next.Count > 0)
            {
                foreach (var poly in next)
                    ReturnPolygon(poly);
                next.Clear();
            }
            outsideBuffer.Clear();
            insideBuffer.Clear();
        }

        public void StartTriangle(Matrix4x4 toBox, Vector3[] vertices, int[] triangles, int triStart)
        {
            LoadTriangleBoxLocal(toBox, vertices, triangles, triStart, polygon);
        }

        private List<Vector3> RentPolygon()
        {
            if (polygonPool.Count > 0)
            {
                var poly = polygonPool.Pop();
                poly.Clear();
                return poly;
            }
            return new List<Vector3>(8);
        }

        public List<Vector3> ClonePolygon(IList<Vector3> source)
        {
            var poly = RentPolygon();
            poly.AddRange(source);
            return poly;
        }

        public void ReturnPolygon(List<Vector3> poly)
        {
            poly.Clear();
            polygonPool.Push(poly);
        }
    }
}
