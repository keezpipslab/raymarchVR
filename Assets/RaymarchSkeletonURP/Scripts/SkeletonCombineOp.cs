namespace Premiere.RaymarchSkeleton
{
    /// <summary>
    /// Boolean operator combining Skeleton A and Skeleton B into one surface
    /// in RaymarchSkeletonCore.hlsl's SceneSDFSurface. The int value is
    /// uploaded as _RM_SkeletonCombineOp and switched on directly, so this
    /// enum's ordering/values must match the HLSL side.
    /// </summary>
    public enum SkeletonCombineOp
    {
        Union = 0,               // A + B (smooth union - the original always-on behaviour)
        Subtract = 1,            // A - B (Skeleton B carved out of Skeleton A)
        Intersect = 2,           // A ∩ B (only the overlap survives)
        SymmetricDifference = 3, // A % B / XOR ((A-B) union (B-A), overlap removed)
    }
}
