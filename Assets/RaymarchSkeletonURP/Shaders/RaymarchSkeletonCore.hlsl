#ifndef RAYMARCH_SKELETON_CORE_INCLUDED
#define RAYMARCH_SKELETON_CORE_INCLUDED

// ---------------------------------------------------------------------
// Port of RayMarching/shaderFrag.glsl (PREMIERE MotionVisualisation) to
// HLSL for Unity 6000.3 URP.
//
// Only the parts of the original shader that actually drive the
// skeleton visualisation were ported 1:1 (SDF primitives used for
// joints/edges, poly-min smoothing/blending, Phong lighting, soft
// shadows, ray-marched ambient occlusion, distance fog). The exotic
// "object" fractals (apollonian, mandelbulb, menger sponge, kaliBox,
// julia, snake, truchet tower, ...) were left out to keep this file a
// manageable size - they can be dropped in exactly the same way, since
// GLSL and HLSL vector math is nearly identical (see the "Extending"
// section in the README).
//
// Two skeletons are supported out of the box: joints/edges are laid
// out back-to-back in flat arrays, MAX_JOINTS_PER_SKELETON /
// MAX_EDGES_PER_SKELETON apart, and each skeleton has its own color +
// shading parameters so the two performers can be told apart visually.
// ---------------------------------------------------------------------

#define SKELETON_COUNT 2
#define MAX_JOINTS_PER_SKELETON 40
#define MAX_EDGES_PER_SKELETON 40
#define MAX_JOINTS (MAX_JOINTS_PER_SKELETON * SKELETON_COUNT)
#define MAX_EDGES (MAX_EDGES_PER_SKELETON * SKELETON_COUNT)
#define MAX_OBJECTS 4

#define RM_MIN_DIST 0.0
#define RM_MAX_DIST 100.0
#define RM_EPSILON 0.0001

// -----------------------------------------------------------------
// Uniforms (set from RaymarchSkeletonRendererFeature.cs)
// -----------------------------------------------------------------

// quality - max sphere-tracing iterations per ray. Runtime-tunable (rather
// than a compile-time constant) so VR/perf-constrained targets can dial it
// down without a shader recompile. An unset/zero value (e.g. before the
// renderer feature has ever run) is safe: the march loop below simply never
// iterates and every ray resolves to background, same as running out of
// steps normally does.
int _RM_MaxSteps;

// light settings
float3 _RM_LightPosition;
float _RM_ShadowSmooth;
float _RM_ShadowStrength;

// background
float3 _RM_BgColor;
float3 _RM_BgOcclusionColor;

// fog
float _RM_FogMinDist;
float _RM_FogMaxDist;

// scene transform (applied once before evaluating any primitive, matches
// `sceneTransform` in the original shader - lets you rotate/scale the
// whole raymarched scene without touching every joint/edge matrix)
float4x4 _RM_SceneTransform;

// combined smoothing between joints/edges and between skeleton/objects
float _RM_JointEdgeSmoothing[SKELETON_COUNT];
float _RM_SkelObjectSmoothing;

// boolean operator combining Skeleton A (index 0) and Skeleton B (index 1):
// 0=Union, 1=Subtract (A-B), 2=Intersect, 3=SymmetricDifference. Must match
// SkeletonCombineOp.cs's int values.
int _RM_SkeletonCombineOp;
float _RM_SkeletonCombineSmoothing;
// per-skeleton: non-zero flips that skeleton's signed distance (cavity/void)
// before it's combined with the other skeleton.
float _RM_SkeletonInvert[SKELETON_COUNT];

// per-skeleton joint look
float3 _RM_JointColor[SKELETON_COUNT];
float _RM_JointAmbientScale[SKELETON_COUNT];
float _RM_JointDiffuseScale[SKELETON_COUNT];
float _RM_JointSpecularScale[SKELETON_COUNT];
float _RM_JointSpecularPow[SKELETON_COUNT];
float _RM_JointOcclusionScale[SKELETON_COUNT];
float _RM_JointOcclusionRange[SKELETON_COUNT];
float _RM_JointOcclusionResolution[SKELETON_COUNT];
float3 _RM_JointOcclusionColor[SKELETON_COUNT];

