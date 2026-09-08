using System;
using System.Collections.Generic;
using UnityEngine;

namespace Premiere.RaymarchSkeleton
{
    /// <summary>
    /// Skeleton joint topology: which joints are used, and which joints
    /// each joint connects to (its children). This is a direct analogue
    /// of the "jointFilter" / "jointConnectivity" arrays in
    /// joint_settings.json / hand_joint_settings.json / swarm_joint_settings.json
    /// from the original Python tool, so those same JSON files can be
    /// reused here.
    /// </summary>
    [CreateAssetMenu(menuName = "PREMIERE/Raymarch Skeleton/Skeleton Topology", fileName = "SkeletonTopology")]
    public class SkeletonTopology : ScriptableObject
    {
        [Tooltip("Indices, into the incoming joint position array, of the joints that are used by this topology (in local joint order).")]
        public int[] jointFilter;

        [Tooltip("For each local joint (same order as jointFilter), the local indices of its child joints - one entry per bone/edge.")]
        public int[][] jointConnectivity;

        [Tooltip("Optional per-joint rotation correction (Euler degrees), one entry per joint in jointFilter order. Mirrors \"jointRotCorrections\" in the original *_joint_settings.json configs (e.g. xsens_joint_settings.json) - those store radians, which FromJson() converts to degrees for Unity's Quaternion.Euler. Leave empty if the raw incoming joint rotations don't need correcting.")]
        public Vector3[] jointRotCorrections;

        public int JointCount => jointFilter?.Length ?? 0;

        public int EdgeCount
        {
            get
            {
                int count = 0;
                if (jointConnectivity != null)
                {
                    foreach (var children in jointConnectivity)
                        count += children?.Length ?? 0;
                }
                return count;
            }
        }

        /// <summary>
        /// Parses a JSON file in the same shape as joint_settings.json:
        /// { "jointFilter": [...], "jointConnectivity": [[...], [...], ...] }
        /// Unity's built-in JsonUtility does not support jagged arrays, so
        /// this uses a small hand-rolled reader instead of a JSON library.
        /// </summary>
        public static SkeletonTopology FromJson(string json)
        {
            var topology = CreateInstance<SkeletonTopology>();
            topology.jointFilter = ExtractIntArray(json, "jointFilter");
            topology.jointConnectivity = ExtractJaggedIntArray(json, "jointConnectivity");
            topology.jointRotCorrections = ExtractVector3ArrayRadiansToDegrees(json, "jointRotCorrections");
            return topology;
        }

        private static int[] ExtractIntArray(string json, string key)
        {
            int start = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (start < 0) return Array.Empty<int>();
            int arrStart = json.IndexOf('[', start);
            int arrEnd = FindMatchingBracket(json, arrStart);
            string inner = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
            return ParseIntList(inner);
        }

        private static int[][] ExtractJaggedIntArray(string json, string key)
        {
            int start = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (start < 0) return Array.Empty<int[]>();
            int arrStart = json.IndexOf('[', start);
            int arrEnd = FindMatchingBracket(json, arrStart);
            string inner = json.Substring(arrStart + 1, arrEnd - arrStart - 1);

            var result = new List<int[]>();
            int depth = 0, segStart = -1;
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '[')
                {
                    if (depth == 0) segStart = i + 1;
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        result.Add(ParseIntList(inner.Substring(segStart, i - segStart)));
                    }
                }
            }
            return result.ToArray();
        }

        private static int[] ParseIntList(string s)
        {
            var parts = s.Split(new[] { ',', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<int>(parts.Length);
            foreach (var p in parts)
            {
                if (int.TryParse(p, out int v)) list.Add(v);
            }
            return list.ToArray();
        }

        /// <summary>
        /// Reads a "key": [[x,y,z], [x,y,z], ...] block (values in radians,
        /// as stored by the original tool's euler2quat-based configs) and
        /// returns it as Vector3s in degrees, ready for Quaternion.Euler.
        /// Returns an empty array if the key isn't present.
        /// </summary>
        private static Vector3[] ExtractVector3ArrayRadiansToDegrees(string json, string key)
        {
            int start = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (start < 0) return Array.Empty<Vector3>();
            int arrStart = json.IndexOf('[', start);
            int arrEnd = FindMatchingBracket(json, arrStart);
            string inner = json.Substring(arrStart + 1, arrEnd - arrStart - 1);

            var result = new List<Vector3>();
            int depth = 0, segStart = -1;
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '[')
                {
                    if (depth == 0) segStart = i + 1;
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        float[] xyz = ParseFloatList(inner.Substring(segStart, i - segStart));
                        Vector3 rad = new Vector3(
                            xyz.Length > 0 ? xyz[0] : 0f,
                            xyz.Length > 1 ? xyz[1] : 0f,
                            xyz.Length > 2 ? xyz[2] : 0f);
                        result.Add(rad * Mathf.Rad2Deg);
                    }
                }
            }
            return result.ToArray();
        }

        private static float[] ParseFloatList(string s)
        {
            var parts = s.Split(new[] { ',', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<float>(parts.Length);
            foreach (var p in parts)
            {
                if (float.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                    list.Add(v);
            }
            return list.ToArray();
        }

        private static int FindMatchingBracket(string json, int openIndex)
        {
            int depth = 0;
            for (int i = openIndex; i < json.Length; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return json.Length - 1;
        }
    }
}
