using UnityEngine;
using UnityEngine.InputSystem;

namespace Premiere.RaymarchSkeleton
{
    /// <summary>
    /// Runtime control panel for RaymarchSkeletonRendererFeature + its two
    /// RaymarchSkeletonInstance skeletons. Pure IMGUI (OnGUI) so it needs no
    /// Canvas/UIDocument/prefab setup - drop this on any GameObject, assign
    /// the feature + skeletons, hit Play.
    ///
    /// This is meant as a working default / debugging tool, not final
    /// production UI - swap in UGUI/UI Toolkit bound to the same
    /// feature/instance fields later if you want a polished in-app panel;
    /// the fields it reads/writes are the same ones you'd bind either way.
    /// </summary>
    public class RaymarchSkeletonUI : MonoBehaviour
    {
        public RaymarchSkeletonRendererFeature feature;
        public RaymarchSkeletonInstance skeletonA;
        public RaymarchSkeletonInstance skeletonB;

        [Tooltip("Key that shows/hides the panel.")]
        public Key toggleKey = Key.F1;

        [Tooltip("Key that turns raymarching itself on/off, without needing the panel open.")]
        public Key raymarchToggleKey = Key.F2;

        [Tooltip("Key that shows/hides both skeletons' base joints+edges at once, leaving only the composition overlay primitives.")]
        public Key skeletonBaseToggleKey = Key.F3;

        public bool visible = true;

        private Vector2 _scroll;
        private Rect _windowRect = new Rect(20, 20, 380, 700);
        private int _skelTab; // 0 = A, 1 = B

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (IsValidKey(toggleKey) && keyboard[toggleKey].wasPressedThisFrame) visible = !visible;
            if (IsValidKey(raymarchToggleKey) && keyboard[raymarchToggleKey].wasPressedThisFrame && feature != null) feature.raymarchEnabled = !feature.raymarchEnabled;
            if (IsValidKey(skeletonBaseToggleKey) && keyboard[skeletonBaseToggleKey].wasPressedThisFrame) ToggleSkeletonBases();
        }

        /// <summary>Hides both skeletons' joints+edges if any are visible, otherwise shows them all again.</summary>
        private void ToggleSkeletonBases()
        {
            bool anyVisible = IsBaseVisible(skeletonA) || IsBaseVisible(skeletonB);
            SetBaseVisible(skeletonA, !anyVisible);
            SetBaseVisible(skeletonB, !anyVisible);
        }

        private static bool IsBaseVisible(RaymarchSkeletonInstance inst) => inst != null && (inst.showJoints || inst.showEdges);

        private static void SetBaseVisible(RaymarchSkeletonInstance inst, bool show)
        {
            if (inst == null) return;
            inst.showJoints = show;
            inst.showEdges = show;
        }

        private static bool IsValidKey(Key key) => key > Key.None && key <= Key.OEM5;

        // Scenes saved while these fields were legacy KeyCodes still hold
        // KeyCode numbers (e.g. F1 = 282), which aren't valid Input System
        // Keys - fall back to the defaults instead of silently ignoring them.
        // Key.None stays None, so a key can still be deliberately disabled.
        private void OnValidate()
        {
            if (toggleKey != Key.None && !IsValidKey(toggleKey)) toggleKey = Key.F1;
            if (raymarchToggleKey != Key.None && !IsValidKey(raymarchToggleKey)) raymarchToggleKey = Key.F2;
            if (skeletonBaseToggleKey != Key.None && !IsValidKey(skeletonBaseToggleKey)) skeletonBaseToggleKey = Key.F3;
        }

        private void Awake() => OnValidate();

        private void OnGUI()
        {
            if (!visible) return;
            _windowRect = GUILayout.Window(GetInstanceID(), _windowRect, DrawWindow, "Raymarch Skeleton Settings");
        }

        private void DrawWindow(int id)
        {
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(660));

            DrawGlobalSection();
            GUILayout.Space(8);