// per-skeleton edge look
float3 _RM_EdgeColor[SKELETON_COUNT];
float _RM_EdgeAmbientScale[SKELETON_COUNT];
float _RM_EdgeDiffuseScale[SKELETON_COUNT];
float _RM_EdgeSpecularScale[SKELETON_COUNT];
float _RM_EdgeSpecularPow[SKELETON_COUNT];
float _RM_EdgeOcclusionScale[SKELETON_COUNT];
float _RM_EdgeOcclusionRange[SKELETON_COUNT];
float _RM_EdgeOcclusionResolution[SKELETON_COUNT];
float3 _RM_EdgeOcclusionColor[SKELETON_COUNT];

// joints, flattened [skeleton0 joints..][skeleton1 joints..]
// _RM_JointTransforms is the INVERSE of the joint's world matrix -
// it transforms a world-space sample point into joint-local space
// (equivalent to the numpy transpose(...) matrices in skeleton.py).
float4x4 _RM_JointTransforms[MAX_JOINTS];
float _RM_JointPrimitives[MAX_JOINTS];   // <0 = inactive/unused slot
float3 _RM_JointSizes[MAX_JOINTS];
float _RM_JointRoundings[MAX_JOINTS];
float _RM_JointSmoothings[MAX_JOINTS];

// edges, flattened the same way
float4x4 _RM_EdgeTransforms[MAX_EDGES];
float _RM_EdgePrimitives[MAX_EDGES];     // <0 = inactive/unused slot
float _RM_EdgeLengths[MAX_EDGES];
float3 _RM_EdgeSizes[MAX_EDGES];
float _RM_EdgeRoundings[MAX_EDGES];
float _RM_EdgeSmoothings[MAX_EDGES];

// optional extra props, shared (not per-skeleton), same 4 slots as the
// original "objects" array
float3 _RM_ObjectColors[MAX_OBJECTS];
float _RM_ObjectAmbientScales[MAX_OBJECTS];
float _RM_ObjectDiffuseScales[MAX_OBJECTS];
float _RM_ObjectSpecularScales[MAX_OBJECTS];
float _RM_ObjectSpecularPows[MAX_OBJECTS];
float _RM_ObjectOcclusionScales[MAX_OBJECTS];
float _RM_ObjectOcclusionRanges[MAX_OBJECTS];
float _RM_ObjectOcclusionResolutions[MAX_OBJECTS];
float3 _RM_ObjectOcclusionColors[MAX_OBJECTS];
float4x4 _RM_ObjectTransforms[MAX_OBJECTS];
float _RM_ObjectPrimitives[MAX_OBJECTS];
float3 _RM_ObjectSizes[MAX_OBJECTS];
float _RM_ObjectRoundings[MAX_OBJECTS];
float _RM_ObjectSmoothings[MAX_OBJECTS];

// -----------------------------------------------------------------
// Surface struct (== `Surface` in the GLSL source)
// -----------------------------------------------------------------
struct Surface
{
    float3 color;
    float ambientScale;
    float diffuseScale;
    float specularScale;
    float specularPow;
    float occlusionScale;
    float occlusionRange;
    float occlusionResolution;
    float3 occlusionColor;
    float signedDistance;
};

Surface MakeEmptySurface()
{
    Surface s;
    s.color = float3(0, 0, 0);
    s.ambientScale = 0;
    s.diffuseScale = 0;
    s.specularScale = 0;
    s.specularPow = 10.0;
    s.occlusionScale = 0;
    s.occlusionRange = 0.5;
    s.occlusionResolution = 0.5;
    s.occlusionColor = float3(0, 0, 0);
    s.signedDistance = 1000.0;
    return s;
}

