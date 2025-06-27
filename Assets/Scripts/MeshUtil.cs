using UnityEngine;
using UnityVolumeRendering;

public static class MeshUtil
{
    /// <summary>
    /// 依 Dataset 尺寸、Transform 縮放、旋轉，把網格
    ///   - 轉成實際單位 (mm ➜ m)
    ///   - 移除 0.5 voxel 偏差，讓 Pivot = Volume 中心
    /// </summary>
    public static void ApplyDatasetScaleAndCenter(
        Mesh mesh,
        VolumeDataset ds,
        Vector3  extraScale,           // vol.transform.lossyScale
        Quaternion extraRot,          // vol.transform.rotation * 任何額外旋轉
        float globalScale = 1f)      // mm ➜ m
    {
        if (extraScale == Vector3.zero)  extraScale = Vector3.one;

        // 每 voxel 實尺寸（mm）
        Vector3 perVoxel = new(
            ds.scale.x / ds.dimX,
            ds.scale.y / ds.dimY,
            ds.scale.z / ds.dimZ);

        Vector3[] v = mesh.vertices;
        for (int i = 0; i < v.Length; i++)
        {
            // ① voxel → 真實座標 → mm→m → Transform.scale
            Vector3 p = Vector3.Scale(v[i], perVoxel) * globalScale;
            p = Vector3.Scale(p, extraScale);

            // ② 補 -90° 或任何自訂旋轉
            p = extraRot * p;
            v[i] = p;
        }

        // === 用 Dataset 半長度作為真正中心 ===
        Vector3 half = Vector3.Scale(ds.scale, extraScale) * globalScale * 0.5f;
        half = extraRot * half;     // 旋轉到同一座標系

        for (int i = 0; i < v.Length; i++)
            v[i] -= half;           // 把 Pivot 挪到幾何中心

        mesh.vertices = v;
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
    }
}