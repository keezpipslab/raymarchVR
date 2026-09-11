using System;
using UnityEngine;

namespace Premiere.RaymarchSkeleton
{
    /// <summary>
    /// One raymarched skeleton (one performer). Feed it joint world
    /// positions every frame (from motion capture, an OSC receiver
    /// replacing the original osc_control.py, animation rig bones,
    /// whatever) and it derives:
    ///   - a joint transform (inverse world matrix) + primitive per joint
    ///   - a bone/edge transform (positioned+oriented between parent and
    ///     child joint, like the original's edge construction) + length
    ///     per connectivity entry
    /// which RaymarchSkeletonRendererFeature reads and uploads to the
    /// shader arrays each frame.
    ///
    /// Joint positions are required; joint rotations (jointWorldRotations)
    /// are optional - supply them to orient box/capsule/cylinder joint
    /// primitives along the limb, matching what skeleton.py did with its
    /// per-joint rotations + jointRotCorrections. Without rotations, joints
    /// fall back to an unrotated orientation (fine for spheres, or when you
    /// only care about edge/bone orientation, which is always derived from
    /// parent->child position regardless of this setting).
    /// </summary>
    [ExecuteAlways]
    public class RaymarchSkeletonInstance : MonoBehaviour
    {
        [Header("Topology")]
        public SkeletonTopology topology;

        [Header("Live joint positions (world space), one per topology.jointFilter entry")]
        public Vector3[] jointWorldPositions;

        [Header("Live joint rotations (world space, optional)")]
        [Tooltip("One entry per topology.jointFilter entry, same order as jointWorldPositions. If left empty (or shorter than the joint count), missing joints fall back to an unrotated (identity) orientation, matching the old position-only behaviour.")]
        public Quaternion[] jointWorldRotations;

        [Tooltip("Use jointWorldRotations to orient joint primitives (lets box/capsule/cylinder joints match limb orientation, not just position). Turn off to force identity orientation even if rotations are supplied.")]
        public bool useJointRotations = true;

        [Tooltip("Applied on top of every joint's (corrected) rotation. Use this instead of hand-editing every jointRotCorrections entry when your whole capture rig's axes are offset from Unity's (the original tool hardcoded a blanket +90 deg around Y for this same reason - this is the equivalent knob).")]
        public Quaternion globalJointRotationOffset = Quaternion.identity;

        [Header("Joint look")]
        public RaymarchPrimitive jointPrimitiveType = RaymarchPrimitive.Sphere;
        public Vector3 jointSize = new Vector3(0.08f, 0.08f, 0.08f);
        public float jointRounding = 0.02f;
        [Tooltip("Smooth-min blend radius between neighbouring joint primitives.")]
        public float jointSmoothing = 0.05f;
        public Color jointColor = Color.white;
        [Range(0, 2)] public float jointAmbientScale = 0.35f;
        [Range(0, 2)] public float jointDiffuseScale = 1.0f;
        [Range(0, 4)] public float jointSpecularScale = 1.0f;
        [Range(1, 128)] public float jointSpecularPow = 24.0f;
        [Range(0, 1)] public float jointOcclusionScale = 0.6f;
        public float jointOcclusionRange = 0.5f;
        public float jointOcclusionResolution = 0.08f;
        public Color jointOcclusionColor = Color.black;

        [Header("Edge (bone) look")]
        public RaymarchPrimitive edgePrimitiveType = RaymarchPrimitive.Capsule;
        [Tooltip("x/y = bone radius, z is a multiplier on the actual bone length (usually 1).")]
        public Vector3 edgeSize = new Vector3(0.035f, 0.035f, 1.0f);
        public float edgeRounding = 0.02f;
        public float edgeSmoothing = 0.05f;
        public Color edgeColor = new Color(0.6f, 0.8f, 1f);
        [Range(0, 2)] public float edgeAmbientScale = 0.35f;
        [Range(0, 2)] public float edgeDiffuseScale = 1.0f;
        [Range(0, 4)] public float edgeSpecularScale = 1.0f;
        [Range(1, 128)] public float edgeSpecularPow = 24.0f;
        [Range(0, 1)] public float edgeOcclusionScale = 0.6f;
        public float edgeOcclusionRange = 0.5f;
        public float edgeOcclusionResolution = 0.08f;
        public Color edgeOcclusionColor = Color.black;

        [Header("Blend between joints and edges of this skeleton")]
        public float jointEdgeSmoothing = 0.08f;

        [Header("Skeleton A/B combine")]
        [Tooltip("Flip this skeleton's surface inside-out (negate its signed distance) before it's combined with the other skeleton. Turns solid geometry into a cavity/void - most useful paired with Subtract or Intersect on the renderer feature. Applied once to this whole skeleton's already-combined joints+edges, not per-primitive.")]
        public bool invert = false;

        // Cached per-frame data, read by RaymarchSkeletonRendererFeature.
        [NonSerialized] public Matrix4x4[] jointInverseTransforms;
        [NonSerialized] public float[] jointPrimitives;
        [NonSerialized] public Vector3[] jointSizes;
        [NonSerialized] public float[] jointRoundings;
        [NonSerialized] public float[] jointSmoothings;

        [NonSerialized] public Matrix4x4[] edgeInverseTransforms;
        [NonSerialized] public float[] edgePrimitives;
        [NonSerialized] public float[] edgeLengths;
        [NonSerialized] public Vector3[] edgeSizes;
        [NonSerialized] public float[] edgeRoundings;
        [NonSerialized] public float[] edgeSmoothings;