// -----------------------------------------------------------------
// Smoothing operations (== poly_smin / poly_smin_surface / union_surface)
// -----------------------------------------------------------------
float PolySmin(float a, float b, float k)
{
    float h = clamp(0.5 + 0.5 * (b - a) / k, 0.0, 1.0);
    return lerp(b, a, h) - k * h * (1.0 - h);
}

float2 PolySminSurface(float a, float b, float k)
{
    float h = max(k - abs(a - b), 0.0) / k;
    float m = h * h * 0.5;
    float s = m * k * 0.5;
    return (a < b) ? float2(a - s, m) : float2(b - s, 1.0 - m);
}

Surface UnionSurface(Surface s1, Surface s2, float smoothness)
{
    float2 sd = PolySminSurface(s1.signedDistance, s2.signedDistance, smoothness);
    float signedDistance = sd.x;
    float t = sd.y;

    Surface o;
    o.color = lerp(s1.color, s2.color, t);
    o.ambientScale = lerp(s1.ambientScale, s2.ambientScale, t);
    o.diffuseScale = lerp(s1.diffuseScale, s2.diffuseScale, t);
    o.specularScale = lerp(s1.specularScale, s2.specularScale, t);
    o.specularPow = lerp(s1.specularPow, s2.specularPow, t);
    o.occlusionScale = lerp(s1.occlusionScale, s2.occlusionScale, t);
    o.occlusionRange = lerp(s1.occlusionRange, s2.occlusionRange, t);
    o.occlusionResolution = lerp(s1.occlusionResolution, s2.occlusionResolution, t);
    o.occlusionColor = lerp(s1.occlusionColor, s2.occlusionColor, t);
    o.signedDistance = signedDistance;
    return o;
}

// Smooth max, the dual of PolySminSurface: smax(a,b,k) = -smin(-a,-b,k). The
// returned blend factor keeps the same "0 = pure a, 1 = pure b" meaning as
// PolySminSurface's, since that factor only depends on |a-b|.
float2 PolySmaxSurface(float a, float b, float k)
{
    float2 sd = PolySminSurface(-a, -b, k);
    return float2(-sd.x, sd.y);
}

Surface IntersectSurface(Surface s1, Surface s2, float smoothness)
{
    float2 sd = PolySmaxSurface(s1.signedDistance, s2.signedDistance, smoothness);
    float signedDistance = sd.x;
    float t = sd.y;

    Surface o;
    o.color = lerp(s1.color, s2.color, t);
    o.ambientScale = lerp(s1.ambientScale, s2.ambientScale, t);
    o.diffuseScale = lerp(s1.diffuseScale, s2.diffuseScale, t);
    o.specularScale = lerp(s1.specularScale, s2.specularScale, t);
    o.specularPow = lerp(s1.specularPow, s2.specularPow, t);
    o.occlusionScale = lerp(s1.occlusionScale, s2.occlusionScale, t);
    o.occlusionRange = lerp(s1.occlusionRange, s2.occlusionRange, t);
    o.occlusionResolution = lerp(s1.occlusionResolution, s2.occlusionResolution, t);
    o.occlusionColor = lerp(s1.occlusionColor, s2.occlusionColor, t);
    o.signedDistance = signedDistance;
    return o;
}

