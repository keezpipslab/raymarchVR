using System;
using System.IO;
using UnityEngine;

namespace Premiere.RaymarchSkeleton
{
    /// <summary>
    /// Renders a set of body-anchored primitives exported from the companion
    /// project's CompositionExporter as raymarched SDF shapes, re-attached
    /// every frame to this avatar's live Humanoid pose.
    ///
    /// Each exported element's `anchor` is either `Joint_{BoneName}` (a
    /// single Humanoid bone - BoneName matches a HumanBodyBones member name
    /// directly, e.g. "LeftLowerArm") or `Bone_{From}_{To}` (a limb segment,
    /// anchored at the midpoint between two bones, oriented the same way
    /// RaymarchSkeletonInstance orients edges: parent->child, LookRotation
    /// with world-up). For each element:
    ///   worldPos = anchorWorldPos + anchorWorldRot * localPosition
    ///   worldRot = anchorWorldRot * localRotation
    /// matching the reconstruction formula the exporting project documents.
    ///
    /// One of these sits alongside a RaymarchAvatarSource on (or near) each
    /// performer, feeding avatarAnimator from the same Humanoid Animator.
    /// RaymarchSkeletonRendererFeature reads Gather()'s output from
    /// compositionA/compositionB every frame and uploads it into the
    /// shader's shared "object" arrays (see RaymarchSkeletonCore.hlsl).
    /// </summary>
    [ExecuteAlways]
    public class RaymarchCompositionInstance : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("The Humanoid Animator this composition's anchors resolve against. If left empty, GetComponentInParent<Animator>() is used at OnEnable - point this at the same Animator your RaymarchAvatarSource uses.")]
        public Animator avatarAnimator;

        [Tooltip("Exported composition JSON, imported as a Text Asset (drag the .json file into the project, or use compositionFilePath below to load from an arbitrary path at runtime instead).")]
        public TextAsset compositionJson;

        [Tooltip("If set, loaded instead of compositionJson at OnEnable - an absolute path to a JSON file exported by CompositionExporter (e.g. somewhere outside the project, for iterating without re-importing). Leave empty to use compositionJson.")]
        public string compositionFilePath;

        [Tooltip("Load compositionJson/compositionFilePath automatically when this component is enabled. Turn off if you'll call LoadFromJson/LoadFromFile yourself (e.g. from a runtime file-loading UI).")]
        public bool loadOnEnable = true;

        [Header("Look (shared across every element in this composition)")]
        public Color color = Color.white;
        [Range(0, 2)] public float ambientScale = 0.35f;
        [Range(0, 2)] public float diffuseScale = 1.0f;
        [Range(0, 4)] public float specularScale = 1.0f;
        [Range(1, 128)] public float specularPow = 24.0f;
        [Range(0, 1)] public float occlusionScale = 0.6f;
        public float occlusionRange = 0.5f;
        public float occlusionResolution = 0.08f;
        public Color occlusionColor = Color.black;

        [Header("Shape")]
        [Tooltip("Corner rounding applied to elements whose kind is RoundBox. The exported JSON only carries a size, not a rounding amount, so this is shared across every RoundBox element in this composition. Box elements are always sharp (rounding 0).")]
        [Range(0, 0.5f)] public float roundBoxRounding = 0.08f;

        [Tooltip("Smooth-min blend radius between this composition's own elements.")]
        public float smoothing = 0.03f;

        // Cached per-frame data, read by RaymarchSkeletonRendererFeature.
        [NonSerialized] public Matrix4x4[] elementInverseTransforms;
        [NonSerialized] public float[] elementPrimitives;
        [NonSerialized] public Vector3[] elementSizes;
        [NonSerialized] public float[] elementRoundings;

        public int ActiveElementCount { get; private set; }

        private RaymarchCompositionExportData _data;

        private void OnEnable()
        {
            if (avatarAnimator == null)
                avatarAnimator = GetComponentInParent<Animator>();

            if (loadOnEnable)
            {
                if (!string.IsNullOrEmpty(compositionFilePath)) LoadFromFile(compositionFilePath);
                else if (compositionJson != null) LoadFromJson(compositionJson.text);
            }
        }

        /// <summary>Parses and replaces the current composition. Safe to call at runtime to hot-swap a freshly exported file.</summary>
        public void LoadFromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                _data = null;
                return;
            }

            try
            {
                _data = JsonUtility.FromJson<RaymarchCompositionExportData>(json);
            }
            catch (Exception e)
            {
                Debug.LogError($"RaymarchCompositionInstance ({name}): failed to parse composition JSON - {e.Message}", this);
                _data = null;
            }
        }

        public void LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Debug.LogWarning($"RaymarchCompositionInstance ({name}): composition file not found: {path}", this);
                return;
            }
            LoadFromJson(File.ReadAllText(path));
        }

        /// <summary>
        /// Re-resolves every element's anchor against the current Humanoid
        /// pose and rebuilds the transform/primitive/size/rounding arrays.
        /// Call this once per frame (RaymarchSkeletonRendererFeature does
        /// this automatically via Gather()).
        /// </summary>
        public void Gather()
        {
            var elements = _data?.elements;
            if (elements == null || avatarAnimator == null || !avatarAnimator.isHuman)
            {
                ActiveElementCount = 0;
                return;
            }

            EnsureCapacity(elements.Length);

            int outI = 0;
            for (int i = 0; i < elements.Length; i++)
            {
                var el = elements[i];
                if (el == null) continue;
                if (!TryResolveAnchor(el.anchor, out Vector3 anchorPos, out Quaternion anchorRot)) continue;
                if (!Enum.TryParse(el.kind, out RaymarchCompositionPrimitive kind)) continue;

                Vector3 worldPos = anchorPos + anchorRot * el.localPosition;
                Quaternion worldRot = anchorRot * el.localRotation;
                Matrix4x4 world = Matrix4x4.TRS(worldPos, worldRot, Vector3.one);

                elementInverseTransforms[outI] = world.inverse;
                elementPrimitives[outI] = (float)kind;
                elementSizes[outI] = el.size;
                elementRoundings[outI] = kind == RaymarchCompositionPrimitive.RoundBox ? roundBoxRounding : 0f;
                outI++;
            }
            ActiveElementCount = outI;
        }

        /// <summary>
        /// Resolves a CompositionExporter anchor name against this avatar's
        /// live Humanoid pose. "Joint_{BoneName}" resolves directly via
        /// HumanBodyBones (BoneName is already a HumanBodyBones member name
        /// for every joint the exporter uses). "Bone_{From}_{To}" resolves
        /// to the midpoint between the two named bones, oriented the same
        /// way RaymarchSkeletonInstance.Gather() orients edges.
        /// </summary>
        private bool TryResolveAnchor(string anchor, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (string.IsNullOrEmpty(anchor)) return false;

            if (anchor.StartsWith("Joint_", StringComparison.Ordinal))
            {
                string boneName = anchor.Substring("Joint_".Length);
                if (!Enum.TryParse(boneName, out HumanBodyBones bone)) return false;

                Transform t = avatarAnimator.GetBoneTransform(bone);
                if (t == null) return false;

                pos = t.position;
                rot = t.rotation;
                return true;
            }

            if (anchor.StartsWith("Bone_", StringComparison.Ordinal))
            {
                string rest = anchor.Substring("Bone_".Length);

                // Bone names are PascalCase with no underscores of their own
                // (LeftUpperArm, LeftLowerArm, ...), so try every underscore
                // as the From/To split point and use the first one where
                // both halves resolve to real bones - robust even if that
                // assumption ever changes.
                for (int i = 0; i < rest.Length; i++)
                {
                    if (rest[i] != '_') continue;

                    string fromName = rest.Substring(0, i);
                    string toName = rest.Substring(i + 1);
                    if (!Enum.TryParse(fromName, out HumanBodyBones fromBone)) continue;
                    if (!Enum.TryParse(toName, out HumanBodyBones toBone)) continue;

                    Transform fromT = avatarAnimator.GetBoneTransform(fromBone);
                    Transform toT = avatarAnimator.GetBoneTransform(toBone);
                    if (fromT == null || toT == null) continue;

                    Vector3 parentPos = fromT.position;
                    Vector3 childPos = toT.position;
                    pos = (parentPos + childPos) * 0.5f;

                    Vector3 dir = childPos - parentPos;
                    rot = dir.sqrMagnitude > 1e-12f
                        ? Quaternion.LookRotation(dir.normalized, Vector3.up)
                        : Quaternion.identity;
                    return true;
                }
            }

            return false;
        }

        private void EnsureCapacity(int count)
        {
            if (elementInverseTransforms != null && elementInverseTransforms.Length >= count) return;

            elementInverseTransforms = new Matrix4x4[count];
            elementPrimitives = new float[count];
            elementSizes = new Vector3[count];
            elementRoundings = new float[count];
        }
    }
}
