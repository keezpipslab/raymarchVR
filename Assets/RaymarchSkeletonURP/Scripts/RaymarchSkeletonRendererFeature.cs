using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Premiere.RaymarchSkeleton
{
    /// <summary>
    /// URP 6000.3 (Render Graph) renderer feature that composites the
    /// raymarched skeleton scene into the camera color target. Wire up
    /// two RaymarchSkeletonInstance components (one per performer) and
    /// this pushes their joint/edge data into Shaders/RaymarchSkeleton.shader
    /// every frame.
    ///
    /// Add this feature to your URP Renderer asset
    /// (Renderer Data -> Add Renderer Feature -> Raymarch Skeleton).
    /// </summary>
    public class RaymarchSkeletonRendererFeature : ScriptableRendererFeature
    {
        private const int MaxJointsPerSkeleton = 40;
        private const int MaxEdgesPerSkeleton = 40;
        private const int SkeletonCount = 2;
        private const int MaxJoints = MaxJointsPerSkeleton * SkeletonCount;
        private const int MaxEdges = MaxEdgesPerSkeleton * SkeletonCount;
        private const int MaxObjects = 4;

        [Header("Material")]
        public Material raymarchMaterial;

        [Tooltip("Turns the raymarch pass on/off at runtime. When off, this feature draws nothing and the camera falls back to whatever it would normally render (e.g. the avatars' skinned meshes, if enabled). Same effect as ScriptableRendererFeature.SetActive(), exposed here for the F1 UI/scripting.")]
        public bool raymarchEnabled = true;

        [Header("Quality")]
        [Range(8, 255)]
        [Tooltip("Max sphere-tracing steps per ray. Lower this to trade sharpness at grazing/distant surfaces for GPU time - most useful for VR, which pays this cost twice (once per eye) every frame.")]
        public int maxSteps = 255;

        [Tooltip("Under single-pass instanced XR, compute the raymarch once (not once per eye) and cheaply duplicate it to both eyes instead of true per-eye stereo. Roughly halves the cost of the most expensive part of this shader; the raymarched content loses stereo depth (head position/rotation tracking is unaffected). No effect outside single-pass instanced XR.")]
        public bool monoInVR = true;

        [Header("Skeletons (exactly two)")]
        public RaymarchSkeletonInstance skeletonA;
        public RaymarchSkeletonInstance skeletonB;

        [Header("Light")]
        public Transform lightSource;
        [Range(0, 64)] public float shadowSmooth = 16f;
        [Range(0, 1)] public float shadowStrength = 0.6f;

        [Header("Background / Fog")]
        public Color backgroundColor = Color.black;
        public Color backgroundOcclusionColor = Color.black;
        public float fogMinDist = 12f;
        public float fogMaxDist = 20f;

        [Header("Scene")]
        public Transform sceneRoot; // optional extra transform applied to the whole raymarched scene

        [Range(0, 1f)]
        [Tooltip("Blend radius between the combined skeletons and the optional shared object primitives (== /vis/skelobjectsmooth in the original).")]
        public float skelObjectSmoothing = 0.05f;

        [Header("Skeleton A/B Combine")]
        [Tooltip("Boolean operator combining Skeleton A and Skeleton B into one surface: Union (+), Subtract (A minus B, -), Intersect (∩), or Symmetric Difference/XOR (%).")]
        public SkeletonCombineOp skeletonCombineOp = SkeletonCombineOp.Union;

        [Range(0, 1f)]
        [Tooltip("Blend radius for the Skeleton A/B combine operator above. At the default value, Union reproduces this system's original always-on hardcoded blend exactly.")]
        public float skeletonCombineSmoothing = 0.001f;

        private RaymarchPass _pass;

        private static readonly int SceneTransformId = Shader.PropertyToID("_RM_SceneTransform");
        private static readonly int LightPositionId = Shader.PropertyToID("_RM_LightPosition");
        private static readonly int ShadowSmoothId = Shader.PropertyToID("_RM_ShadowSmooth");
        private static readonly int ShadowStrengthId = Shader.PropertyToID("_RM_ShadowStrength");
        private static readonly int BgColorId = Shader.PropertyToID("_RM_BgColor");
        private static readonly int BgOcclusionColorId = Shader.PropertyToID("_RM_BgOcclusionColor");
        private static readonly int FogMinDistId = Shader.PropertyToID("_RM_FogMinDist");
        private static readonly int FogMaxDistId = Shader.PropertyToID("_RM_FogMaxDist");
        private static readonly int JointEdgeSmoothingId = Shader.PropertyToID("_RM_JointEdgeSmoothing");
        private static readonly int SkelObjectSmoothingId = Shader.PropertyToID("_RM_SkelObjectSmoothing");
        private static readonly int SkeletonCombineOpId = Shader.PropertyToID("_RM_SkeletonCombineOp");
        private static readonly int SkeletonCombineSmoothingId = Shader.PropertyToID("_RM_SkeletonCombineSmoothing");
        private static readonly int SkeletonInvertId = Shader.PropertyToID("_RM_SkeletonInvert");

        private static readonly int JointColorId = Shader.PropertyToID("_RM_JointColor");
        private static readonly int JointAmbientId = Shader.PropertyToID("_RM_JointAmbientScale");
        private static readonly int JointDiffuseId = Shader.PropertyToID("_RM_JointDiffuseScale");
        private static readonly int JointSpecularId = Shader.PropertyToID("_RM_JointSpecularScale");
        private static readonly int JointSpecularPowId = Shader.PropertyToID("_RM_JointSpecularPow");
        private static readonly int JointOcclusionScaleId = Shader.PropertyToID("_RM_JointOcclusionScale");
        private static readonly int JointOcclusionRangeId = Shader.PropertyToID("_RM_JointOcclusionRange");
        private static readonly int JointOcclusionResolutionId = Shader.PropertyToID("_RM_JointOcclusionResolution");
        private static readonly int JointOcclusionColorId = Shader.PropertyToID("_RM_JointOcclusionColor");

        private static readonly int EdgeColorId = Shader.PropertyToID("_RM_EdgeColor");
        private static readonly int EdgeAmbientId = Shader.PropertyToID("_RM_EdgeAmbientScale");
        private static readonly int EdgeDiffuseId = Shader.PropertyToID("_RM_EdgeDiffuseScale");
        private static readonly int EdgeSpecularId = Shader.PropertyToID("_RM_EdgeSpecularScale");
        private static readonly int EdgeSpecularPowId = Shader.PropertyToID("_RM_EdgeSpecularPow");
        private static readonly int EdgeOcclusionScaleId = Shader.PropertyToID("_RM_EdgeOcclusionScale");
        private static readonly int EdgeOcclusionRangeId = Shader.PropertyToID("_RM_EdgeOcclusionRange");
        private static readonly int EdgeOcclusionResolutionId = Shader.PropertyToID("_RM_EdgeOcclusionResolution");
        private static readonly int EdgeOcclusionColorId = Shader.PropertyToID("_RM_EdgeOcclusionColor");

        private static readonly int JointTransformsId = Shader.PropertyToID("_RM_JointTransforms");
        private static readonly int JointPrimitivesId = Shader.PropertyToID("_RM_JointPrimitives");
        private static readonly int JointSizesId = Shader.PropertyToID("_RM_JointSizes");
        private static readonly int JointRoundingsId = Shader.PropertyToID("_RM_JointRoundings");
        private static readonly int JointSmoothingsId = Shader.PropertyToID("_RM_JointSmoothings");

        private static readonly int EdgeTransformsId = Shader.PropertyToID("_RM_EdgeTransforms");
        private static readonly int EdgePrimitivesId = Shader.PropertyToID("_RM_EdgePrimitives");
        private static readonly int EdgeLengthsId = Shader.PropertyToID("_RM_EdgeLengths");
        private static readonly int EdgeSizesId = Shader.PropertyToID("_RM_EdgeSizes");
        private static readonly int EdgeRoundingsId = Shader.PropertyToID("_RM_EdgeRoundings");
        private static readonly int EdgeSmoothingsId = Shader.PropertyToID("_RM_EdgeSmoothings");

        private static readonly int ObjectPrimitivesId = Shader.PropertyToID("_RM_ObjectPrimitives");

        private static readonly int MaxStepsId = Shader.PropertyToID("_RM_MaxSteps");
        private static readonly int MonoSourceId = Shader.PropertyToID("_RM_MonoSource");

        // Reused per-frame scratch arrays.
        private readonly Matrix4x4[] _jointTransforms = new Matrix4x4[MaxJoints];
        private readonly float[] _jointPrimitives = new float[MaxJoints];
        private readonly Vector4[] _jointSizes = new Vector4[MaxJoints];
        private readonly float[] _jointRoundings = new float[MaxJoints];
        private readonly float[] _jointSmoothings = new float[MaxJoints];

        private readonly Matrix4x4[] _edgeTransforms = new Matrix4x4[MaxEdges];
        private readonly float[] _edgePrimitives = new float[MaxEdges];
        private readonly float[] _edgeLengths = new float[MaxEdges];
        private readonly Vector4[] _edgeSizes = new Vector4[MaxEdges];
        private readonly float[] _edgeRoundings = new float[MaxEdges];
        private readonly float[] _edgeSmoothings = new float[MaxEdges];

        private readonly float[] _objectPrimitives = new float[MaxObjects];

        public override void Create()
        {
            _pass = new RaymarchPass(this)
            {
                // Must run after the skybox, not just after opaques: the
                // raymarch shader has ZWrite Off (see RaymarchSkeleton.shader),
                // so it never claims depth. If injected at
                // AfterRenderingOpaques, URP's own skybox pass (which runs
                // *after* that event) would repaint every pixel where no real
                // geometry wrote depth - i.e. the entire background - erasing
                // this pass's output everywhere except where it happened to
                // overlap actual GameObjects.
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox,
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (raymarchMaterial == null || !raymarchEnabled) return;
            renderer.EnqueuePass(_pass);
        }

        /// <summary>Gathers both skeletons and pushes every uniform to the material. Called once per frame from the pass.</summary>
        private void UpdateMaterial(Camera camera)
        {
            skeletonA?.Gather();
            skeletonB?.Gather();

            // --- scalar / global settings ---
            raymarchMaterial.SetInt(MaxStepsId, maxSteps);

            Matrix4x4 sceneTransform = sceneRoot != null ? sceneRoot.worldToLocalMatrix : Matrix4x4.identity;
            raymarchMaterial.SetMatrix(SceneTransformId, sceneTransform);

            Vector3 lightPos = lightSource != null ? lightSource.position : (camera.transform.position + camera.transform.forward * 2f + Vector3.up * 4f);
            raymarchMaterial.SetVector(LightPositionId, lightPos);
            raymarchMaterial.SetFloat(ShadowSmoothId, shadowSmooth);
            raymarchMaterial.SetFloat(ShadowStrengthId, shadowStrength);

            raymarchMaterial.SetColor(BgColorId, backgroundColor);
            raymarchMaterial.SetColor(BgOcclusionColorId, backgroundOcclusionColor);
            raymarchMaterial.SetFloat(FogMinDistId, fogMinDist);
            raymarchMaterial.SetFloat(FogMaxDistId, fogMaxDist);

            raymarchMaterial.SetFloat(SkelObjectSmoothingId, skelObjectSmoothing);
            raymarchMaterial.SetInt(SkeletonCombineOpId, (int)skeletonCombineOp);
            raymarchMaterial.SetFloat(SkeletonCombineSmoothingId, skeletonCombineSmoothing);

            // --- per-skeleton look ---
            var skeletons = new[] { skeletonA, skeletonB };

            var jointColors = new Vector4[SkeletonCount];
            var jointAmbient = new float[SkeletonCount];
            var jointDiffuse = new float[SkeletonCount];
            var jointSpecular = new float[SkeletonCount];
            var jointSpecularPow = new float[SkeletonCount];
            var jointOccScale = new float[SkeletonCount];
            var jointOccRange = new float[SkeletonCount];
            var jointOccRes = new float[SkeletonCount];
            var jointOccColor = new Vector4[SkeletonCount];

            var edgeColors = new Vector4[SkeletonCount];
            var edgeAmbient = new float[SkeletonCount];
            var edgeDiffuse = new float[SkeletonCount];
            var edgeSpecular = new float[SkeletonCount];
            var edgeSpecularPow = new float[SkeletonCount];
            var edgeOccScale = new float[SkeletonCount];
            var edgeOccRange = new float[SkeletonCount];
            var edgeOccRes = new float[SkeletonCount];
            var edgeOccColor = new Vector4[SkeletonCount];

            var jointEdgeSmoothing = new float[SkeletonCount];
            var skeletonInvert = new float[SkeletonCount];

            for (int sk = 0; sk < SkeletonCount; sk++)
            {
                var inst = skeletons[sk];

                // Sensible fallback look if a skeleton slot is empty.
                Color jc = inst != null ? inst.jointColor : Color.gray;
                Color ec = inst != null ? inst.edgeColor : Color.gray;

                jointColors[sk] = jc;
                jointAmbient[sk] = inst != null ? inst.jointAmbientScale : 0.3f;
                jointDiffuse[sk] = inst != null ? inst.jointDiffuseScale : 1f;
                jointSpecular[sk] = inst != null ? inst.jointSpecularScale : 1f;
                jointSpecularPow[sk] = inst != null ? inst.jointSpecularPow : 24f;
                jointOccScale[sk] = inst != null ? inst.jointOcclusionScale : 0.5f;
                jointOccRange[sk] = inst != null ? inst.jointOcclusionRange : 0.5f;
                jointOccRes[sk] = inst != null ? inst.jointOcclusionResolution : 0.08f;
                jointOccColor[sk] = inst != null ? (Vector4)(Color)inst.jointOcclusionColor : Vector4.zero;

                edgeColors[sk] = ec;
                edgeAmbient[sk] = inst != null ? inst.edgeAmbientScale : 0.3f;
                edgeDiffuse[sk] = inst != null ? inst.edgeDiffuseScale : 1f;
                edgeSpecular[sk] = inst != null ? inst.edgeSpecularScale : 1f;
                edgeSpecularPow[sk] = inst != null ? inst.edgeSpecularPow : 24f;
                edgeOccScale[sk] = inst != null ? inst.edgeOcclusionScale : 0.5f;
                edgeOccRange[sk] = inst != null ? inst.edgeOcclusionRange : 0.5f;
                edgeOccRes[sk] = inst != null ? inst.edgeOcclusionResolution : 0.08f;
                edgeOccColor[sk] = inst != null ? (Vector4)(Color)inst.edgeOcclusionColor : Vector4.zero;

                jointEdgeSmoothing[sk] = inst != null ? inst.jointEdgeSmoothing : 0.05f;
                skeletonInvert[sk] = (inst != null && inst.invert) ? 1f : 0f;
            }

            raymarchMaterial.SetVectorArray(JointColorId, jointColors);
            raymarchMaterial.SetFloatArray(JointAmbientId, jointAmbient);
            raymarchMaterial.SetFloatArray(JointDiffuseId, jointDiffuse);
            raymarchMaterial.SetFloatArray(JointSpecularId, jointSpecular);
            raymarchMaterial.SetFloatArray(JointSpecularPowId, jointSpecularPow);
            raymarchMaterial.SetFloatArray(JointOcclusionScaleId, jointOccScale);
            raymarchMaterial.SetFloatArray(JointOcclusionRangeId, jointOccRange);
            raymarchMaterial.SetFloatArray(JointOcclusionResolutionId, jointOccRes);
            raymarchMaterial.SetVectorArray(JointOcclusionColorId, jointOccColor);

            raymarchMaterial.SetVectorArray(EdgeColorId, edgeColors);
            raymarchMaterial.SetFloatArray(EdgeAmbientId, edgeAmbient);
            raymarchMaterial.SetFloatArray(EdgeDiffuseId, edgeDiffuse);
            raymarchMaterial.SetFloatArray(EdgeSpecularId, edgeSpecular);
            raymarchMaterial.SetFloatArray(EdgeSpecularPowId, edgeSpecularPow);
            raymarchMaterial.SetFloatArray(EdgeOcclusionScaleId, edgeOccScale);
            raymarchMaterial.SetFloatArray(EdgeOcclusionRangeId, edgeOccRange);
            raymarchMaterial.SetFloatArray(EdgeOcclusionResolutionId, edgeOccRes);
            raymarchMaterial.SetVectorArray(EdgeOcclusionColorId, edgeOccColor);

            raymarchMaterial.SetFloatArray(JointEdgeSmoothingId, jointEdgeSmoothing);
            raymarchMaterial.SetFloatArray(SkeletonInvertId, skeletonInvert);

            // --- flatten joints/edges from both skeletons into fixed-size arrays ---
            for (int i = 0; i < MaxJoints; i++)
            {
                _jointTransforms[i] = Matrix4x4.identity;
                _jointPrimitives[i] = -1f;
                _jointSizes[i] = Vector4.zero;
                _jointRoundings[i] = 0f;
                _jointSmoothings[i] = 0.01f;
            }
            for (int i = 0; i < MaxEdges; i++)
            {
                _edgeTransforms[i] = Matrix4x4.identity;
                _edgePrimitives[i] = -1f;
                _edgeLengths[i] = 0f;
                _edgeSizes[i] = Vector4.zero;
                _edgeRoundings[i] = 0f;
                _edgeSmoothings[i] = 0.01f;
            }

            for (int sk = 0; sk < SkeletonCount; sk++)
            {
                var inst = skeletons[sk];
                if (inst == null) continue;

                int jointBase = sk * MaxJointsPerSkeleton;
                int jointCount = Mathf.Min(inst.ActiveJointCount, MaxJointsPerSkeleton);
                for (int jI = 0; jI < jointCount; jI++)
                {
                    _jointTransforms[jointBase + jI] = inst.jointInverseTransforms[jI];
                    _jointPrimitives[jointBase + jI] = inst.jointPrimitives[jI];
                    _jointSizes[jointBase + jI] = inst.jointSizes[jI];
                    _jointRoundings[jointBase + jI] = inst.jointRoundings[jI];
                    _jointSmoothings[jointBase + jI] = inst.jointSmoothings[jI];
                }

                int edgeBase = sk * MaxEdgesPerSkeleton;
                int edgeCount = Mathf.Min(inst.ActiveEdgeCount, MaxEdgesPerSkeleton);
                for (int eI = 0; eI < edgeCount; eI++)
                {
                    _edgeTransforms[edgeBase + eI] = inst.edgeInverseTransforms[eI];
                    _edgePrimitives[edgeBase + eI] = inst.edgePrimitives[eI];
                    _edgeLengths[edgeBase + eI] = inst.edgeLengths[eI];
                    _edgeSizes[edgeBase + eI] = inst.edgeSizes[eI];
                    _edgeRoundings[edgeBase + eI] = inst.edgeRoundings[eI];
                    _edgeSmoothings[edgeBase + eI] = inst.edgeSmoothings[eI];
                }
            }

            raymarchMaterial.SetMatrixArray(JointTransformsId, _jointTransforms);
            raymarchMaterial.SetFloatArray(JointPrimitivesId, _jointPrimitives);
            raymarchMaterial.SetVectorArray(JointSizesId, _jointSizes);
            raymarchMaterial.SetFloatArray(JointRoundingsId, _jointRoundings);
            raymarchMaterial.SetFloatArray(JointSmoothingsId, _jointSmoothings);

            raymarchMaterial.SetMatrixArray(EdgeTransformsId, _edgeTransforms);
            raymarchMaterial.SetFloatArray(EdgePrimitivesId, _edgePrimitives);
            raymarchMaterial.SetFloatArray(EdgeLengthsId, _edgeLengths);
            raymarchMaterial.SetVectorArray(EdgeSizesId, _edgeSizes);
            raymarchMaterial.SetFloatArray(EdgeRoundingsId, _edgeRoundings);
            raymarchMaterial.SetFloatArray(EdgeSmoothingsId, _edgeSmoothings);

            // No props wired up by default - leave the (optional) object slots inactive.
            for (int i = 0; i < MaxObjects; i++) _objectPrimitives[i] = -1f;
            raymarchMaterial.SetFloatArray(ObjectPrimitivesId, _objectPrimitives);

            // Camera ray reconstruction happens in-shader via Unity's built-in
            // UNITY_MATRIX_I_VP / _WorldSpaceCameraPos rather than a manually
            // set uniform - those are already correct per-eye under single-pass
            // instanced XR, which a single mono matrix set from here couldn't be.
        }

        private class RaymarchPass : ScriptableRenderPass
        {
            private readonly RaymarchSkeletonRendererFeature _feature;

            private class PassData
            {
                public Material material;
                public int instanceCount;
            }

            private class BlitPassData
            {
                public Material material;
                public TextureHandle source;
            }

            public RaymarchPass(RaymarchSkeletonRendererFeature feature)
            {
                _feature = feature;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();

                if (_feature.raymarchMaterial == null || !resourceData.activeColorTexture.IsValid())
                    return;

                _feature.UpdateMaterial(cameraData.camera);

                bool singlePassStereo = cameraData.xr.enabled && cameraData.xr.singlePassEnabled;
                bool mono = singlePassStereo && _feature.monoInVR;

                if (!mono)
                {
                    using (var builder = renderGraph.AddRasterRenderPass<PassData>("Raymarch Skeleton", out var passData))
                    {
                        passData.material = _feature.raymarchMaterial;
                        // Single-pass instanced XR renders both eyes in one draw call,
                        // one instance per eye; every other path (multi-pass XR) draws one.
                        passData.instanceCount = singlePassStereo ? 2 : 1;

                        builder.SetRenderAttachment(resourceData.activeColorTexture, 0);
                        builder.AllowPassCulling(false);

                        builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                        {
                            ctx.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, data.instanceCount);
                        });
                    }
                    return;
                }

                // --- Mono-in-VR path: run the expensive raymarch pass ONCE into a
                // plain (non-array) intermediate texture, then cheaply duplicate that
                // single image into both eyes' slices of the real stereo target with
                // a trivial passthrough pass, instead of paying the full raymarch
                // cost twice. ---
                var monoDesc = new TextureDesc(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height)
                {
                    colorFormat = cameraData.cameraTargetDescriptor.graphicsFormat,
                    name = "_RaymarchMonoSource",
                    clearBuffer = false,
                };
                TextureHandle monoTexture = renderGraph.CreateTexture(monoDesc);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Raymarch Skeleton (mono)", out var passData))
                {
                    passData.material = _feature.raymarchMaterial;
                    passData.instanceCount = 1;

                    builder.SetRenderAttachment(monoTexture, 0);
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, data.instanceCount);
                    });
                }

                using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>("Raymarch Skeleton (duplicate to both eyes)", out var blitData))
                {
                    blitData.material = _feature.raymarchMaterial;
                    blitData.source = monoTexture;

                    builder.UseTexture(monoTexture);
                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true); // required for SetGlobalTexture below

                    builder.SetRenderFunc((BlitPassData data, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.SetGlobalTexture(MonoSourceId, data.source);
                        // Shader pass 1 = "RaymarchSkeletonMonoBlit" in RaymarchSkeleton.shader.
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, data.material, 1, MeshTopology.Triangles, 3, 2);
                    });
                }
            }
        }
    }
}