// A minus B: carves s2 out of s1 (smooth subtraction, smax(d1,-d2,k)). Near
// the seam the carved cavity wall is literally s2's own surface with its
// normal flipped inward, so we shade it by blending toward s2's shading
// fields there, same as any other seam in this file.
Surface SubtractSurface(Surface s1, Surface s2, float smoothness)
{
    float2 sd = PolySmaxSurface(s1.signedDistance, -s2.signedDistance, smoothness);
    float signedDistance = sd.x;
    float t = sd.y;

    Surface o;
    o.color = lerp(s1.color, s2.color, t);
    o.ambientScale = lerp(s1.ambientScale, s2.ambientScale, t);
    o.diffuseScale = lerp(s1.diffuseScale, s2.diffuseScale, t);
    o.specularScale = lerp(s1.specularScale, s2.specularScale, t);
    o.specularPow = lerp(s1.specularPow, s2.specularPow, t);
    o.occlusionScale = lerp(s1.occlusionScale, s2.occlusionScale, t);
    o.occlusionRange = lerp(s1.occlusionRange, s2.occlusionRange, t);
    o.occlusionResolution = lerp(s1.occlusionResolution, s2.occlusionResolution, t);
    o.occlusionColor = lerp(s1.occlusionColor, s2.occlusionColor, t);
    o.signedDistance = signedDistance;
    return o;
}

// XOR: (A minus B) union (B minus A) - the parts of each skeleton that
// don't overlap the other, with the overlap itself hollowed out.
Surface SymmetricDifferenceSurface(Surface s1, Surface s2, float smoothness)
{
    Surface aMinusB = SubtractSurface(s1, s2, smoothness);
    Surface bMinusA = SubtractSurface(s2, s1, smoothness);
    return UnionSurface(aMinusB, bMinusA, smoothness);
}

// -----------------------------------------------------------------
// Signed distance functions (== the *SDF functions in the GLSL source)
// -----------------------------------------------------------------
float SphereSDF(float3 p, float r)
{
    return length(p) - r;
}

float RoundBoxSDF(float3 p, float3 size, float radius)
{
    float3 d = abs(p) - ((size - radius) / 2.0);
    float insideDistance = min(max(d.x, max(d.y, d.z)), 0.0);
    float outsideDistance = length(max(d, 0.0));
    return insideDistance + outsideDistance - radius;
}

float RoundCylinderSDF(float3 p, float h, float r, float radius)
{
    float inOutRadius = length(p.xy) - (r - radius);
    float inOutHeight = abs(p.z) - (h - radius) / 2.0;
    float insideDistance = min(max(inOutRadius, inOutHeight), 0.0);
    float outsideDistance = length(max(float2(inOutRadius, inOutHeight), 0.0));
    return insideDistance + outsideDistance - radius;
}

float RoundCapsuleSDF(float3 p, float h, float r, float radius)
{
    p.z -= clamp(p.z, -(h - radius) / 2.0, (h - radius) / 2.0);
    return length(p) - r - radius;
}

// -----------------------------------------------------------------
// Primitive morphing (== primitiveMorphSDF). `primitive` can carry a
// fractional part to smoothly morph between primitive N and N+1, exactly
// like the original (e.g. 0.5 == halfway between sphere and box).
// 0 = sphere, 1 = box, 2 = capsule, 3 = cylinder.
// -----------------------------------------------------------------
float PrimitiveMorphSDF(float3 p, float3 size, float rounding, float primitive)
{
    int i0 = (int)floor(primitive);
    float mixT = frac(primitive);
    int i1 = mixT != 0.0 ? i0 + 1 : i0;

    float d0 = 0.0;
    float d1 = 0.0;

    if (i0 == 0) // sphere
    {
        d0 = SphereSDF(p, size.x);
        d1 = (i1 == 1) ? RoundBoxSDF(p, size, rounding) : d0;
    }
    else if (i0 == 1) // box
    {
        d0 = RoundBoxSDF(p, size, rounding);
        d1 = (i1 == 2) ? RoundCapsuleSDF(p, size.z, size.x, rounding) : d0;
    }
    else if (i0 == 2) // capsule
    {
        d0 = RoundCapsuleSDF(p, size.z, size.x, rounding);
        d1 = (i1 == 3) ? RoundCylinderSDF(p, size.z, size.x, rounding) : d0;
    }
    else // cylinder
    {
        d0 = RoundCylinderSDF(p, size.z, size.x, rounding);
        d1 = d0;
    }

    return lerp(d0, d1, mixT);
}

