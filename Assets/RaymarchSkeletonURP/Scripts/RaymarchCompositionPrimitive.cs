namespace Premiere.RaymarchSkeleton
{
    /// <summary>
    /// SDF primitive kinds for body-anchored "composition" elements, matching
    /// the `PrimitiveKind` enum names written by the companion project's
    /// CompositionExporter (one JSON file per export, `kind` field is the
    /// enum name as a string - see RaymarchCompositionData.cs). Enum.TryParse
    /// on the JSON `kind` string maps straight onto this enum, so the names
    /// here must match CompositionExporter's PrimitiveKind exactly.
    ///
    /// Unlike RaymarchPrimitive (joints/edges, 0-3, morphable), these values
    /// are consumed by CompositionPrimitiveSDF() in RaymarchSkeletonCore.hlsl
    /// as an exact switch - no fractional morphing between composition
    /// element kinds.
    /// </summary>
    public enum RaymarchCompositionPrimitive
    {
        Sphere = 0,
        Box = 1,
        Capsule = 2,
        Pyramid = 3,
        Torus = 4,
        RoundBox = 5,
        Cone = 6,
        Octahedron = 7,
        HexagonalPrism = 8,
        Cylinder = 9,
        TriangularPrism = 10,
        Link = 11,
    }
}