        public int ActiveJointCount { get; private set; }
        public int ActiveEdgeCount { get; private set; }

        /// <summary>
        /// Recomputes joint/edge transforms from jointWorldPositions.
        /// Call this once per frame (RaymarchSkeletonRendererFeature does
        /// this automatically via Gather()) after you've updated
        /// jointWorldPositions from your motion source.
        /// </summary>
        public void Gather()
        {
            if (topology == null || jointWorldPositions == null)
            {
                ActiveJointCount = 0;
                ActiveEdgeCount = 0;
                return;
            }

            int jointCount = Mathf.Min(topology.JointCount, jointWorldPositions.Length);
            EnsureCapacity(jointCount, topology.EdgeCount);

            for (int jI = 0; jI < jointCount; jI++)
            {
                Vector3 pos = jointWorldPositions[jI];
                Quaternion rot = ComputeJointRotation(jI);
                Matrix4x4 world = Matrix4x4.TRS(pos, rot, Vector3.one);

                jointInverseTransforms[jI] = world.inverse;
                jointPrimitives[jI] = (float)jointPrimitiveType;
                jointSizes[jI] = jointSize;
                jointRoundings[jI] = jointRounding;
                jointSmoothings[jI] = jointSmoothing;
            }
            ActiveJointCount = jointCount;

            int eI = 0;
            for (int pjI = 0; pjI < jointCount; pjI++)
            {
                var children = topology.jointConnectivity != null && pjI < topology.jointConnectivity.Length
                    ? topology.jointConnectivity[pjI]
                    : null;
                if (children == null) continue;

                Vector3 parentPos = jointWorldPositions[pjI];

                foreach (int cjI in children)
                {
                    if (cjI < 0 || cjI >= jointCount) continue;
                    if (eI >= edgeInverseTransforms.Length) break;

                    Vector3 childPos = jointWorldPositions[cjI];
                    Vector3 mid = (parentPos + childPos) * 0.5f;
                    Vector3 dir = childPos - parentPos;
                    float length = dir.magnitude;

                    Quaternion rot = length > 1e-6f
                        ? Quaternion.LookRotation(dir / length, Vector3.up)
                        : Quaternion.identity;

                    Matrix4x4 world = Matrix4x4.TRS(mid, rot, Vector3.one);

                    edgeInverseTransforms[eI] = world.inverse;
                    edgePrimitives[eI] = (float)edgePrimitiveType;
                    edgeLengths[eI] = length;
                    edgeSizes[eI] = edgeSize;
                    edgeRoundings[eI] = edgeRounding;
                    edgeSmoothings[eI] = edgeSmoothing;
                    eI++;
                }
            }
            ActiveEdgeCount = eI;
        }

        /// <summary>
        /// Joint-local orientation for joint jI: raw incoming rotation (if
        /// supplied and enabled) -> per-joint correction from the topology's
        /// jointRotCorrections (== skeleton.py's per-joint euler correction
        /// applied via qmult(jointRotCorrections[jI], rotations[jI])) ->
        /// global offset. jointWorldPositions/Rotations are true world-space
        /// values (e.g. straight from Animator bone transforms) - this
        /// component's own GameObject transform is not folded in, so moving
        /// it around has no effect; reposition the whole scene via the
        /// renderer feature's sceneRoot instead if you need that.
        /// With no rotations supplied this collapses back to identity.
        /// </summary>
        private Quaternion ComputeJointRotation(int jI)
        {
            Quaternion raw = Quaternion.identity;
            if (useJointRotations && jointWorldRotations != null && jI < jointWorldRotations.Length)
                raw = jointWorldRotations[jI];

            Quaternion correction = Quaternion.identity;
            if (topology != null && topology.jointRotCorrections != null && jI < topology.jointRotCorrections.Length)
                correction = Quaternion.Euler(topology.jointRotCorrections[jI]);

            Quaternion corrected = correction * raw;
            return globalJointRotationOffset * corrected;
        }

        private void EnsureCapacity(int jointCount, int edgeCount)
        {
            if (jointInverseTransforms == null || jointInverseTransforms.Length < jointCount)
            {
                jointInverseTransforms = new Matrix4x4[jointCount];
                jointPrimitives = new float[jointCount];
                jointSizes = new Vector3[jointCount];
                jointRoundings = new float[jointCount];
                jointSmoothings = new float[jointCount];
            }

            if (edgeInverseTransforms == null || edgeInverseTransforms.Length < edgeCount)
            {
                edgeInverseTransforms = new Matrix4x4[edgeCount];
                edgePrimitives = new float[edgeCount];
                edgeLengths = new float[edgeCount];
                edgeSizes = new Vector3[edgeCount];
                edgeRoundings = new float[edgeCount];
                edgeSmoothings = new float[edgeCount];
            }
        }

        private void Reset()
        {
            if (topology != null && (jointWorldPositions == null || jointWorldPositions.Length != topology.JointCount))
            {
                jointWorldPositions = new Vector3[topology.JointCount];
            }

            if (topology != null && (jointWorldRotations == null || jointWorldRotations.Length != topology.JointCount))
            {
                jointWorldRotations = new Quaternion[topology.JointCount];
                for (int i = 0; i < jointWorldRotations.Length; i++)
                    jointWorldRotations[i] = Quaternion.identity;
            }
        }
    }
}