// -----------------------------------------------------------------
// Scene composition (== sceneSDF_surface). Builds the combined surface
// for both skeletons + optional shared object primitives.
// -----------------------------------------------------------------
Surface SceneSDFSurface(float3 samplePoint)
{
    float4 samplePoint4D = mul(_RM_SceneTransform, float4(samplePoint, 1.0));

    // --- joints, per skeleton ---
    Surface jointSurface[SKELETON_COUNT];
    bool jointHit[SKELETON_COUNT];
    [unroll]
    for (int sk = 0; sk < SKELETON_COUNT; sk++)
    {
        jointSurface[sk] = MakeEmptySurface();
        jointHit[sk] = false;
    }

    [loop]
    for (int jI = 0; jI < MAX_JOINTS; jI++)
    {
        float prim = _RM_JointPrimitives[jI];
        // NaN-safe: an unset/garbage uniform (e.g. before the renderer feature
        // has ever run, such as the Editor's material preview) must never be
        // treated as active - `prim < 0` alone is false for NaN.
        if (!(prim >= 0.0)) continue;

        int sk = jI / MAX_JOINTS_PER_SKELETON;

        float3 localPos = mul(_RM_JointTransforms[jI], samplePoint4D).xyz;
        float d = PrimitiveMorphSDF(localPos, _RM_JointSizes[jI], _RM_JointRoundings[jI], prim);

        Surface tmp;
        tmp.color = _RM_JointColor[sk];
        tmp.ambientScale = _RM_JointAmbientScale[sk];
        tmp.diffuseScale = _RM_JointDiffuseScale[sk];
        tmp.specularScale = _RM_JointSpecularScale[sk];
        tmp.specularPow = _RM_JointSpecularPow[sk];
        tmp.occlusionScale = _RM_JointOcclusionScale[sk];
        tmp.occlusionRange = _RM_JointOcclusionRange[sk];
        tmp.occlusionResolution = _RM_JointOcclusionResolution[sk];
        tmp.occlusionColor = _RM_JointOcclusionColor[sk];
        tmp.signedDistance = d;

        if (jointHit[sk])
        {
            jointSurface[sk] = UnionSurface(tmp, jointSurface[sk], _RM_JointSmoothings[jI]);
        }
        else
        {
            jointSurface[sk] = tmp;
        }
        jointHit[sk] = true;
    }

    // --- edges, per skeleton ---
    Surface edgeSurface[SKELETON_COUNT];
    bool edgeHit[SKELETON_COUNT];
    [unroll]
    for (int sk2 = 0; sk2 < SKELETON_COUNT; sk2++)
    {
        edgeSurface[sk2] = MakeEmptySurface();
        edgeHit[sk2] = false;
    }

    [loop]
    for (int eI = 0; eI < MAX_EDGES; eI++)
    {
        float prim = _RM_EdgePrimitives[eI];
        if (!(prim >= 0.0)) continue; // NaN-safe, see joint loop above

        int sk = eI / MAX_EDGES_PER_SKELETON;

        float3 localPos = mul(_RM_EdgeTransforms[eI], samplePoint4D).xyz;
        float3 size = float3(_RM_EdgeSizes[eI].x, _RM_EdgeSizes[eI].y, _RM_EdgeLengths[eI] * _RM_EdgeSizes[eI].z);
        float d = PrimitiveMorphSDF(localPos, size, _RM_EdgeRoundings[eI], prim);

        Surface tmp;
        tmp.color = _RM_EdgeColor[sk];
        tmp.ambientScale = _RM_EdgeAmbientScale[sk];
        tmp.diffuseScale = _RM_EdgeDiffuseScale[sk];
        tmp.specularScale = _RM_EdgeSpecularScale[sk];
        tmp.specularPow = _RM_EdgeSpecularPow[sk];
        tmp.occlusionScale = _RM_EdgeOcclusionScale[sk];
        tmp.occlusionRange = _RM_EdgeOcclusionRange[sk];
        tmp.occlusionResolution = _RM_EdgeOcclusionResolution[sk];
        tmp.occlusionColor = _RM_EdgeOcclusionColor[sk];
        tmp.signedDistance = d;

        if (edgeHit[sk])
        {
            edgeSurface[sk] = UnionSurface(tmp, edgeSurface[sk], _RM_EdgeSmoothings[eI]);
        }
        else
        {
            edgeSurface[sk] = tmp;
        }
        edgeHit[sk] = true;
    }

    // --- combine joints+edges per skeleton ---
    Surface skelSurfaces[SKELETON_COUNT];
    bool skelSurfaceValid[SKELETON_COUNT];

    [unroll]
    for (int sk3 = 0; sk3 < SKELETON_COUNT; sk3++)
    {
        Surface combined = MakeEmptySurface();
        bool combinedValid = false;

        if (jointHit[sk3] && edgeHit[sk3])
        {
            combined = UnionSurface(jointSurface[sk3], edgeSurface[sk3], _RM_JointEdgeSmoothing[sk3]);
            combinedValid = true;
        }
        else if (jointHit[sk3])
        {
            combined = jointSurface[sk3];
            combinedValid = true;
        }
        else if (edgeHit[sk3])
        {
            combined = edgeSurface[sk3];
            combinedValid = true;
        }

        if (combinedValid && _RM_SkeletonInvert[sk3] != 0.0)
        {
            combined.signedDistance = -combined.signedDistance;
        }

        skelSurfaces[sk3] = combined;
        skelSurfaceValid[sk3] = combinedValid;
    }

    // --- combine skeleton A (index 0) and skeleton B (index 1) via the
    // selected boolean operator ---
    Surface skelSurface = MakeEmptySurface();
    bool skelHit = false;

    if (skelSurfaceValid[0] && skelSurfaceValid[1])
    {
        if (_RM_SkeletonCombineOp == 1)
        {
            skelSurface = SubtractSurface(skelSurfaces[0], skelSurfaces[1], _RM_SkeletonCombineSmoothing);
        }
        else if (_RM_SkeletonCombineOp == 2)
        {
            skelSurface = IntersectSurface(skelSurfaces[0], skelSurfaces[1], _RM_SkeletonCombineSmoothing);
        }
        else if (_RM_SkeletonCombineOp == 3)
        {
            skelSurface = SymmetricDifferenceSurface(skelSurfaces[0], skelSurfaces[1], _RM_SkeletonCombineSmoothing);
        }
        else
        {
            skelSurface = UnionSurface(skelSurfaces[0], skelSurfaces[1], _RM_SkeletonCombineSmoothing);
        }
        skelHit = true;
    }
    else if (skelSurfaceValid[0])
    {
        skelSurface = skelSurfaces[0];
        skelHit = true;
    }
    else if (skelSurfaceValid[1])
    {
        skelSurface = skelSurfaces[1];
        skelHit = true;
    }

    // --- optional shared object primitives ---
    Surface objectSurface = MakeEmptySurface();
    bool objectHit = false;

    [loop]
    for (int oI = 0; oI < MAX_OBJECTS; oI++)
    {
        float prim = _RM_ObjectPrimitives[oI];
        if (!(prim >= 0.0)) continue; // NaN-safe, see joint loop above

        float3 localPos = mul(_RM_ObjectTransforms[oI], samplePoint4D).xyz;
        float d = PrimitiveMorphSDF(localPos, _RM_ObjectSizes[oI], _RM_ObjectRoundings[oI], prim);

        Surface tmp;
        tmp.color = _RM_ObjectColors[oI];
        tmp.ambientScale = _RM_ObjectAmbientScales[oI];
        tmp.diffuseScale = _RM_ObjectDiffuseScales[oI];
        tmp.specularScale = _RM_ObjectSpecularScales[oI];
        tmp.specularPow = _RM_ObjectSpecularPows[oI];
        tmp.occlusionScale = _RM_ObjectOcclusionScales[oI];
        tmp.occlusionRange = _RM_ObjectOcclusionRanges[oI];
        tmp.occlusionResolution = _RM_ObjectOcclusionResolutions[oI];
        tmp.occlusionColor = _RM_ObjectOcclusionColors[oI];
        tmp.signedDistance = d;

        if (objectHit)
        {
            objectSurface = UnionSurface(tmp, objectSurface, _RM_ObjectSmoothings[oI]);
        }
        else
        {
            objectSurface = tmp;
        }
        objectHit = true;
    }

    Surface result;
    if (skelHit && objectHit)
    {
        result = UnionSurface(skelSurface, objectSurface, _RM_SkelObjectSmoothing);
    }
    else if (skelHit)
    {
        result = skelSurface;
    }
    else if (objectHit)
    {
        result = objectSurface;
    }
    else
    {
        result = MakeEmptySurface();
    }

    return result;
}

