# Raymarch Skeleton — Unity 6000.3 URP port

Port of the PREMIERE `RayMarching` Python/OpenGL tool (`raymarching.py`,
`visualization.py`, `skeleton.py`, `shaderVert.glsl`, `shaderFrag.glsl`)
to a Unity 6000.3 Universal Render Pipeline (Render Graph) full-screen
effect, extended to draw **two skeletons at once**.

I wasn't able to open the GitLab README you linked (it's a JS-rendered
GitLab blob page my browsing tool can't execute, and the repo isn't
indexed by search), so this port is built directly from the source
files you uploaded afterwards — `shaderFrag.glsl` is the actual ray
march method, and `skeleton.py` / `visualization.py` / `raymarching.py`
show how it's driven.

## What the original does

- `raymarching.py` opens a Qt/OpenGL window, loads a skeleton topology
  (`jointFilter` + `jointConnectivity`, e.g. `joint_settings.json`),
  and renders a full-screen quad with `shaderVert.glsl` / `shaderFrag.glsl`.
- `skeleton.py` turns joint positions/rotations into a **joint transform**
  per joint and an **edge transform** per bone (parent→child), stored as
  4×4 matrices that map a *world-space point into that joint/edge's local
  space* — the standard raymarching trick of transforming the sample
  point instead of the primitive.
- `shaderFrag.glsl` is a classic sphere-tracer:
  - `primitiveMorphSDF` / `primitiveMorphRippleSDF` pick (and can morph
    between) SDF primitives — sphere, round box, round capsule, round
    cylinder, plus ~14 fractal "objects" (truchet tower, apollonian,
    mandelbulb, menger sponge, julia, kaliBox, a "snake" SDF, …).
  - `sceneSDF_surface` unions all joints together (`poly_smin`), all
    edges together, blends joints+edges, then blends in up to 4 free
    "object" primitives — each blend also tracks a **Surface** (color +
    shading params) so materials smoothly interpolate where shapes merge.
  - `shortestDistanceToSurface_surface` is the actual march loop
    (255 steps max, epsilon-based hit test).
  - Shading = normal from the SDF gradient, Phong lighting, ray-marched
    soft shadows, ray-marched ambient occlusion, and linear distance fog.

## What's in this port

| Original (GLSL/Python) | Ported to |
|---|---|
| `shaderFrag.glsl` primitives, `poly_smin`, `union_surface`, `sceneSDF_surface`, march loop, normals, AO, soft shadow, Phong, fog | `Shaders/RaymarchSkeletonCore.hlsl` (near line-for-line, renamed to Unity/HLSL conventions) |
| `shaderVert.glsl` + Qt window/quad | `Shaders/RaymarchSkeleton.shader` — a URP full-screen triangle pass. Ray origin/direction are now reconstructed from the **real URP camera** (`_RM_InvViewProjection` / `_RM_CameraWorldPos`) instead of the original's separate `camPosition`/`camAngle` uniforms, so it behaves like a normal part of the scene and follows the Scene/Game camera. |
| `skeleton.py` joint/edge transform building | `Scripts/RaymarchSkeletonInstance.cs` — given live joint **positions** (and optionally rotations), computes the same "world→local" joint matrices and builds oriented capsule/cylinder edge transforms between parent and child joints, with edge length passed through the same way (`edgeLengths[eI] * edgeSizes[eI].z`). Edges always auto-orient from the parent→child direction (position-driven, like the majority of skeleton visualisations need); joints use `jointWorldRotations` if you supply them. |
| `joint_settings.json` / `jointFilter` / `jointConnectivity` / `jointRotCorrections` | `Scripts/SkeletonTopology.cs` — a `ScriptableObject` that parses the exact same JSON shape (including the optional `jointRotCorrections` block, converted from radians to degrees), so `joint_settings.json`, `hand_joint_settings.json`, `swarm_joint_settings.json`, or a full `xsens_joint_settings.json` with corrections can be reused as-is (see below). |
| `skeleton.py`'s per-joint rotation + `jointRotCorrections` + the hardcoded "+90° around Y" fixup | `RaymarchSkeletonInstance.jointWorldRotations` (per-joint, optional) + `SkeletonTopology.jointRotCorrections` (per-joint correction, from JSON) + `RaymarchSkeletonInstance.globalJointRotationOffset` (a proper Inspector-exposed knob replacing the hardcoded axis fixup — set it once per capture rig instead of editing shader code). |
| `visualization.py` (building & uploading uniform arrays every frame) | `Scripts/RaymarchSkeletonRendererFeature.cs` — a `ScriptableRendererFeature` using URP's Render Graph API, uploading flattened joint/edge arrays for **two** skeletons every frame. |
| `objectCount = 4` free/fractal primitives | Kept as a **shared, optional** 4-slot array (`_RM_Object*`) using the same 4 basic primitive types. All 4 slots are inactive by default (`primitive = -1`). |

