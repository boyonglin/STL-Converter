using System.Collections.Generic;
using UnityEngine;
using MarchingCubesProject;

public static class IsoSurfaceGenerator
{
    public static Mesh BuildMesh(
        float[] voxels, int width, int height, int depth,
        float isoLevel = 0.5f, bool smooth = true, bool doubleSided = false)
    {
        var algo = new MarchingCubes(isoLevel);
        var vertices = new List<Vector3>(doubleSided ? 131072 : 65536);
        var triangles = new List<int>(doubleSided ? 262144 : 131072);
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

        var mesh = new Mesh
        {
            indexFormat = vertices.Count > 65_534
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16
        };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        if (smooth) mesh.RecalculateNormals();
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
}