float SceneSDF(float3 samplePoint)
{
    return SceneSDFSurface(samplePoint).signedDistance;
}

// -----------------------------------------------------------------
// Ray marching (== shortestDistanceToSurface_surface)
// -----------------------------------------------------------------
Surface RayMarch(float3 eye, float3 dir, float start, float end)
{
    float depth = start;
    Surface surf = MakeEmptySurface();

    [loop]
    for (int i = 0; i < _RM_MaxSteps; i++)
    {
        float3 p = eye + depth * dir;
        surf = SceneSDFSurface(p);

        if (surf.signedDistance < RM_EPSILON)
        {
            surf.signedDistance = depth;
            return surf;
        }

        depth += surf.signedDistance;

        if (depth >= end)
        {
            surf.signedDistance = end;
            surf.color = _RM_BgColor;
            return surf;
        }
    }

    surf.color = _RM_BgColor;
    surf.ambientScale = 1.0;
    surf.diffuseScale = 0.0;
    surf.specularScale = 0.0;
    surf.specularPow = 1.0;
    surf.occlusionScale = 0.0;
    surf.occlusionRange = 1.0;
    surf.occlusionResolution = 1.0;
    surf.occlusionColor = _RM_BgOcclusionColor;
    surf.signedDistance = end;
    return surf;
}

