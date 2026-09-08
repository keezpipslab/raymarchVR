using UnityEngine;

namespace Premiere.RaymarchSkeleton
{
    /// <summary>
    /// Feeds a RaymarchSkeletonInstance from a Humanoid-rigged Animator every
    /// frame - this is the piece that replaces the original's OSC-delivered
    /// `/mocap/0/joint/pos_world` / `rot_world` messages when your motion
    /// comes from a Timeline (Animation track) driving an Animator instead.
    ///
    /// Ships with a built-in 21-joint default topology covering the common
    /// Humanoid bones (spine/head/arms/legs/toes), so you can drop this on
    /// an avatar and get a working skeleton without authoring a
    /// SkeletonTopology asset first. If you need a different joint set
    /// (fingers, twist bones, a subset for style), edit `DefaultBones` /
    /// `DefaultConnectivity` below, or assign your own SkeletonTopology to
    /// the paired RaymarchSkeletonInstance and set useBuiltInTopology=false.
    /// </summary>
    [RequireComponent(typeof(RaymarchSkeletonInstance))]
    [ExecuteAlways]
    public class RaymarchAvatarSource : MonoBehaviour
    {
        [Tooltip("The Humanoid Animator driven by your Timeline/mocap. If left empty, GetComponentInParent<Animator>() is used at OnEnable.")]
        public Animator avatarAnimator;

        [Tooltip("Use the built-in default humanoid joint set below. Turn off if you've assigned your own SkeletonTopology (matching your own HumanBodyBones list) to the RaymarchSkeletonInstance and want to drive it manually instead.")]
        public bool useBuiltInTopology = true;

        [Tooltip("Also push bone rotations into RaymarchSkeletonInstance.jointWorldRotations (needed only if you switch a joint's primitive away from Sphere to something orientation-sensitive, e.g. Box).")]
        public bool driveJointRotations = true;

        // The default joint set: index-for-index matches DefaultConnectivity.
        public static readonly HumanBodyBones[] DefaultBones =
        {
            HumanBodyBones.Hips,            // 0
            HumanBodyBones.Spine,           // 1
            HumanBodyBones.Chest,           // 2
            HumanBodyBones.Neck,            // 3
            HumanBodyBones.Head,            // 4
            HumanBodyBones.LeftShoulder,    // 5
            HumanBodyBones.LeftUpperArm,    // 6
            HumanBodyBones.LeftLowerArm,    // 7
            HumanBodyBones.LeftHand,        // 8
            HumanBodyBones.RightShoulder,   // 9
            HumanBodyBones.RightUpperArm,   // 10
            HumanBodyBones.RightLowerArm,   // 11
            HumanBodyBones.RightHand,       // 12
            HumanBodyBones.LeftUpperLeg,    // 13
            HumanBodyBones.LeftLowerLeg,    // 14
            HumanBodyBones.LeftFoot,        // 15
            HumanBodyBones.LeftToes,        // 16
            HumanBodyBones.RightUpperLeg,   // 17
            HumanBodyBones.RightLowerLeg,   // 18
            HumanBodyBones.RightFoot,       // 19
            HumanBodyBones.RightToes,       // 20
        };

        // children indices per joint above (parent -> children), same shape
        // as jointConnectivity in joint_settings.json.
        public static readonly int[][] DefaultConnectivity =
        {
            new[] { 1, 13, 17 }, // 0  Hips -> Spine, LeftUpperLeg, RightUpperLeg
            new[] { 2 },         // 1  Spine -> Chest
            new[] { 3, 5, 9 },   // 2  Chest -> Neck, LeftShoulder, RightShoulder
            new[] { 4 },         // 3  Neck -> Head
            System.Array.Empty<int>(), // 4  Head
            new[] { 6 },         // 5  LeftShoulder -> LeftUpperArm
            new[] { 7 },         // 6  LeftUpperArm -> LeftLowerArm
            new[] { 8 },         // 7  LeftLowerArm -> LeftHand
            System.Array.Empty<int>(), // 8  LeftHand
            new[] { 10 },        // 9  RightShoulder -> RightUpperArm
            new[] { 11 },        // 10 RightUpperArm -> RightLowerArm
            new[] { 12 },        // 11 RightLowerArm -> RightHand
            System.Array.Empty<int>(), // 12 RightHand
            new[] { 14 },        // 13 LeftUpperLeg -> LeftLowerLeg
            new[] { 15 },        // 14 LeftLowerLeg -> LeftFoot
            new[] { 16 },        // 15 LeftFoot -> LeftToes
            System.Array.Empty<int>(), // 16 LeftToes
            new[] { 18 },        // 17 RightUpperLeg -> RightLowerLeg
            new[] { 19 },        // 18 RightLowerLeg -> RightFoot
            new[] { 20 },        // 19 RightFoot -> RightToes
            System.Array.Empty<int>(), // 20 RightToes
        };

        private RaymarchSkeletonInstance _instance;
        private Transform[] _bones;
        private SkeletonTopology _runtimeTopology;

        private void OnEnable()
        {
            _instance = GetComponent<RaymarchSkeletonInstance>();

            if (avatarAnimator == null)
                avatarAnimator = GetComponentInParent<Animator>();

            if (useBuiltInTopology)
            {
                _runtimeTopology = ScriptableObject.CreateInstance<SkeletonTopology>();
                _runtimeTopology.jointFilter = new int[DefaultBones.Length];
                for (int i = 0; i < DefaultBones.Length; i++) _runtimeTopology.jointFilter[i] = i;
                _runtimeTopology.jointConnectivity = DefaultConnectivity;
                _instance.topology = _runtimeTopology;

                CacheBones();
            }
        }

        private void CacheBones()
        {
            if (avatarAnimator == null || !avatarAnimator.isHuman)
            {
                _bones = null;
                return;
            }

            _bones = new Transform[DefaultBones.Length];
            for (int i = 0; i < DefaultBones.Length; i++)
                _bones[i] = avatarAnimator.GetBoneTransform(DefaultBones[i]);
        }

        // LateUpdate so this reads bone poses *after* Timeline/Animator has
        // applied the current frame's mocap pose.
        private void LateUpdate()
        {
            if (!useBuiltInTopology || avatarAnimator == null) return;

            if (_bones == null) CacheBones();
            if (_bones == null) return; // not a Humanoid avatar yet (still loading, or mis-set up)

            int count = _bones.Length;
            if (_instance.jointWorldPositions == null || _instance.jointWorldPositions.Length != count)
                _instance.jointWorldPositions = new Vector3[count];
            if (driveJointRotations && (_instance.jointWorldRotations == null || _instance.jointWorldRotations.Length != count))
                _instance.jointWorldRotations = new Quaternion[count];

            for (int i = 0; i < count; i++)
            {
                Transform bone = _bones[i];
                if (bone == null) continue; // this humanoid rig doesn't have this optional bone (e.g. no toes)

                _instance.jointWorldPositions[i] = bone.position;
                if (driveJointRotations) _instance.jointWorldRotations[i] = bone.rotation;
            }
        }

        private void OnDisable()
        {
            if (_runtimeTopology != null)
            {
                if (Application.isPlaying) Destroy(_runtimeTopology);
                else DestroyImmediate(_runtimeTopology);
                _runtimeTopology = null;
            }
        }
    }
}