            _skelTab = GUILayout.Toolbar(_skelTab, new[] { "Skeleton A", "Skeleton B" });
            var inst = _skelTab == 0 ? skeletonA : skeletonB;
            DrawSkeletonSection(inst);

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void DrawGlobalSection()
        {
            GUILayout.Label("Global", GUI.skin.box);

            if (feature == null)
            {
                GUILayout.Label("Assign a RaymarchSkeletonRendererFeature to control global settings.");
                return;
            }

            feature.raymarchEnabled = GUILayout.Toggle(feature.raymarchEnabled, $"Raymarching Enabled ({raymarchToggleKey})");
            feature.maxSteps = Mathf.RoundToInt(LabeledSlider("Max Steps", feature.maxSteps, 8f, 255f));
            feature.monoInVR = GUILayout.Toggle(feature.monoInVR, "Mono in VR (compute once, duplicate to both eyes)");
            feature.shadowStrength = LabeledSlider("Shadow Strength", feature.shadowStrength, 0f, 1f);
            feature.shadowSmooth = LabeledSlider("Shadow Smooth", feature.shadowSmooth, 1f, 64f);

            feature.fogMinDist = LabeledSlider("Fog Min Dist", feature.fogMinDist, 0f, 50f);
            feature.fogMaxDist = LabeledSlider("Fog Max Dist", feature.fogMaxDist, 0f, 100f);

            feature.skelObjectSmoothing = LabeledSlider("Skeleton/Object Smoothing", feature.skelObjectSmoothing, 0f, 1f);

            GUILayout.Label("Skeleton A/B Combine");
            feature.skeletonCombineOp = SkeletonCombineOpToolbar(feature.skeletonCombineOp);
            feature.skeletonCombineSmoothing = LabeledSlider("Skeleton A/B Smoothing", feature.skeletonCombineSmoothing, 0f, 1f);

            GUILayout.Label("Background Color");
            feature.backgroundColor = ColorSliders(feature.backgroundColor);

            GUILayout.Label("Background Occlusion Color");
            feature.backgroundOcclusionColor = ColorSliders(feature.backgroundOcclusionColor);

            if (feature.lightSource != null)
            {
                GUILayout.Label($"Light: {feature.lightSource.name} (drag its Transform in-scene to move it)");
            }
            else
            {
                GUILayout.Label("No light Transform assigned - using a fallback position above the camera.");
            }
        }

