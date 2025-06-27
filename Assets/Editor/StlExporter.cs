using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityVolumeRendering;

public static class StlExporter
{
    /// <summary>
    /// Exports a VolumeRenderedObject to an STL file.
    /// </summary>
    /// <param name="isBinary">True to export in binary format, false for ASCII.</param>
    /// <param name="volumeObject">The VolumeRenderedObject to export.</param>
    public static void Export(bool isBinary, VolumeRenderedObject volumeObject)
    {
        if (volumeObject == null)
        {
            EditorUtility.DisplayDialog("Selection Error", "Please select a GameObject with a VolumeRenderedObject component.", "OK");
            return;
        }

        // Get voxel data and dimensions
        var dataset = volumeObject.dataset;
        var voxels = dataset.data;
        int width = dataset.dimX, height = dataset.dimY, depth = dataset.dimZ;

        // Generate isosurface mesh
        var visibilityWindow = volumeObject.GetVisibilityWindow();
        var isoLevel = dataset.GetMinDataValue() + visibilityWindow.x * (dataset.GetMaxDataValue() - dataset.GetMinDataValue());
        
        var mesh = IsoSurfaceGenerator.BuildMesh(voxels, width, height, depth, isoLevel);
        if (mesh.vertexCount == 0)
        {
            EditorUtility.DisplayDialog("Generation Failed", "The generated mesh is empty. Please adjust the isoLevel.", "OK");
            return;
        }

        // Apply scale and pivot correction
        var transformScale = volumeObject.transform.lossyScale;
        var transformRotation = volumeObject.transform.rotation * Quaternion.Euler(-90f, 0, 0);
        MeshUtil.ApplyDatasetScaleAndCenter(mesh, dataset, transformScale, transformRotation);

        // Prompt user for the file path
        var typeName = isBinary ? "Binary STL" : "ASCII STL";
        var baseName = volumeObject.name.Replace(".dcm", "");
        var fileName = $"{baseName}{(isBinary ? "_binary" : "_ascii")}.stl";
        var path = EditorUtility.SaveFilePanel($"Export {typeName}", Application.dataPath, fileName, "stl");
        if (string.IsNullOrEmpty(path)) return;

        // Export
        if (isBinary)
            WriteBinaryStl(mesh, path);
        else
            WriteAsciiStl(mesh, path);

        EditorUtility.DisplayDialog("Export Complete", $"{typeName} saved to:\n{path}", "OK");
    }

    private static void WriteBinaryStl(Mesh mesh, string filePath)
    {
        var triangles = mesh.triangles;
        var vertices = mesh.vertices;
        var normals = mesh.normals;

        using var writer = new BinaryWriter(File.Open(filePath, FileMode.Create));

        // Write 80-byte header
        var header = new byte[80];
        Encoding.ASCII.GetBytes("unity_volumerendering").CopyTo(header, 0);
        writer.Write(header);

        // Write triangle count
        writer.Write((uint)(triangles.Length / 3));

        // Write triangles
        for (var i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            var normal = normals.Length == vertices.Length
                ? normals[a]
                : Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).normalized;

            WriteVector3(writer, normal);
            WriteVector3(writer, vertices[a]);
            WriteVector3(writer, vertices[b]);
            WriteVector3(writer, vertices[c]);

            writer.Write((ushort)0); // attribute byte count
        }
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 vector)
    {
        writer.Write(vector.x);
        writer.Write(vector.y);
        writer.Write(vector.z);
    }

    private static void WriteAsciiStl(Mesh mesh, string filePath)
    {
        var sb = new StringBuilder("solid unity_volumerendering\n");
        var triangles = mesh.triangles;
        var vertices = mesh.vertices;
        var normals = mesh.normals;

        for (var i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            var normal = normals.Length == vertices.Length
                ? normals[a]
                : Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).normalized;

            sb.AppendLine($"facet normal {normal.x} {normal.y} {normal.z}");
            sb.AppendLine("outer loop");
            sb.AppendLine($"vertex {vertices[a].x} {vertices[a].y} {vertices[a].z}");
            sb.AppendLine($"vertex {vertices[b].x} {vertices[b].y} {vertices[b].z}");
            sb.AppendLine($"vertex {vertices[c].x} {vertices[c].y} {vertices[c].z}");
            sb.AppendLine("endloop");
            sb.AppendLine("endfacet");
        }

        sb.Append("endsolid unity_volumerendering");
        File.WriteAllText(filePath, sb.ToString(), Encoding.ASCII);
    }
}
