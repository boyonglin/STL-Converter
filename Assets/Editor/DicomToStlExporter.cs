using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityVolumeRendering;

public static class DicomToStlExporter
{
    // ============================  選單  ============================
    [MenuItem("Tools/Export STL/STL (Binary)")]
    private static void ExportDicomToStlBinary() =>
        ExportDicomToStl(isBinary: true);

    [MenuItem("Tools/Export STL/STL (ASCII)")]
    private static void ExportDicomToStlAscii() =>
        ExportDicomToStl(isBinary: false);

    // =======================  共用出口邏輯  =========================
    private static void ExportDicomToStl(bool isBinary)
    {
        var sel = Selection.activeGameObject;
        var vol = sel ? sel.GetComponent<VolumeRenderedObject>() : null;
        if (vol == null)
        {
            EditorUtility.DisplayDialog("請先選取 DICOM 物件",
                "在 Hierarchy 點選 VolumeRenderedObject 後再試一次。", "了解");
            return;
        }

        // 取得體素 + 尺寸
        VolumeDataset ds = vol.dataset;
        float[] vox = ds.data;
        int w = ds.dimX, h = ds.dimY, d = ds.dimZ;

        // 產等值面
        const float iso = 0.5f;
        Mesh mesh = IsoSurfaceGenerator.BuildMesh(vox, w, h, d, iso);
        if (mesh.vertexCount == 0)
        {
            EditorUtility.DisplayDialog("產生失敗", "Mesh 為空，請調整 isoLevel。", "好");
            return;
        }

        // 尺寸 + Pivot 修正
        Vector3    trScale = vol.transform.lossyScale;
        Quaternion trRot   = vol.transform.rotation * Quaternion.Euler(-90f, 0, 0);
        MeshUtil.ApplyDatasetScaleAndCenter(mesh, ds, trScale, trRot);

        // 讓用戶選檔名
        string typeName = isBinary ? "Binary STL" : "ASCII STL";
        string path = EditorUtility.SaveFilePanel($"匯出 {typeName}", Application.dataPath, sel.name + ".stl", "stl");
        if (string.IsNullOrEmpty(path)) return;

        // 輸出
        if (isBinary)      WriteBinaryStl(mesh, path);
        else               WriteAsciiStl (mesh, path);

        EditorUtility.DisplayDialog("匯出完成", $"{typeName} 已儲存：\n{path}", "OK");
    }

    // =======================  Binary STL  ==========================
    private static void WriteBinaryStl(Mesh mesh, string filePath)
    {
        var t = mesh.triangles;
        var v = mesh.vertices;
        var n = mesh.normals;

        using var bw = new BinaryWriter(File.Open(filePath, FileMode.Create));

        // header (80 bytes)
        var header = new byte[80];
        Encoding.ASCII.GetBytes("unity_volumerendering").CopyTo(header, 0);
        bw.Write(header);

        // triangle count
        bw.Write((uint)(t.Length / 3));

        // triangles
        for (int i = 0; i < t.Length; i += 3)
        {
            int a = t[i], b = t[i + 1], c = t[i + 2];
            Vector3 normal = n.Length == v.Length ? n[a]
                : Vector3.Cross(v[b] - v[a], v[c] - v[a]).normalized;

            bw.Write(normal.x); bw.Write(normal.y); bw.Write(normal.z);

            Vector3 v0 = v[a]; bw.Write(v0.x); bw.Write(v0.y); bw.Write(v0.z);
            Vector3 v1 = v[b]; bw.Write(v1.x); bw.Write(v1.y); bw.Write(v1.z);
            Vector3 v2 = v[c]; bw.Write(v2.x); bw.Write(v2.y); bw.Write(v2.z);

            bw.Write((ushort)0); // attribute byte count
        }
    }

    // ========================  ASCII STL  ==========================
    private static void WriteAsciiStl(Mesh mesh, string filePath)
    {
        var sb = new StringBuilder("solid unity_volumerendering\n");
        int[]   t = mesh.triangles;
        var   vtx = mesh.vertices;
        var   nor = mesh.normals;

        for (int i = 0; i < t.Length; i += 3)
        {
            int a = t[i], b = t[i + 1], c = t[i + 2];
            Vector3 n = nor.Length == vtx.Length ? nor[a]
                : Vector3.Cross(vtx[b] - vtx[a], vtx[c] - vtx[a]).normalized;

            sb.AppendLine($"  facet normal {n.x} {n.y} {n.z}");
            sb.AppendLine("    outer loop");
            sb.AppendLine($"      vertex {vtx[a].x} {vtx[a].y} {vtx[a].z}");
            sb.AppendLine($"      vertex {vtx[b].x} {vtx[b].y} {vtx[b].z}");
            sb.AppendLine($"      vertex {vtx[c].x} {vtx[c].y} {vtx[c].z}");
            sb.AppendLine("    endloop");
            sb.AppendLine("  endfacet");
        }

        sb.Append("endsolid unity_volumerendering");
        File.WriteAllText(filePath, sb.ToString(), Encoding.ASCII);
    }
}