        private void DrawSkeletonSection(RaymarchSkeletonInstance inst)
        {
            if (inst == null)
            {
                GUILayout.Label("Assign this skeleton slot on the RaymarchSkeletonUI component.");
                return;
            }

            GUILayout.Space(6);
            GUILayout.Label($"Visibility (both skeletons: {skeletonBaseToggleKey})", GUI.skin.box);
            inst.showJoints = GUILayout.Toggle(inst.showJoints, "Show joints");
            inst.showEdges = GUILayout.Toggle(inst.showEdges, "Show edges (bones)");

            GUILayout.Space(6);
            GUILayout.Label("Joints", GUI.skin.box);
            inst.jointPrimitiveType = PrimitiveToolbar(inst.jointPrimitiveType);
            inst.jointSize = Vector3Sliders("Size", inst.jointSize, 0.001f, 0.5f);
            inst.jointRounding = LabeledSlider("Rounding", inst.jointRounding, 0f, 0.2f);
            inst.jointSmoothing = LabeledSlider("Smoothing", inst.jointSmoothing, 0.001f, 0.3f);
            GUILayout.Label("Color");
            inst.jointColor = ColorSliders(inst.jointColor);
            inst.jointAmbientScale = LabeledSlider("Ambient", inst.jointAmbientScale, 0f, 2f);
            inst.jointDiffuseScale = LabeledSlider("Diffuse", inst.jointDiffuseScale, 0f, 2f);
            inst.jointSpecularScale = LabeledSlider("Specular", inst.jointSpecularScale, 0f, 4f);
            inst.jointSpecularPow = LabeledSlider("Specular Pow", inst.jointSpecularPow, 1f, 128f);
            inst.jointOcclusionScale = LabeledSlider("Occlusion Scale", inst.jointOcclusionScale, 0f, 1f);
            inst.jointOcclusionRange = LabeledSlider("Occlusion Range", inst.jointOcclusionRange, 0.01f, 2f);
            inst.jointOcclusionResolution = LabeledSlider("Occlusion Resolution", inst.jointOcclusionResolution, 0.01f, 0.5f);

            GUILayout.Space(6);
            GUILayout.Label("Edges (bones)", GUI.skin.box);
            inst.edgePrimitiveType = PrimitiveToolbar(inst.edgePrimitiveType);
            inst.edgeSize = Vector3Sliders("Size (z = length mult.)", inst.edgeSize, 0.001f, 0.5f);
            inst.edgeRounding = LabeledSlider("Rounding", inst.edgeRounding, 0f, 0.2f);
            inst.edgeSmoothing = LabeledSlider("Smoothing", inst.edgeSmoothing, 0.001f, 0.3f);
            GUILayout.Label("Color");
            inst.edgeColor = ColorSliders(inst.edgeColor);
            inst.edgeAmbientScale = LabeledSlider("Ambient", inst.edgeAmbientScale, 0f, 2f);
            inst.edgeDiffuseScale = LabeledSlider("Diffuse", inst.edgeDiffuseScale, 0f, 2f);
            inst.edgeSpecularScale = LabeledSlider("Specular", inst.edgeSpecularScale, 0f, 4f);
            inst.edgeSpecularPow = LabeledSlider("Specular Pow", inst.edgeSpecularPow, 1f, 128f);
            inst.edgeOcclusionScale = LabeledSlider("Occlusion Scale", inst.edgeOcclusionScale, 0f, 1f);
            inst.edgeOcclusionRange = LabeledSlider("Occlusion Range", inst.edgeOcclusionRange, 0.01f, 2f);
            inst.edgeOcclusionResolution = LabeledSlider("Occlusion Resolution", inst.edgeOcclusionResolution, 0.01f, 0.5f);

            GUILayout.Space(6);
            inst.jointEdgeSmoothing = LabeledSlider("Joint/Edge Blend", inst.jointEdgeSmoothing, 0f, 0.5f);

            GUILayout.Space(6);
            inst.useJointRotations = GUILayout.Toggle(inst.useJointRotations, "Use bone rotations for joint orientation");

            GUILayout.Space(6);
            inst.invert = GUILayout.Toggle(inst.invert, "Invert (negate) - turn into cavity/void");
        }

        // --- small IMGUI helpers ---

        private static RaymarchPrimitive PrimitiveToolbar(RaymarchPrimitive current)
        {
            string[] names = { "Sphere", "Box", "Capsule", "Cylinder" };
            int idx = GUILayout.Toolbar((int)current, names);
            return (RaymarchPrimitive)idx;
        }

        private static SkeletonCombineOp SkeletonCombineOpToolbar(SkeletonCombineOp current)
        {
            string[] names = { "Union (+)", "Subtract (-)", "Intersect (∩)", "XOR (%)" };
            int idx = GUILayout.Toolbar((int)current, names);
            return (SkeletonCombineOp)idx;
        }

        private static float LabeledSlider(string label, float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(150));
            float result = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(140));
            GUILayout.Label(result.ToString("0.000"), GUILayout.Width(60));
            GUILayout.EndHorizontal();
            return result;
        }

        private static Vector3 Vector3Sliders(string label, Vector3 value, float min, float max)
        {
            GUILayout.Label(label);
            value.x = LabeledSlider("x", value.x, min, max);
            value.y = LabeledSlider("y", value.y, min, max);
            value.z = LabeledSlider("z", value.z, min, max);
            return value;
        }

        private static Color ColorSliders(Color c)
        {
            c.r = LabeledSlider("r", c.r, 0f, 1f);
            c.g = LabeledSlider("g", c.g, 0f, 1f);
            c.b = LabeledSlider("b", c.b, 0f, 1f);
            return c;
        }
    }
}