### Two skeletons

Joints and edges are stored in flat shader arrays, laid out
back-to-back: `[skeleton 0 joints][skeleton 1 joints]`
(`MAX_JOINTS_PER_SKELETON = 40` per skeleton, same idea for edges).
Each skeleton gets its **own** color/ambient/diffuse/specular/occlusion
settings (`_RM_JointColor[2]`, `_RM_EdgeColor[2]`, …), so the two
performers can be tinted differently and still fully blend into one
continuous field where they touch (same `poly_smin` behaviour as the
original blending joints into edges into objects).

### What was intentionally left out

The ~14 fractal "object" SDFs (`apollonian1`, `mandelbulb_v2`, `julia`,
`mengerSponge`, `kaliBox`, `truchetTower`, the "snake" SDF, ripple
variants, `rayRotation`/`rayWiggle` screen-space ray distortion) were
**not** ported, to keep this a reviewable size — they aren't used for
the joints/edges themselves in the original (only for the 4 optional
"objects"), and GLSL → HLSL for them is close to a copy/paste (both use
the same `vec`/`mat` swizzle syntax; the main changes needed are
`mat4(...)` column-major literal syntax → `float4x4` and `mix` → `lerp`,
`fract` → `frac`). If you want them, drop the function bodies from
`shaderFrag.glsl` lines ~460–1230 into `RaymarchSkeletonCore.hlsl` and
extend `PrimitiveMorphSDF`'s primitive index chain (or a separate
`PrimitiveMorphRippleSDF` for the object slots) the same way the source
does.

## Setup in Unity

1. Copy `Shaders/` and `Scripts/` into your project's `Assets/` folder
   (e.g. `Assets/RaymarchSkeleton/`).
2. Create a Material from `Hidden/PREMIERE/RaymarchSkeleton` (it's
   marked `Hidden` on purpose since it's only meant to be driven by the
   renderer feature, not dropped onto a mesh).
3. On your URP renderer asset (`Project Settings → Graphics` → the
   active `Universal Renderer Data` asset), **Add Renderer Feature →
   Raymarch Skeleton**, and assign the material you just created.
4. Create two `SkeletonTopology` assets (or call
   `SkeletonTopology.FromJson(File.ReadAllText(...))` at runtime) from
   `joint_settings.json` / `hand_joint_settings.json` /
   `swarm_joint_settings.json` — they parse unchanged.
5. Add a `RaymarchSkeletonInstance` component to two GameObjects (one
   per performer), assign a topology, and each frame set
   `jointWorldPositions` from whatever now replaces
   `osc_control.py`/ZED/mocap input (an OSC receiver, a `Animator`'s
   bone transforms, etc.). If your source also gives joint rotations
   (e.g. `/mocap/0/joint/rot_world`), set `jointWorldRotations` too so
   box/capsule/cylinder joint primitives orient with the limb instead
   of staying axis-aligned; if your rig's axes come in rotated relative
   to Unity, set `globalJointRotationOffset` once rather than fighting
   individual joints.
6. Drag both instances into the renderer feature's `Skeleton A` /
   `Skeleton B` slots, plus a light `Transform` and background/fog
   settings.

Everything else — joint/edge primitive type, size, rounding, smoothing,
color, ambient/diffuse/specular, occlusion, and the joint↔edge blend
radius — is exposed as inspector fields on `RaymarchSkeletonInstance`,
matching the equivalent OSC-controllable uniforms in `visualization.py`.

## Notes / things to sanity-check in-editor

- `RaymarchSkeletonCore.hlsl` uses dynamic loops (`[loop]`) over fixed
  `MAX_JOINTS`/`MAX_EDGES`/`MAX_OBJECTS` bounds with an early `continue`
  on inactive slots (`primitive < 0`), mirroring the original's
  `if (jointPrimitives[jI] < 0) {} else {...}` pattern — this keeps cost
  proportional to *declared* max, not active count, same trade-off the
  original made.
- The march loop runs against `RM_MAX_DIST = 100` and `RM_MAX_STEPS = 255`,
  identical constants to the source; tune `RM_EPSILON` if you see banding.
- Soft shadows/AO both re-run `SceneSDF` per sample, so cost scales with
  how many joints/edges are active — same as the original.
