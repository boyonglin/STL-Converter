using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class MeshClipper
{
    /// <summary>
    /// Clips a mesh by an axis-aligned unit box (local extents [-0.5, 0.5]) defined by <paramref name="boxTransform"/>.
    /// The source mesh is expressed in <paramref name="meshTransform"/>'s local space; we build a polygon per source triangle,
    /// successively clip it against the 6 half-spaces, then fan-triangulate the surviving polygon.
    /// </summary>
    /// <param name="srcMesh">Source mesh (only vertices + triangles are used).</param>
    /// <param name="meshTransform">Transform that owns the mesh (local space reference).</param>
    /// <param name="boxTransform">Transform of the unit clip box (scale/rotation/position affect clipping frame).</param>
    /// <param name="eps">Tolerance for classifying a vertex as inside (default 1e-6).</param>
    /// <returns>A new clipped mesh (empty mesh if fully outside). Returns original if null/empty.</returns>
    public static Mesh ClipByBox(Mesh srcMesh, Transform meshTransform, Transform boxTransform, float eps = 1e-6f)
    {
        if (srcMesh == null || srcMesh.vertexCount == 0) return srcMesh;

        // meshLocal → boxLocal
        var toBox = boxTransform.worldToLocalMatrix * meshTransform.localToWorldMatrix;
        // boxLocal → meshLocal
        var toMesh = meshTransform.worldToLocalMatrix * boxTransform.localToWorldMatrix;

        var srcVertices = srcMesh.vertices;
        var srcTriangles = srcMesh.triangles;
        if (srcVertices == null || srcVertices.Length == 0 || srcTriangles == null || srcTriangles.Length == 0)
            return new Mesh();

        // Working polygon buffers.
        var polygon = new List<Vector3>(8);
        var scratch = new List<Vector3>(8);

        // Output buffers.
        var outVertices = new List<Vector3>(srcVertices.Length);
        var outIndices = new List<int>(srcTriangles.Length);

        // Sequentially process each source triangle.
        for (var tri = 0; tri < srcTriangles.Length; tri += 3)
        {
            polygon.Clear();
            polygon.Add(toBox.MultiplyPoint3x4(srcVertices[srcTriangles[tri]]));
            polygon.Add(toBox.MultiplyPoint3x4(srcVertices[srcTriangles[tri + 1]]));
            polygon.Add(toBox.MultiplyPoint3x4(srcVertices[srcTriangles[tri + 2]]));

            // Clip against 6 planes: ±X, ±Y, ±Z (v * axisSign - half <= 0)
            for (var axis = 0; axis < 3 && polygon.Count > 0; axis++)
            {
                ClipAxis(polygon, scratch, axis, +1, 0.5f, eps);
                if (polygon.Count == 0) break;
                ClipAxis(polygon, scratch, axis, -1, 0.5f, eps);
            }
            if (polygon.Count < 3) continue; // Fully clipped or degenerate

            // Fan triangulate
            var baseIndex = outVertices.Count;
            outVertices.AddRange(polygon.Select(t => toMesh.MultiplyPoint3x4(t)));

            for (var k = 2; k < polygon.Count; k++)
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
                outPoly.Add(curr);
            }
            else if (insidePrev && !insideCurr)
            {
                outPoly.Add(Intersect(prev, curr, distPrev, distCurr));
            }
            else if (!insidePrev && insideCurr)
            {
                outPoly.Add(Intersect(prev, curr, distPrev, distCurr));
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
}