// -----------------------------------------------------------------
// Normal estimation, AO, shadows, Phong (direct ports)
// -----------------------------------------------------------------
float3 EstimateNormal(float3 p)
{
    float centerDist = SceneSDF(p);
    return normalize(float3(
        SceneSDF(float3(p.x + RM_EPSILON, p.y, p.z)) - centerDist,
        SceneSDF(float3(p.x, p.y + RM_EPSILON, p.z)) - centerDist,
        SceneSDF(float3(p.x, p.y, p.z + RM_EPSILON)) - centerDist
    ));
}

float AmbientOcclusion(float3 surfacePos, float3 surfaceNormal, float occlusionRange, float occlusionResolution)
{
    float minT = 0.01;
    float maxT = occlusionRange;
    float tIncr = max(occlusionResolution, 0.001);

    float occlusionFactor = 1.0;

    for (float t = minT; t < maxT; t += tIncr)
    {
        float dist = SceneSDF(surfacePos + surfaceNormal * t);

        if (dist < t - RM_EPSILON)
        {
            float normT = (t - minT) / (maxT - minT);
            occlusionFactor = occlusionFactor * normT + occlusionFactor * dist / t * (1.0 - normT);
        }

        if (occlusionFactor <= 0.0) break;
    }

    return occlusionFactor;
}

