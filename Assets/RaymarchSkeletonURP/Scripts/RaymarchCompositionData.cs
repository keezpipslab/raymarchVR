using System;
using UnityEngine;

namespace Premiere.RaymarchSkeleton
{
    /// <summary>
    /// JSON shape written by the companion project's CompositionExporter -
    /// one file per export. JsonUtility deserialises Vector3/Quaternion
    /// natively (matching field names x/y/z/w), so no hand-rolled parsing
    /// is needed here (unlike SkeletonTopology's jagged-array JSON).
    ///
    /// Example:
    /// {
    ///   "exportedAtUtc": "2026-09-23T14:02:11.0000000Z",
    ///   "elements": [
    ///     {
    ///       "kind": "Box",
    ///       "size": { "x": 0.15, "y": 0.15, "z": 0.15 },
    ///       "anchor": "Joint_LeftLowerArm",
    ///       "localPosition": { "x": 0.02, "y": -0.01, "z": 0.0 },
    ///       "localRotation": { "x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0 }
    ///     }
    ///   ]
    /// }
    /// </summary>
    [Serializable]
    public class RaymarchCompositionElementData
    {
        public string kind;
        public Vector3 size;
        public string anchor;
        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
    }

    [Serializable]
    public class RaymarchCompositionExportData
    {
        public string exportedAtUtc;
        public RaymarchCompositionElementData[] elements;
    }
}
