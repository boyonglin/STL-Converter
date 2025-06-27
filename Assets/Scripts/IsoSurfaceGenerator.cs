using System.Collections.Generic;
using UnityEngine;
using MarchingCubesProject;

public static class IsoSurfaceGenerator
{
    public static Mesh BuildMesh(
        float[] voxels, int width, int height, int depth,
        float isoLevel = 0.5f, bool smooth = true)
    {
        var algo = new MarchingCubes(isoLevel);
        var vertices = new List<Vector3>(65536);
        var triangles = new List<int>(131072);
        var cube = new float[8];
        var slice = width * height;

        for (var z = 0; z < depth - 1; z++)
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

            algo.PolygoniseCube(x, y, z, cube, vertices, triangles);
        }

        var mesh = new Mesh
        {
            indexFormat =
                vertices.Count > 65_534
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16
        };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        if (smooth) mesh.RecalculateNormals();
        return mesh;
    }
}