# CLAUDE.md — Raymarch Skeleton (URP, Unity 6000.3)

This project renders **two mocap-driven avatars** (animated by Timeline)
as a single continuous raymarched/SDF surface, instead of their normal
skinned meshes. It's a Unity URP port of a Python/OpenGL tool
(`PREMIERE / AI-Toolbox / MotionVisualisation / RayMarching`) used for
abstract motion visualisation research.

If you're picking this up in a fresh session: read this file first, then
`Assets/RaymarchSkeleton/README.md` for the deep technical mapping back
to the original Python/GLSL source (what was ported 1:1, what was
simplified, what's still missing).

## What's actually happening, at a glance

1. Two avatars in the scene are driven by a **Timeline** (Animation
   tracks) into their **Humanoid Animator**, as normal.
2. `RaymarchAvatarSource` (one per avatar) reads that Animator's bone
   Transforms every `LateUpdate` (after Timeline/Animator have applied
   the frame's pose) into a paired `RaymarchSkeletonInstance`.
3. `RaymarchSkeletonInstance` turns those joint positions (+ optional
   rotations) into per-joint/per-edge SDF primitive data (inverse world
   matrices, primitive type, size, rounding, smoothing).
4. `RaymarchSkeletonRendererFeature` (a URP `ScriptableRendererFeature`,
   Render Graph API) gathers both instances every frame and uploads
   everything as shader arrays to `Hidden/PREMIERE/RaymarchSkeleton`.
5. That shader does the actual sphere-tracing full-screen, using the
   real scene camera, and composites the result into the camera's color
   target — **it does not render the avatars' skinned meshes**, it
   renders an SDF field built from their bone positions.
6. `RaymarchSkeletonUI` is a runtime IMGUI panel (toggle: **F1**) to
   live-tune every look/lighting parameter per skeleton, for finding
   good settings while scrubbing the Timeline.

## File map (`Assets/RaymarchSkeleton/`)

```
Shaders/
  RaymarchSkeletonCore.hlsl        - SDF primitives, blending, march loop, shading (the actual "raymarching")
  RaymarchSkeleton.shader          - full-screen URP pass wrapping the .hlsl, reconstructs camera ray
Scripts/
  RaymarchPrimitive.cs             - enum: Sphere/Box/Capsule/Cylinder (matches HLSL primitive index 0-3)
  SkeletonTopology.cs              - ScriptableObject: joint list + parent->child connectivity (+ optional per-joint rotation corrections), parses original *_joint_settings.json shape
  RaymarchSkeletonInstance.cs      - one skeleton's per-frame joint/edge transform + look data (feed jointWorldPositions/Rotations)
  RaymarchAvatarSource.cs          - drives a RaymarchSkeletonInstance from a Humanoid Animator's bones; ships a built-in 21-joint default topology
  RaymarchSkeletonRendererFeature.cs - URP renderer feature; gathers both skeletons/frame, uploads shader arrays, issues the full-screen draw
  RaymarchSkeletonUI.cs            - runtime (F1) IMGUI control panel bound to the feature + both instances
README.md                          - full technical mapping back to the original Python/GLSL tool
```

## Scene setup (already done if you're reading this from within the
project, but here's the reference if something needs re-wiring)

1. **URP Renderer asset** → Renderer Features → `RaymarchSkeletonRendererFeature`
   is added, with `Raymarch Material` set to a material using
   `Hidden/PREMIERE/RaymarchSkeleton`.
2. **Two avatar GameObjects**, each: Humanoid `Animator` (Timeline
   target) + a child/sibling object holding `RaymarchSkeletonInstance` +
   `RaymarchAvatarSource` (the latter needs `avatarAnimator` pointing at
   the Humanoid Animator).
3. The renderer feature is a sub-asset of your URP Renderer Data asset
   (a Project asset), while `RaymarchSkeletonInstance` lives on scene
   GameObjects — Unity refuses to let an asset field hold a direct
   scene-object reference, so dragging a skeleton straight into the
   feature's **Skeleton A / Skeleton B** slots in the Inspector fails
   with "Type Mismatch". Instead, add a `RaymarchSkeletonBinder`
   component (any GameObject in the scene) and assign its **Feature**,
   **Skeleton A**, **Skeleton B**, and **Light Source** fields there —
   all scene-to-scene / scene-to-asset references, so normal drag-and-
   drop works. It pushes those references onto the feature in code
   (`OnEnable`/`OnValidate`) once the scene loads.
4. Light Source is optional on the binder — leave it unassigned and the
   feature falls back to a position above the camera.
5. `RaymarchSkeletonUI` sits on any GameObject, with `feature`,
   `skeletonA`, `skeletonB` assigned. Press **F1** in Play mode to open
   the panel.

## Conventions / gotchas for future changes

- **World space, not local.** `RaymarchSkeletonInstance.jointWorldPositions`
  / `jointWorldRotations` are literal world-space values — the
  component's own GameObject transform is *not* folded in. If you need
  to reposition/rotate the whole raymarched scene, use the renderer
  feature's `sceneRoot` Transform (maps to `_RM_SceneTransform`), not
  the instance's transform.
- **Edges auto-orient from position**, not from bone rotation — a bone's
  capsule always points from parent joint to child joint. Bone rotations
  (`jointWorldRotations`) only affect *joint* primitives, and only
  matter visually once you switch a joint off `Sphere` (spheres are
  rotation-invariant).
- **Two skeletons look identical by default** (same C# class defaults
  for both instances) — deliberately, so nothing is hardcoded to "player
  1 is orange." Give Skeleton A/B distinct `jointColor`/`edgeColor` in
  the Inspector or via the F1 panel so they're visually distinguishable
  where they overlap/blend.
- **Array sizes are fixed at compile time**: `MAX_JOINTS_PER_SKELETON = 40`,
  `MAX_EDGES_PER_SKELETON = 40`, `SKELETON_COUNT = 2`, `MAX_OBJECTS = 4`
  (top of `RaymarchSkeletonCore.hlsl`, mirrored as consts in
  `RaymarchSkeletonRendererFeature.cs`). The built-in humanoid topology
  uses 21 joints / 20 edges, well under the cap. If you change these
  constants, change them **in both files** and in any place that
  allocates matching arrays.
- **This only supports exactly two skeletons** by design (array layout,
  UI, feature fields are all hardcoded to A/B). Supporting N skeletons
  would mean switching the flat arrays to a StructuredBuffer instead of
  fixed-size shader arrays — a bigger change, not a config tweak.
- Adding a third+ avatar today = it just won't render (no slot for it).

## Known gaps vs. the original tool (see README.md for detail)

- No OSC control layer (the original was driven live via Max/MSP OSC
  messages — see the original README's `/vis/...` protocol). All
  control here is either Inspector fields or the F1 runtime UI.
- No ripple deformation on the optional "object" primitives
  (`objectamplitude`/`frequency`/`phase` in the original).
- No fractal object primitives (apollonian, mandelbulb, menger sponge,
  julia, kaliBox, truchet tower, "snake", …) — only sphere/box/capsule/
  cylinder, for joints, edges, and objects alike.
- No `rayrotation`/`raywiggle` full-scene ray-distortion effects.

## Testing changes

There's no automated test suite (it's a rendering effect) — verify
changes by pressing Play with the Timeline scrubbing/playing and
watching the Game view; the F1 panel is the fastest way to confirm a
shader/uniform change actually reaches the material (e.g. push a slider
to an extreme and confirm the visual responds).
