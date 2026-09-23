using UnityEngine;

namespace Premiere.RaymarchSkeleton
{
    /// <summary>
    /// Wires scene RaymarchSkeletonInstances (+ an optional light) into a
    /// RaymarchSkeletonRendererFeature at runtime.
    ///
    /// The renderer feature is a persistent Project asset (a sub-asset of
    /// your URP Renderer Data asset), while RaymarchSkeletonInstance lives
    /// on scene GameObjects - Unity does not allow an asset's serialized
    /// field to hold a direct reference to a scene object, so dragging one
    /// into the feature's Skeleton A/B slots in the Inspector is rejected
    /// ("Type Mismatch"). This component bridges that gap by assigning the
    /// references in code once the scene is loaded/edited, instead of
    /// through the feature's own Inspector fields.
    ///
    /// Drop this on any GameObject in the scene and assign all four fields
    /// - they're all scene-to-scene or scene-to-asset references, so normal
    /// Inspector drag-and-drop works fine here.
    /// </summary>
    [ExecuteAlways]
    public class RaymarchSkeletonBinder : MonoBehaviour
    {
        public RaymarchSkeletonRendererFeature feature;
        public RaymarchSkeletonInstance skeletonA;
        public RaymarchSkeletonInstance skeletonB;
        public Transform lightSource;

        [Tooltip("Optional body-anchored composition overlays (CompositionExporter JSON), one per skeleton slot above.")]
        public RaymarchCompositionInstance compositionA;
        public RaymarchCompositionInstance compositionB;

        private void OnEnable() => Bind();
        private void OnValidate() => Bind();

        private void Bind()
        {
            if (feature == null) return;
            feature.skeletonA = skeletonA;
            feature.skeletonB = skeletonB;
            feature.lightSource = lightSource;
            feature.compositionA = compositionA;
            feature.compositionB = compositionB;
        }
    }
}
