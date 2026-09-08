using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Premiere.RaymarchSkeleton;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Premiere.RaymarchSkeleton.Editor
{
    /// <summary>
    /// Offline mesh extraction for the raymarched skeleton scene. Not part of
    /// the runtime raymarch path (RaymarchSkeletonCore.hlsl) at all - this is
    /// a from-scratch C# port of the same SDF math (PrimitiveMorphSDF /
    /// UnionSurface / the joint+edge+skeleton combine order in
    /// SceneSDFSurface) so a marching-tetrahedra sweep over a 3D grid can
    /// pull an actual Mesh out of the field for a frozen pose. Any change to
    /// the primitive/blend math in the .hlsl should be mirrored here.
    ///
    /// Uses marching TETRAHEDRA (6 tets/cube via the main-diagonal / Kuhn
    /// decomposition) rather than classic marching cubes, specifically to
    /// avoid needing the usual 256-entry triangulation lookup table - each
    /// tetrahedron only has 16 sign configurations and the triangle(s) for
    /// each can be derived directly, and per-triangle winding is corrected
    /// from the field gradient rather than baked into a table.
    /// </summary>
    public class RaymarchSkeletonMeshBaker : EditorWindow
    {
        private RaymarchSkeletonInstance _skeletonA;
        private RaymarchSkeletonInstance _skeletonB;
        private Transform _sceneRoot;
        private float _voxelSize = 0.02f;
        private float _padding = 0.35f;
        private bool _exportObj = true;
        private string _lastBakeInfo = "";

        [MenuItem("Tools/Raymarch Skeleton/Bake Combined Mesh...")]
        private static void Open()
        {
            var window = GetWindow<RaymarchSkeletonMeshBaker>(true, "Bake Raymarch Skeleton Mesh");
            window.TryAutoFind();
            window.minSize = new Vector2(380, 260);
        }

        private void TryAutoFind()
        {
            var binder = FindFirstObjectByType<RaymarchSkeletonBinder>();
            if (binder != null)
            {
                _skeletonA = binder.skeletonA;
                _skeletonB = binder.skeletonB;
                if (binder.feature != null) _sceneRoot = binder.feature.sceneRoot;
                return;
            }

            var instances = FindObjectsByType<RaymarchSkeletonInstance>(FindObjectsSortMode.None);
            if (instances.Length > 0) _skeletonA = instances[0];
            if (instances.Length > 1) _skeletonB = instances[1];
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Bakes a static Mesh from both skeletons' current pose (whatever the Timeline is scrubbed to right now) using the same SDF blend as the raymarch shader. Not real-time - a single grid sweep can take from a few seconds to a minute or two depending on Voxel Size.",
                MessageType.Info);

            EditorGUILayout.Space();
            _skeletonA = (RaymarchSkeletonInstance)EditorGUILayout.ObjectField("Skeleton A", _skeletonA, typeof(RaymarchSkeletonInstance), true);
            _skeletonB = (RaymarchSkeletonInstance)EditorGUILayout.ObjectField("Skeleton B", _skeletonB, typeof(RaymarchSkeletonInstance), true);
            _sceneRoot = (Transform)EditorGUILayout.ObjectField("Scene Root (optional)", _sceneRoot, typeof(Transform), true);

            if (GUILayout.Button("Find In Scene")) TryAutoFind();

            EditorGUILayout.Space();
            _voxelSize = EditorGUILayout.FloatField(new GUIContent("Voxel Size", "Grid cell size in world units. Smaller = more detail, much slower and heavier. 0.01-0.03 is a reasonable range for human-scale limbs (~0.03-0.08 joint/edge radii)."), _voxelSize);
            _voxelSize = Mathf.Max(_voxelSize, 0.001f);
            _padding = EditorGUILayout.FloatField(new GUIContent("Bounds Padding", "Extra world-space margin around both skeletons' joints, so the surface isn't clipped at the sample grid's edge."), _padding);

            _exportObj = EditorGUILayout.Toggle(new GUIContent("Also export .obj", "Writes an .obj file next to the saved Mesh asset (no built-in FBX writer without the FBX Exporter package - import the .obj into Blender/etc. and re-export as FBX if you need that format)."), _exportObj);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_skeletonA == null && _skeletonB == null))
            {
                if (GUILayout.Button("Bake Mesh...", GUILayout.Height(32)))
                {
                    Bake();
                }
            }

            if (!string.IsNullOrEmpty(_lastBakeInfo))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_lastBakeInfo, MessageType.None);
            }
        }

        private void Bake()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Save Baked Skeleton Mesh", "RaymarchSkeletonBake", "asset",
                "Choose where to save the baked Mesh asset.");
            if (string.IsNullOrEmpty(path)) return;

            var fieldA = BuildFieldData(_skeletonA);
            var fieldB = BuildFieldData(_skeletonB);

            if (!fieldA.HasValue && !fieldB.HasValue)
            {
                EditorUtility.DisplayDialog("Bake Raymarch Skeleton Mesh", "Neither skeleton has any active joints right now.", "OK");
                return;
            }

            Bounds bounds = ComputeBounds(_skeletonA, _skeletonB, _padding);
            Matrix4x4 sceneTransform = _sceneRoot != null ? _sceneRoot.worldToLocalMatrix : Matrix4x4.identity;

            Mesh mesh;
            try
            {
                mesh = MarchingTetrahedra.Build(fieldA, fieldB, sceneTransform, bounds, _voxelSize);
            }
            catch (BakeCancelledException)
            {
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (mesh.vertexCount == 0)
            {
                EditorUtility.DisplayDialog("Bake Raymarch Skeleton Mesh",
                    "The grid sweep found no surface crossings - the two skeletons' current pose may be empty, or Voxel Size may be too coarse relative to the smallest primitive.", "OK");
                return;
            }

            AssetDatabase.CreateAsset(mesh, path);
            AssetDatabase.SaveAssets();

            var go = new GameObject(Path.GetFileNameWithoutExtension(path));
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            Undo.RegisterCreatedObjectUndo(go, "Bake Raymarch Skeleton Mesh");
            Selection.activeGameObject = go;

            string objPath = null;
            if (_exportObj)
            {
                objPath = Path.ChangeExtension(path, ".obj");
                ObjExporter.Export(mesh, objPath);
                AssetDatabase.ImportAsset(objPath);
            }

            _lastBakeInfo = $"Baked {mesh.vertexCount} verts / {mesh.triangles.Length / 3} tris.\nMesh: {path}" +
                             (objPath != null ? $"\nOBJ: {objPath}" : "");
            Debug.Log("[RaymarchSkeletonMeshBaker] " + _lastBakeInfo.Replace("\n", "  "));
        }

        private static Bounds ComputeBounds(RaymarchSkeletonInstance a, RaymarchSkeletonInstance b, float padding)
        {
            bool started = false;
            Bounds bounds = new Bounds();

            void Encapsulate(RaymarchSkeletonInstance inst)
            {
                if (inst == null || inst.jointWorldPositions == null) return;
                inst.Gather();
                int count = Mathf.Min(inst.ActiveJointCount, inst.jointWorldPositions.Length);
                for (int i = 0; i < count; i++)
                {
                    if (!started) { bounds = new Bounds(inst.jointWorldPositions[i], Vector3.zero); started = true; }
                    else bounds.Encapsulate(inst.jointWorldPositions[i]);
                }
            }

            Encapsulate(a);
            Encapsulate(b);

            if (!started) bounds = new Bounds(Vector3.zero, Vector3.one);
            bounds.Expand(padding * 2f); // Expand() grows by the value on both sides of each axis
            return bounds;
        }

        private static SkeletonFieldData? BuildFieldData(RaymarchSkeletonInstance inst)
        {
            if (inst == null) return null;
            inst.Gather();
            if (inst.ActiveJointCount == 0 && inst.ActiveEdgeCount == 0) return null;

            return new SkeletonFieldData
            {
                jointCount = inst.ActiveJointCount,
                jointInv = inst.jointInverseTransforms,
                jointPrim = inst.jointPrimitives,
                jointSize = inst.jointSizes,
                jointRound = inst.jointRoundings,
                jointSmooth = inst.jointSmoothings,
                jointColor = inst.jointColor,

                edgeCount = inst.ActiveEdgeCount,
                edgeInv = inst.edgeInverseTransforms,
                edgePrim = inst.edgePrimitives,
                edgeLen = inst.edgeLengths,
                edgeSize = inst.edgeSizes,
                edgeRound = inst.edgeRoundings,
                edgeSmooth = inst.edgeSmoothings,
                edgeColor = inst.edgeColor,

                jointEdgeSmoothing = inst.jointEdgeSmoothing,
            };
        }
    }

    // -----------------------------------------------------------------
    // SDF field - a straight C# port of RaymarchSkeletonCore.hlsl's
    // PrimitiveMorphSDF / UnionSurface / SceneSDFSurface. Distance-only
    // consumers can ignore SurfaceSample.color.
    // -----------------------------------------------------------------
    internal struct SkeletonFieldData
    {
        public int jointCount;
        public Matrix4x4[] jointInv;
        public float[] jointPrim;
        public Vector3[] jointSize;
        public float[] jointRound;
        public float[] jointSmooth;
        public Color jointColor;

        public int edgeCount;
        public Matrix4x4[] edgeInv;
        public float[] edgePrim;
        public float[] edgeLen;
        public Vector3[] edgeSize;
        public float[] edgeRound;
        public float[] edgeSmooth;
        public Color edgeColor;

        public float jointEdgeSmoothing;
    }

    internal struct SurfaceSample
    {
        public float d;
        public Color color;

        public static readonly SurfaceSample Empty = new SurfaceSample { d = 1000f, color = Color.black };
    }

    internal static class RaymarchSdf
    {
        private static float SphereSDF(Vector3 p, float r) => p.magnitude - r;

        private static float RoundBoxSDF(Vector3 p, Vector3 size, float radius)
        {
            Vector3 d = new Vector3(Mathf.Abs(p.x), Mathf.Abs(p.y), Mathf.Abs(p.z)) - (size - Vector3.one * radius) * 0.5f;
            float insideDistance = Mathf.Min(Mathf.Max(d.x, Mathf.Max(d.y, d.z)), 0f);
            Vector3 dMax = new Vector3(Mathf.Max(d.x, 0f), Mathf.Max(d.y, 0f), Mathf.Max(d.z, 0f));
            float outsideDistance = dMax.magnitude;
            return insideDistance + outsideDistance - radius;
        }

        private static float RoundCylinderSDF(Vector3 p, float h, float r, float radius)
        {
            float inOutRadius = new Vector2(p.x, p.y).magnitude - (r - radius);
            float inOutHeight = Mathf.Abs(p.z) - (h - radius) * 0.5f;
            float insideDistance = Mathf.Min(Mathf.Max(inOutRadius, inOutHeight), 0f);
            float outsideDistance = new Vector2(Mathf.Max(inOutRadius, 0f), Mathf.Max(inOutHeight, 0f)).magnitude;
            return insideDistance + outsideDistance - radius;
        }

        private static float RoundCapsuleSDF(Vector3 p, float h, float r, float radius)
        {
            float halfSpan = (h - radius) * 0.5f;
            p.z -= Mathf.Clamp(p.z, -halfSpan, halfSpan);
            return p.magnitude - r - radius;
        }

        internal static float PrimitiveMorphSDF(Vector3 p, Vector3 size, float rounding, float primitive)
        {
            int i0 = Mathf.FloorToInt(primitive);
            float mixT = primitive - i0;
            int i1 = mixT != 0f ? i0 + 1 : i0;

            float d0, d1;
            if (i0 == 0)
            {
                d0 = SphereSDF(p, size.x);
                d1 = (i1 == 1) ? RoundBoxSDF(p, size, rounding) : d0;
            }
            else if (i0 == 1)
            {
                d0 = RoundBoxSDF(p, size, rounding);
                d1 = (i1 == 2) ? RoundCapsuleSDF(p, size.z, size.x, rounding) : d0;
            }
            else if (i0 == 2)
            {
                d0 = RoundCapsuleSDF(p, size.z, size.x, rounding);
                d1 = (i1 == 3) ? RoundCylinderSDF(p, size.z, size.x, rounding) : d0;
            }
            else
            {
                d0 = RoundCylinderSDF(p, size.z, size.x, rounding);
                d1 = d0;
            }

            return Mathf.Lerp(d0, d1, mixT);
        }

        internal static SurfaceSample Union(SurfaceSample a, SurfaceSample b, float k)
        {
            float kk = Mathf.Max(k, 1e-5f);
            float h = Mathf.Max(kk - Mathf.Abs(a.d - b.d), 0f) / kk;
            float m = h * h * 0.5f;
            float s = m * kk * 0.5f;

            float dist;
            float t;
            if (a.d < b.d) { dist = a.d - s; t = m; }
            else { dist = b.d - s; t = 1f - m; }

            return new SurfaceSample { d = dist, color = Color.Lerp(a.color, b.color, t) };
        }

        private static bool EvaluateSkeleton(in SkeletonFieldData data, Vector3 sp, out SurfaceSample result)
        {
            SurfaceSample jointSurf = SurfaceSample.Empty;
            bool jointHit = false;
            for (int i = 0; i < data.jointCount; i++)
            {
                float prim = data.jointPrim[i];
                if (!(prim >= 0f)) continue;

                Vector3 localPos = data.jointInv[i].MultiplyPoint3x4(sp);
                float d = PrimitiveMorphSDF(localPos, data.jointSize[i], data.jointRound[i], prim);
                var tmp = new SurfaceSample { d = d, color = data.jointColor };

                jointSurf = jointHit ? Union(tmp, jointSurf, data.jointSmooth[i]) : tmp;
                jointHit = true;
            }

            SurfaceSample edgeSurf = SurfaceSample.Empty;
            bool edgeHit = false;
            for (int i = 0; i < data.edgeCount; i++)
            {
                float prim = data.edgePrim[i];
                if (!(prim >= 0f)) continue;

                Vector3 localPos = data.edgeInv[i].MultiplyPoint3x4(sp);
                Vector3 size = new Vector3(data.edgeSize[i].x, data.edgeSize[i].y, data.edgeLen[i] * data.edgeSize[i].z);
                float d = PrimitiveMorphSDF(localPos, size, data.edgeRound[i], prim);
                var tmp = new SurfaceSample { d = d, color = data.edgeColor };

                edgeSurf = edgeHit ? Union(tmp, edgeSurf, data.edgeSmooth[i]) : tmp;
                edgeHit = true;
            }

            if (jointHit && edgeHit) { result = Union(jointSurf, edgeSurf, data.jointEdgeSmoothing); return true; }
            if (jointHit) { result = jointSurf; return true; }
            if (edgeHit) { result = edgeSurf; return true; }
            result = SurfaceSample.Empty;
            return false;
        }

        internal static bool Evaluate(in SkeletonFieldData? a, in SkeletonFieldData? b, Matrix4x4 sceneTransform, Vector3 worldP, out SurfaceSample result)
        {
            Vector3 sp = sceneTransform.MultiplyPoint3x4(worldP);

            SurfaceSample sa = SurfaceSample.Empty;
            SurfaceSample sb = SurfaceSample.Empty;
            bool hitA = a.HasValue && EvaluateSkeleton(a.Value, sp, out sa);
            bool hitB = b.HasValue && EvaluateSkeleton(b.Value, sp, out sb);

            if (hitA && hitB) { result = Union(sb, sa, 0.001f); return true; }
            if (hitA) { result = sa; return true; }
            if (hitB) { result = sb; return true; }
            result = SurfaceSample.Empty;
            return false;
        }

        internal static float EvaluateDistance(in SkeletonFieldData? a, in SkeletonFieldData? b, Matrix4x4 sceneTransform, Vector3 worldP)
        {
            return Evaluate(a, b, sceneTransform, worldP, out var s) ? s.d : SurfaceSample.Empty.d;
        }

        internal static Vector3 GradientNormal(in SkeletonFieldData? a, in SkeletonFieldData? b, Matrix4x4 sceneTransform, Vector3 p, float eps)
        {
            float dc = EvaluateDistance(a, b, sceneTransform, p);
            float dx = EvaluateDistance(a, b, sceneTransform, p + new Vector3(eps, 0, 0)) - dc;
            float dy = EvaluateDistance(a, b, sceneTransform, p + new Vector3(0, eps, 0)) - dc;
            float dz = EvaluateDistance(a, b, sceneTransform, p + new Vector3(0, 0, eps)) - dc;
            Vector3 g = new Vector3(dx, dy, dz);
            return g.sqrMagnitude > 1e-12f ? g.normalized : Vector3.up;
        }
    }

    internal class BakeCancelledException : System.Exception { }

    // -----------------------------------------------------------------
    // Marching tetrahedra: 6 tets per cube (Kuhn/Freudenthal decomposition
    // sharing the cube's main diagonal), each resolved directly from its
    // 16 possible corner-sign configurations - no precomputed triangulation
    // table. Triangle winding is corrected per-triangle from the SDF
    // gradient rather than relying on a fixed table convention.
    // -----------------------------------------------------------------
    internal static class MarchingTetrahedra
    {
        private static readonly Vector3Int[] CornerOffset =
        {
            new Vector3Int(0, 0, 0), new Vector3Int(1, 0, 0), new Vector3Int(1, 1, 0), new Vector3Int(0, 1, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(1, 0, 1), new Vector3Int(1, 1, 1), new Vector3Int(0, 1, 1),
        };

        // Six tetrahedra sharing the main diagonal 0-6.
        private static readonly int[][] Tets =
        {
            new[] { 0, 1, 2, 6 }, new[] { 0, 2, 3, 6 }, new[] { 0, 3, 7, 6 },
            new[] { 0, 7, 4, 6 }, new[] { 0, 4, 5, 6 }, new[] { 0, 5, 1, 6 },
        };

        internal static Mesh Build(SkeletonFieldData? a, SkeletonFieldData? b, Matrix4x4 sceneTransform, Bounds bounds, float voxelSize)
        {
            int nx = Mathf.Max(2, Mathf.CeilToInt(bounds.size.x / voxelSize));
            int ny = Mathf.Max(2, Mathf.CeilToInt(bounds.size.y / voxelSize));
            int nz = Mathf.Max(2, Mathf.CeilToInt(bounds.size.z / voxelSize));

            long totalCells = (long)nx * ny * nz;
            if (totalCells > 8_000_000)
            {
                if (!EditorUtility.DisplayDialog("Bake Raymarch Skeleton Mesh",
                        $"This grid would be {nx}x{ny}x{nz} ({totalCells:N0} cells) - likely to take a very long time and use a lot of memory. Continue anyway?",
                        "Continue", "Cancel"))
                {
                    throw new BakeCancelledException();
                }
            }

            Vector3 origin = bounds.min;
            Vector3 cell = new Vector3(bounds.size.x / nx, bounds.size.y / ny, bounds.size.z / nz);
            float normalEps = cell.magnitude * 0.25f;

            int gx = nx + 1, gy = ny + 1, gz = nz + 1;
            var dist = new float[(long)gx * gy * gz];
            var color = new Color[(long)gx * gy * gz];

            for (int zi = 0; zi < gz; zi++)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Baking Raymarch Skeleton Mesh", $"Sampling field ({zi + 1}/{gz})", 0.5f * zi / gz))
                    throw new BakeCancelledException();

                for (int yi = 0; yi < gy; yi++)
                {
                    for (int xi = 0; xi < gx; xi++)
                    {
                        Vector3 p = origin + new Vector3(xi * cell.x, yi * cell.y, zi * cell.z);
                        long idx = xi + (long)yi * gx + (long)zi * gx * gy;
                        if (RaymarchSdf.Evaluate(a, b, sceneTransform, p, out var s)) { dist[idx] = s.d; color[idx] = s.color; }
                        else { dist[idx] = SurfaceSample.Empty.d; color[idx] = Color.black; }
                    }
                }
            }

            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var cols = new List<Color>();
            var tris = new List<int>();

            var cp = new Vector3[8];
            var cd = new float[8];
            var cc = new Color[8];

            for (int zi = 0; zi < nz; zi++)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Baking Raymarch Skeleton Mesh", $"Triangulating ({zi + 1}/{nz})", 0.5f + 0.5f * zi / nz))
                    throw new BakeCancelledException();

                for (int yi = 0; yi < ny; yi++)
                {
                    for (int xi = 0; xi < nx; xi++)
                    {
                        for (int ci = 0; ci < 8; ci++)
                        {
                            int cx = xi + CornerOffset[ci].x, cy = yi + CornerOffset[ci].y, cz = zi + CornerOffset[ci].z;
                            cp[ci] = origin + new Vector3(cx * cell.x, cy * cell.y, cz * cell.z);
                            long idx = cx + (long)cy * gx + (long)cz * gx * gy;
                            cd[ci] = dist[idx];
                            cc[ci] = color[idx];
                        }

                        foreach (var tet in Tets)
                            ProcessTetra(cp, cd, cc, tet, a, b, sceneTransform, normalEps, verts, norms, cols, tris);
                    }
                }
            }

            var mesh = new Mesh { indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void ProcessTetra(Vector3[] cp, float[] cd, Color[] cc, int[] tet,
            SkeletonFieldData? a, SkeletonFieldData? b, Matrix4x4 sceneTransform, float normalEps,
            List<Vector3> verts, List<Vector3> norms, List<Color> cols, List<int> tris)
        {
            var p = new Vector3[4];
            var d = new float[4];
            var c = new Color[4];
            for (int i = 0; i < 4; i++) { p[i] = cp[tet[i]]; d[i] = cd[tet[i]]; c[i] = cc[tet[i]]; }

            int negMask = 0;
            for (int i = 0; i < 4; i++) if (d[i] < 0f) negMask |= 1 << i;
            int negCount = ((negMask >> 0) & 1) + ((negMask >> 1) & 1) + ((negMask >> 2) & 1) + ((negMask >> 3) & 1);
            if (negCount == 0 || negCount == 4) return;

            Vector3 Interp(int ia, int ib, out Color col)
            {
                float da = d[ia], db = d[ib];
                float t = Mathf.Approximately(da, db) ? 0.5f : Mathf.Clamp01(da / (da - db));
                col = Color.Lerp(c[ia], c[ib], t);
                return Vector3.Lerp(p[ia], p[ib], t);
            }

            void EmitTriangle(Vector3 pa, Vector3 pb, Vector3 pc, Color ca, Color cb, Color ccol)
            {
                Vector3 centroid = (pa + pb + pc) / 3f;
                Vector3 faceNormal = Vector3.Cross(pb - pa, pc - pa);
                Vector3 gradN = RaymarchSdf.GradientNormal(a, b, sceneTransform, centroid, normalEps);
                if (Vector3.Dot(faceNormal, gradN) < 0f)
                {
                    (pb, pc) = (pc, pb);
                    (cb, ccol) = (ccol, cb);
                }

                int baseIdx = verts.Count;
                verts.Add(pa); verts.Add(pb); verts.Add(pc);
                norms.Add(RaymarchSdf.GradientNormal(a, b, sceneTransform, pa, normalEps));
                norms.Add(RaymarchSdf.GradientNormal(a, b, sceneTransform, pb, normalEps));
                norms.Add(RaymarchSdf.GradientNormal(a, b, sceneTransform, pc, normalEps));
                cols.Add(ca); cols.Add(cb); cols.Add(ccol);
                tris.Add(baseIdx); tris.Add(baseIdx + 1); tris.Add(baseIdx + 2);
            }

            if (negCount == 1 || negCount == 3)
            {
                int lone = -1;
                for (int i = 0; i < 4; i++)
                {
                    bool isNeg = (negMask & (1 << i)) != 0;
                    if ((negCount == 1 && isNeg) || (negCount == 3 && !isNeg)) { lone = i; break; }
                }

                int o0 = -1, o1 = -1, o2 = -1, oi = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (i == lone) continue;
                    if (oi == 0) o0 = i; else if (oi == 1) o1 = i; else o2 = i;
                    oi++;
                }

                Vector3 pA = Interp(lone, o0, out Color cA);
                Vector3 pB = Interp(lone, o1, out Color cB);
                Vector3 pC = Interp(lone, o2, out Color cC);
                EmitTriangle(pA, pB, pC, cA, cB, cC);
            }
            else // negCount == 2
            {
                int a0 = -1, a1 = -1, b0 = -1, b1 = -1, ai = 0, bi = 0;
                for (int i = 0; i < 4; i++)
                {
                    bool isNeg = (negMask & (1 << i)) != 0;
                    if (isNeg) { if (ai == 0) a0 = i; else a1 = i; ai++; }
                    else { if (bi == 0) b0 = i; else b1 = i; bi++; }
                }

                Vector3 q00 = Interp(a0, b0, out Color c00);
                Vector3 q01 = Interp(a0, b1, out Color c01);
                Vector3 q10 = Interp(a1, b0, out Color c10);
                Vector3 q11 = Interp(a1, b1, out Color c11);

                // Diagonal split q00-q11 of the quad cross-section (cyclic order q00,q01,q11,q10).
                EmitTriangle(q00, q01, q11, c00, c01, c11);
                EmitTriangle(q00, q11, q10, c00, c11, c10);
            }
        }
    }

    // -----------------------------------------------------------------
    // Minimal OBJ writer (positions + normals + faces, no UVs/materials).
    // Mirrors the standard Unity-left-handed -> OBJ-right-handed fix:
    // negate X and reverse winding, rather than shipping a mirrored mesh.
    // -----------------------------------------------------------------
    internal static class ObjExporter
    {
        internal static void Export(Mesh mesh, string projectRelativePath)
        {
            string fullPath = Path.Combine(Directory.GetCurrentDirectory(), projectRelativePath);
            var verts = mesh.vertices;
            var norms = mesh.normals;
            var tris = mesh.triangles;

            var sb = new StringBuilder();
            sb.AppendLine("# Baked from RaymarchSkeletonMeshBaker");
            foreach (var v in verts)
                sb.AppendLine($"v {(-v.x).ToString("G7")} {v.y.ToString("G7")} {v.z.ToString("G7")}");
            foreach (var n in norms)
                sb.AppendLine($"vn {(-n.x).ToString("G7")} {n.y.ToString("G7")} {n.z.ToString("G7")}");

            for (int i = 0; i < tris.Length; i += 3)
            {
                int i0 = tris[i] + 1, i1 = tris[i + 1] + 1, i2 = tris[i + 2] + 1;
                // Reverse winding to compensate for the X mirror above.
                sb.AppendLine($"f {i2}//{i2} {i1}//{i1} {i0}//{i0}");
            }

            File.WriteAllText(fullPath, sb.ToString());
        }
    }
}
