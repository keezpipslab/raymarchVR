namespace Premiere.RaymarchSkeleton
{
    /// <summary>
    /// Basic SDF primitive types, matching the integer part of the
    /// `primitive` float in RaymarchSkeletonCore.hlsl / the original
    /// primitiveMorphSDF() in shaderFrag.glsl (0-3 range; fractal
    /// primitives 4+ from the original were not ported).
    ///
    /// A joint/edge "primitive" value can carry a fractional part
    /// (e.g. 1.5) to morph smoothly between two neighbouring types,
    /// exactly like the source shader.
    /// </summary>
    public enum RaymarchPrimitive
    {
        Sphere = 0,
        Box = 1,
        Capsule = 2,
        Cylinder = 3,
    }
}
