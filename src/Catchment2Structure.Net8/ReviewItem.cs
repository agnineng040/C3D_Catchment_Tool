using System.Collections.Generic;

using Autodesk.AutoCAD.DatabaseServices;

namespace Catchment2Structure
{
    public enum ReviewItemType
    {
        MultipleStructures,
        NoStructureInside
    }

    public sealed class ReviewItem
    {
        public ReviewItemType Type { get; init; }

        public ObjectId CatchmentId { get; init; }
        public string CatchmentName { get; init; } = string.Empty;

        public List<ObjectId> CandidateStructureIds { get; init; } = new();
        public ObjectId NearestCandidateId { get; init; } = ObjectId.Null;

        // Resolution (set by ReviewWindow)
        public bool IsResolved { get; set; }
        public bool IsAssigned { get; set; }
        public ObjectId AssignedStructureId { get; set; } = ObjectId.Null;
        public string AssignedStructureName { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;

        public string TypeLabel =>
            Type == ReviewItemType.MultipleStructures ? "Multiple structures inside" : "No structure inside";

        public int CandidateCount => CandidateStructureIds?.Count ?? 0;
    }
}
