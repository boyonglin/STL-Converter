using UnityEngine;
using UnityVolumeRendering;

public static class MeshUtil
{
    /// <summary>
    /// 把 Marching Cubes 網格：
    /// 1. 轉到實體尺寸 (mm→m) 並套 Transform 縮放
    /// 2. 套自訂旋轉（例如 -90°X）
    /// 3. 消除「0.5 voxel 偏差」，讓 Pivot＝幾何中心
    /// </summary>
    public static void ApplyDatasetScaleAndCenter(
        Mesh mesh,
        VolumeDataset ds,
        Vector3 extraScale,            // vol.transform.lossyScale
        Quaternion extraRot,           // vol.transform.rotation * 任何額外旋轉
        float globalScale = 1f)        // 1 = mm，0.001 = m
    {
        if (extraScale == Vector3.zero) extraScale = Vector3.one;

        // ── 每 voxel 的實長度（含 globalScale） ──
        Vector3 perVoxel = new(
            ds.scale.x / ds.dimX,
            ds.scale.y / ds.dimY,
            ds.scale.z / ds.dimZ);
        perVoxel *= globalScale;               // mm→m（如設 0.001）
        perVoxel = Vector3.Scale(perVoxel, extraScale);

        // ── 幾何中心位移 (dim-1)/2 × perVoxel ──
        Vector3 halfCount = new(
            (ds.dimX - 1) * 0.5f,
            (ds.dimY - 1) * 0.5f,
            (ds.dimZ - 1) * 0.5f);
        Vector3 center = Vector3.Scale(perVoxel, halfCount);
        center = extraRot * center;            // 旋轉到相同座標系

        // ── 套用到所有頂點 ──
        Vector3[] v = mesh.vertices;
        for (int i = 0; i < v.Length; i++)
        {
            // voxel index → 真實座標
            Vector3 p = Vector3.Scale(v[i], perVoxel);
            p = extraRot * p;
            v[i] = p - center;                 // 把 Pivot 挪到中心
        }

        mesh.vertices = v;
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
    }
}