using System.Collections.Generic;
using System.Linq;
using UnityEngine;

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
    /// <returns>New mesh of clipped geometry; empty mesh if fully outside.</returns>
    public static Mesh ClipByBoxWorld(Mesh worldMesh, Transform box, float eps = 1e-4f)
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

        // Working polygon buffers
        var polygon = new List<Vector3>(8);
        var scratch = new List<Vector3>(8);

        // Output buffers
        var outVertices = new List<Vector3>(srcVertices.Length);
        var outIndices  = new List<int>(srcTriangles.Length);

        // Process each triangle
        for (var tri = 0; tri < srcTriangles.Length; tri += 3)
        {
            polygon.Clear();
            polygon.Add(toBox.MultiplyPoint3x4(srcVertices[srcTriangles[tri]]));
            polygon.Add(toBox.MultiplyPoint3x4(srcVertices[srcTriangles[tri + 1]]));
            polygon.Add(toBox.MultiplyPoint3x4(srcVertices[srcTriangles[tri + 2]]));

            // Clip against ±X, ±Y, ±Z
            for (var axis = 0; axis < 3 && polygon.Count > 0; axis++)
            {
                ClipAxis(polygon, scratch, axis, +1, 0.5f, eps);
                if (polygon.Count == 0) break;
                ClipAxis(polygon, scratch, axis, -1, 0.5f, eps);
            }
            if (polygon.Count < 3) continue;

            // Fan triangulate, back to world space
            var baseIndex = outVertices.Count;
            outVertices.AddRange(polygon.Select(p => toWorld.MultiplyPoint3x4(p)));
            for (var k = 2; k < polygon.Count; ++k)
            {
                outIndices.Add(baseIndex);
                outIndices.Add(baseIndex + k - 1);
                outIndices.Add(baseIndex + k);
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

    // Clip polygon in-place against a single half-space defined by axis/sign/half.
    private static void ClipAxis(List<Vector3> poly, List<Vector3> outPoly, int axis, int sign, float half, float eps)
    {
        outPoly.Clear();
        if (poly.Count == 0) return;

        var prev = poly[^1];
        var distPrev = DistanceToPlane(prev, axis, sign, half);
        var insidePrev = distPrev <= eps;

        foreach (var curr in poly)
        {
            var distCurr = DistanceToPlane(curr, axis, sign, half);
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

    private static float DistanceToPlane(Vector3 p, int axis, int sign, float half)
    {
        var val = axis == 0 ? p.x : axis == 1 ? p.y : p.z;
        return (sign > 0) ? (val - half) : (-val - half);
    }

    private static Vector3 Intersect(Vector3 a, Vector3 b, float distA, float distB)
    {
        var t = distA / (distA - distB);
        return a + t * (b - a);
    }
    
    private static bool ShouldAddVertex(Vector3 vertex, List<Vector3> polygon)
    {
        return polygon.Count == 0 || (vertex - polygon[^1]).sqrMagnitude > 1e-9f;
    }
}
