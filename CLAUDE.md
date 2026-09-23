# CLAUDE.md — Raymarch Skeleton (URP, Unity 6000.3)

This project renders **two mocap-driven avatars** (animated by Timeline)
as a single continuous raymarched/SDF surface, instead of their normal
skinned meshes. It's a Unity URP port of a Python/OpenGL tool
(`PREMIERE / AI-Toolbox / MotionVisualisation / RayMarching`) used for
abstract motion visualisation research.

If you're picking this up in a fresh session: read this file first — it's
the sole source of truth for this project (no README.md exists despite
older notes referencing one).

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
7. Optionally, `RaymarchCompositionInstance` (one per avatar, same
   pattern as `RaymarchAvatarSource`) overlays extra body-anchored SDF
   primitives — sphere/box/capsule/pyramid/torus/roundbox/cone/
   octahedron/hexagonal prism/cylinder/triangular prism/link — exported
   from a companion (non-Unity) project's `CompositionExporter` as JSON,
   re-anchored every frame to this avatar's live Humanoid pose. See
   "Composition overlays" below.

## File map (`Assets/RaymarchSkeletonURP/`)

```
Shaders/
  RaymarchSkeletonCore.hlsl        - SDF primitives, blending, march loop, shading (the actual "raymarching")
  RaymarchSkeleton.shader          - full-screen URP pass wrapping the .hlsl, reconstructs camera ray
Scripts/
  RaymarchPrimitive.cs             - enum: Sphere/Box/Capsule/Cylinder (matches HLSL primitive index 0-3, joints/edges only)
  SkeletonTopology.cs              - ScriptableObject: joint list + parent->child connectivity (+ optional per-joint rotation corrections), parses original *_joint_settings.json shape
  RaymarchSkeletonInstance.cs      - one skeleton's per-frame joint/edge transform + look data (feed jointWorldPositions/Rotations)
  RaymarchAvatarSource.cs          - drives a RaymarchSkeletonInstance from a Humanoid Animator's bones; ships a built-in 21-joint default topology
  RaymarchSkeletonRendererFeature.cs - URP renderer feature; gathers both skeletons + composition overlays/frame, uploads shader arrays, issues the full-screen draw
  RaymarchSkeletonUI.cs            - runtime (F1) IMGUI control panel bound to the feature + both instances
  RaymarchSkeletonBinder.cs        - pushes scene references (skeletons, composition overlays, light) onto the feature asset
  RaymarchCompositionPrimitive.cs  - enum: the 12 composition-overlay SDF kinds (matches HLSL primitive index 0-11, objects only)
  RaymarchCompositionData.cs       - JsonUtility DTOs for CompositionExporter's JSON export shape
  RaymarchCompositionInstance.cs   - resolves a composition JSON's elements against a Humanoid Animator's live pose every frame
Scenes/
  CompositionDemo.unity            - ray.unity + one RaymarchCompositionInstance wired to FiguurA, loading SampleComposition.json
SampleComposition.json             - example CompositionExporter export, used by CompositionDemo.unity
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
   **Skeleton A**, **Skeleton B**, **Light Source**, and (optionally)
   **Composition A** / **Composition B** fields there — all scene-to-
   scene / scene-to-asset references, so normal drag-and-drop works. It
   pushes those references onto the feature in code (`OnEnable`/
   `OnValidate`) once the scene loads.
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
  `MAX_EDGES_PER_SKELETON = 40`, `SKELETON_COUNT = 2`, `MAX_OBJECTS = 64`
  (top of `RaymarchSkeletonCore.hlsl`, mirrored as consts in
  `RaymarchSkeletonRendererFeature.cs`). The built-in humanoid topology
  uses 21 joints / 20 edges, well under the cap; `MAX_OBJECTS` is the
  shared cap across *both* composition overlays' elements combined
  (`compositionA` + `compositionB`, first-come-first-served if you
  exceed it). If you change these constants, change them **in both
  files** and in any place that allocates matching arrays.
- **This only supports exactly two skeletons** by design (array layout,
  UI, feature fields are all hardcoded to A/B). Supporting N skeletons
  would mean switching the flat arrays to a StructuredBuffer instead of
  fixed-size shader arrays — a bigger change, not a config tweak.
- Adding a third+ avatar today = it just won't render (no slot for it).

## Composition overlays (body-anchored primitives from an external export)

A companion (non-Unity) project's `CompositionExporter` can export a JSON
file of primitives rigged to a performer's body — one file, top level
`{ "exportedAtUtc": ..., "elements": [...] }`, each element
`{ "kind", "size", "anchor", "localPosition", "localRotation" }`. This
project reconstructs those against its own Humanoid rig at runtime,
instead of requiring a matching skeleton representation in both projects:

- `anchor` is either `Joint_{BoneName}` (`BoneName` is a `HumanBodyBones`
  member name directly, e.g. `Joint_LeftLowerArm`) or
  `Bone_{From}_{To}` (a limb segment, anchored at the midpoint between
  two bones, oriented the same way `RaymarchSkeletonInstance` orients
  edges — parent→child `LookRotation` with world-up).
- `RaymarchCompositionInstance.TryResolveAnchor()` does this resolution
  every frame from `avatarAnimator.GetBoneTransform(...)`, so it tracks
  Timeline/mocap playback exactly like `RaymarchAvatarSource` does for
  joints/edges. `worldPos = anchorWorldPos + anchorWorldRot *
  localPosition`, `worldRot = anchorWorldRot * localRotation`, matching
  the exporting project's own reconstruction formula.
- `kind` is parsed via `Enum.TryParse<RaymarchCompositionPrimitive>`, so
  its 12 names must keep matching `CompositionExporter`'s `PrimitiveKind`
  enum exactly. These are dispatched in `CompositionPrimitiveSDF()`
  (`RaymarchSkeletonCore.hlsl`) — a plain switch, not the joint/edge
  path's fractional-morph `PrimitiveMorphSDF()`.
- The exported JSON carries no rounding value, so `RoundBox` elements all
  share one `RaymarchCompositionInstance.roundBoxRounding` value; `Box`
  elements are always sharp.
- Composition elements piggyback on the shader's existing (previously
  unused) shared "object" arrays — `RaymarchSkeletonRendererFeature`
  gathers `compositionA`/`compositionB` and flattens their elements into
  those arrays every frame, alongside both skeletons' joints/edges.
- Primitive parameter mapping (all local space, Z is the long/height axis
  — matching `RoundCylinderSDF`/`RoundCapsuleSDF`'s existing XY-radial/Z
  convention, *not* the original GLSL tool's Y-axis convention): Sphere
  `size.x`=radius; Box/RoundBox `size.xyz`=full extents; Capsule/Cylinder
  `size.x`=radius, `size.z`=full length; Torus `size.x`=major radius,
  `size.y`=tube radius; Octahedron `size.x`=scale; HexagonalPrism/
  TriangularPrism `size.x`=radius/size, `size.z`=full depth;
  Cone/Pyramid `size.x`=base radius/width, `size.z`=full height; Link
  `size.x`=ring radius, `size.y`=tube radius, `size.z`=full stretch
  length. This mapping was chosen for internal consistency (not carried
  over from the original tool, which isn't in this repo) — adjust freely
  if it doesn't match the exporting project's visual intent.
- `CompositionDemo.unity` demonstrates the whole path: a
  `RaymarchCompositionInstance` on a `CompositionA` GameObject, pointed
  at `FiguurA`'s Animator, loading `SampleComposition.json`.

## Known gaps vs. the original tool (see README.md for detail)

- No OSC control layer (the original was driven live via Max/MSP OSC
  messages — see the original README's `/vis/...` protocol). All
  control here is either Inspector fields or the F1 runtime UI.
- No ripple deformation on the composition/"object" primitives
  (`objectamplitude`/`frequency`/`phase` in the original).
- No fractal primitives (apollonian, mandelbulb, menger sponge, julia,
  kaliBox, truchet tower, "snake", …) anywhere — joints/edges are still
  limited to sphere/box/capsule/cylinder (`RaymarchPrimitive`); the
  composition/"object" path supports a wider set (see above) but still
  not the fractals.
- No `rayrotation`/`raywiggle` full-scene ray-distortion effects.

## Testing changes

There's no automated test suite (it's a rendering effect) — verify
changes by pressing Play with the Timeline scrubbing/playing and
watching the Game view; the F1 panel is the fastest way to confirm a
shader/uniform change actually reaches the material (e.g. push a slider
to an extreme and confirm the visual responds).
