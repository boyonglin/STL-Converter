using System.Collections.Generic;
using UnityEngine;
using MarchingCubesProject;   // Scrawk 命名空間

public static class IsoSurfaceGenerator
{
    /// <summary>
    ///   根據體素資料與 isoLevel 產生 Mesh
    /// </summary>
    public static Mesh BuildMesh(
        float[] voxels, int w, int h, int d,
        float isoLevel = 0.5f, bool smooth = true)
    {
        var algo   = new MarchingCubes(isoLevel);   // 只吃 isoLevel
        var verts  = new List<Vector3>(65536);
        var tris   = new List<int>(131072);
        var cube   = new float[8];
        int wh     = w * h;

        for (int z = 0; z < d - 1; z++)
        for (int y = 0; y < h - 1; y++)
        for (int x = 0; x < w - 1; x++)
        {
            int p = x + y * w + z * wh;
            // 八個角的體素值
            cube[0] = voxels[p];
            cube[1] = voxels[p + 1];
            cube[2] = voxels[p + 1 + w];
            cube[3] = voxels[p + w];
            cube[4] = voxels[p + wh];
            cube[5] = voxels[p + 1 + wh];
            cube[6] = voxels[p + 1 + w + wh];
            cube[7] = voxels[p + w + wh];

            algo.PolygoniseCube(x, y, z, cube, verts, tris);
        }

        var mesh = new Mesh
        {
            indexFormat =
                verts.Count > 65_534
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16
        };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        if (smooth) mesh.RecalculateNormals();
        return mesh;
    }
}