float3 PhongContribForLight(float3 p, float3 eye, float3 lightPos, float3 N,
    float3 diffuseColor, float diffuseScale, float3 specularColor, float specularScale, float specularPow)
{
    float3 L = normalize(lightPos - p);
    float3 V = normalize(eye - p);
    float3 R = normalize(reflect(-L, N));

    float dotLN = dot(L, N);
    float dotRV = dot(R, V);

    if (dotLN < 0.0) return float3(0, 0, 0);

    if (dotRV < 0.0)
        return diffuseScale * (diffuseColor * dotLN);

    return diffuseScale * diffuseColor * dotLN + specularScale * specularColor * pow(max(dotRV, 0.0), specularPow);
}

float SoftShadow(float3 lightPoint, float3 lightToSurfaceDir, float mint, float maxt, float k)
{
    float res = 1.0;
    float t = mint;

    // Shares the _RM_MaxSteps budget with the primary march - shadow rays
    // are usually the single most expensive part of this shader (one full
    // secondary march per lit pixel), so dialing Max Steps down in the F1
    // panel cuts this cost too, not just the camera ray's.
    [loop]
    for (int i = 0; i < _RM_MaxSteps && t < maxt; i++)
    {
        float h = SceneSDF(lightPoint + lightToSurfaceDir * t);
        if (h < 0.002) return 0.0;

        res = min(res, k * h / t);
        t += h / 10.0;
    }
    return res;
}

// -----------------------------------------------------------------
// Full shading pipeline for a camera ray (== body of `main()`)
// -----------------------------------------------------------------
float3 ShadeRay(float3 eye, float3 worldDir)
{
    Surface surface = RayMarch(eye, worldDir, RM_MIN_DIST, RM_MAX_DIST);
    float dist = surface.signedDistance;

    if (dist > RM_MAX_DIST - RM_EPSILON)
    {
        return surface.color; // background
    }

    float3 surfacePos = eye + dist * worldDir;
    float3 surfaceNormal = EstimateNormal(surfacePos);

    // ambient + diffuse/specular
    float3 color1 = surface.color * surface.ambientScale;

    float lightStrength = 1.0;
    if (_RM_ShadowStrength > 0.0)
    {
        float3 lightToSurfaceVec = surfacePos - _RM_LightPosition;
        float lightToSurfaceDist = length(lightToSurfaceVec);
        float3 lightToSurfaceDir = normalize(lightToSurfaceVec);
        lightStrength = SoftShadow(_RM_LightPosition, lightToSurfaceDir, 0.0, lightToSurfaceDist - 1.0, _RM_ShadowSmooth);
        lightStrength = (1.0 - _RM_ShadowStrength) + lightStrength * _RM_ShadowStrength;
    }

    color1 += PhongContribForLight(surfacePos, eye, _RM_LightPosition, surfaceNormal,
        surface.color, surface.diffuseScale, surface.color, surface.specularScale, surface.specularPow) * lightStrength;

    // ambient occlusion - skip the ray march entirely when this surface's
    // occlusionScale is 0, instead of computing it and multiplying away
    // the result (AmbientOcclusion is its own per-pixel raymarch loop).
    float3 colorDiff = color1 - surface.occlusionColor;
    float occlusionStrength = 0.0;
    if (surface.occlusionScale > 0.0)
    {
        occlusionStrength = AmbientOcclusion(surfacePos, surfaceNormal, surface.occlusionRange, surface.occlusionResolution);
        occlusionStrength = 1.0 - occlusionStrength;
        occlusionStrength *= surface.occlusionScale;
    }

    float3 color2 = color1 - occlusionStrength * colorDiff;

    // distance fog
    float fogT = saturate((dist - _RM_FogMinDist) / max(_RM_FogMaxDist - _RM_FogMinDist, 0.0001));
    float3 color3 = fogT * _RM_BgColor + (1.0 - fogT) * color2;

    return color3;
}

#endif // RAYMARCH_SKELETON_CORE_INCLUDED
