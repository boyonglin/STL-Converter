using UnityEngine;
using System.Threading.Tasks;
using UnityVolumeRendering;

/// <summary>
/// Clips voxel data in the voxel domain (before Marching Cubes).
/// Provides watertight results by setting boundary voxels to the isoLevel.
/// </summary>
public static class VoxelClipper
{
    public static float[] ClipByBoxWithBoundary(
        float[] voxels,
        int width,
        int height,
        int depth,
        float isoLevel,
        Vector3 voxelSize,
        Quaternion voxelRotation,
        Transform clipBoxTransform,
        CutoutType cutoutType = CutoutType.Inclusive)
    {
        if (voxels == null || clipBoxTransform == null)
            return voxels;

        var clippedVoxels = new float[voxels.Length];
        var toBoxLocal = clipBoxTransform.worldToLocalMatrix;
        
        var halfDimensions = new Vector3(
            (width - 1) * 0.5f,
            (height - 1) * 0.5f,
            (depth - 1) * 0.5f);
        var geometricCenter = voxelRotation * Vector3.Scale(voxelSize, halfDimensions);

        var slice = width * height;
        var halfExtent = 0.5f;
        var rotMatrix = Matrix4x4.Rotate(voxelRotation);
        
        // Parallel processing
        Parallel.For(0, depth, z =>
        {
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var idx = x + y * width + z * slice;
                var originalValue = voxels[idx];
                
                // Optimized transformation
                var worldX = x * voxelSize.x;
                var worldY = y * voxelSize.y;
                var worldZ = z * voxelSize.z;
                
                var rotatedX = rotMatrix.m00 * worldX + rotMatrix.m01 * worldY + rotMatrix.m02 * worldZ - geometricCenter.x;
                var rotatedY = rotMatrix.m10 * worldX + rotMatrix.m11 * worldY + rotMatrix.m12 * worldZ - geometricCenter.y;
                var rotatedZ = rotMatrix.m20 * worldX + rotMatrix.m21 * worldY + rotMatrix.m22 * worldZ - geometricCenter.z;
                
                var boxLocalX = toBoxLocal.m00 * rotatedX + toBoxLocal.m01 * rotatedY + toBoxLocal.m02 * rotatedZ + toBoxLocal.m03;
                var boxLocalY = toBoxLocal.m10 * rotatedX + toBoxLocal.m11 * rotatedY + toBoxLocal.m12 * rotatedZ + toBoxLocal.m13;
                var boxLocalZ = toBoxLocal.m20 * rotatedX + toBoxLocal.m21 * rotatedY + toBoxLocal.m22 * rotatedZ + toBoxLocal.m23;
                
                var absX = boxLocalX < 0 ? -boxLocalX : boxLocalX;
                var absY = boxLocalY < 0 ? -boxLocalY : boxLocalY;
                var absZ = boxLocalZ < 0 ? -boxLocalZ : boxLocalZ;
                
                var isInsideBox = absX <= halfExtent && absY <= halfExtent && absZ <= halfExtent;
                
                // Apply cutout logic based on type
                if (cutoutType == CutoutType.Inclusive)
                {
                    // Inclusive: keep inside box, remove outside
                    if (isInsideBox)
                    {
                        clippedVoxels[idx] = originalValue;
                    }
                    else
                    {
                        clippedVoxels[idx] = isoLevel - 1.0f;
                    }
                }
                else // CutoutType.Exclusive
                {
                    // Exclusive: remove inside box, keep outside
                    if (isInsideBox)
                    {
                        clippedVoxels[idx] = isoLevel - 1.0f;
                    }
                    else
                    {
                        clippedVoxels[idx] = originalValue;
                    }
                }
            }
        });
        
        return clippedVoxels;
    }
}