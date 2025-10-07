using UnityEngine;
using UnityVolumeRendering;

public static class MeshUtil
{
    /// <summary>
    /// Transforms Marching Cubes mesh by:
    /// 1. Converting to real-world dimensions (mm→m) and applying Transform scaling
    /// 2. Applying custom rotation (e.g., -90°X)
    /// 3. Eliminating "0.5 voxel offset" to center the pivot at the geometric center
    /// </summary>
    /// <param name="mesh">The mesh to transform</param>
    /// <param name="dataset">Volume dataset containing dimensions and scale</param>
    /// <param name="extraScale">Additional scaling (e.g., vol.transform.lossyScale)</param>
    /// <param name="extraRotation">Additional rotation (e.g., vol.transform.rotation * custom rotation)</param>
    /// <param name="globalScale">Global scale factor: 1 = mm, 0.001 = m</param>
    public static void ApplyDatasetScaleAndCenter(
        Mesh mesh,
        VolumeDataset dataset,
        Vector3 extraScale,
        Quaternion extraRotation,
        float globalScale = 1f)
    {
        if (mesh == null || dataset == null)
            return;

        var voxelSize = CalculateVoxelSize(dataset, globalScale, extraScale);
        var geometricCenter = CalculateGeometricCenter(dataset, voxelSize, extraRotation);
        
        TransformMeshVertices(mesh, voxelSize, extraRotation, geometricCenter);
        
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
    }

    private static Vector3 CalculateVoxelSize(VolumeDataset dataset, float globalScale, Vector3 extraScale)
    {
        var voxelSize = new Vector3(
            dataset.scale.x / dataset.dimX,
            dataset.scale.y / dataset.dimY,
            dataset.scale.z / dataset.dimZ);
        
        voxelSize *= globalScale;
        return Vector3.Scale(voxelSize, extraScale);
    }

    private static Vector3 CalculateGeometricCenter(VolumeDataset dataset, Vector3 voxelSize, Quaternion rotation)
    {
        var halfDimensions = new Vector3(
            (dataset.dimX - 1) * 0.5f,
            (dataset.dimY - 1) * 0.5f,
            (dataset.dimZ - 1) * 0.5f);
        
        var center = Vector3.Scale(voxelSize, halfDimensions);
        return rotation * center;
    }

    private static void TransformMeshVertices(Mesh mesh, Vector3 voxelSize, Quaternion rotation, Vector3 center)
    {
        var vertices = mesh.vertices;
        
        for (var i = 0; i < vertices.Length; i++)
        {
            var worldPosition = Vector3.Scale(vertices[i], voxelSize);
            worldPosition = rotation * worldPosition;
            vertices[i] = worldPosition - center;
        }
        
        mesh.vertices = vertices;
    }
}