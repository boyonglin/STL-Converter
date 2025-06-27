using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityVolumeRendering;

public static class DicomToStlExporter
{
    // ============================  Menu Items  ============================
    [MenuItem("Tools/Export STL/STL (Binary)")]
    private static void ExportDicomToStlBinary() => ExportDicomToStl(true);

    [MenuItem("Tools/Export STL/STL (ASCII)")]
    private static void ExportDicomToStlAscii() => ExportDicomToStl(false);

    // =======================  Shared Export Logic  =========================
    private static void ExportDicomToStl(bool isBinary)
    {
        var selectedObject = Selection.activeGameObject;
        var volumeObject = selectedObject ? selectedObject.GetComponent<VolumeRenderedObject>() : null;
        if (volumeObject == null)
        {
            EditorUtility.DisplayDialog("請先選取 DICOM 物件",
                "在 Hierarchy 點選 VolumeRenderedObject 後再試一次。", "了解");
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
            EditorUtility.DisplayDialog("產生失敗", "Mesh 為空，請調整 isoLevel。", "好");
            return;
        }

        // Apply scale and pivot correction
        var transformScale = volumeObject.transform.lossyScale;
        var transformRotation = volumeObject.transform.rotation * Quaternion.Euler(-90f, 0, 0);
        MeshUtil.ApplyDatasetScaleAndCenter(mesh, dataset, transformScale, transformRotation);

        // Prompt user for the file path
        var typeName = isBinary ? "Binary STL" : "ASCII STL";
        var path = EditorUtility.SaveFilePanel($"匯出 {typeName}", Application.dataPath, selectedObject.name + ".stl", "stl");
        if (string.IsNullOrEmpty(path)) return;

        // Export
        if (isBinary)
            WriteBinaryStl(mesh, path);
        else
            WriteAsciiStl(mesh, path);

        EditorUtility.DisplayDialog("匯出完成", $"{typeName} 已儲存：\n{path}", "OK");
    }

    // =======================  Binary STL Export  ==========================
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

    // ========================  ASCII STL Export  ==========================
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