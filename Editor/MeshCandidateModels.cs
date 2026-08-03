using System;
using System.Collections.Generic;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal enum MobileContentMode
    {
        Keep,
        Exclude,
        ExcludeWithFallback
    }

    internal enum MeshCandidateStatus
    {
        Original,
        Recommended,
        Safe,
        ReviewRequired,
        HighRisk,
        Unavailable,
        Rejected
    }

    [Serializable]
    internal sealed class MeshCandidateQuality
    {
        [SerializeField, Range(-1, 100)] private int structuralIntegrity = -1;
        [SerializeField, Range(-1, 100)] private int silhouettePreservation = -1;
        [SerializeField, Range(-1, 100)] private int deformationQuality = -1;
        [SerializeField, Range(-1, 100)] private int blendShapeFidelity = -1;
        [SerializeField, Range(-1, 100)] private int normalQuality = -1;
        [SerializeField, Range(-1, 100)] private int uvStability = -1;
        [SerializeField, Range(-1, 100)] private int boneWeightIntegrity = -1;
        [SerializeField, Range(-1, 100)] private int visualEfficiency = -1;
        [SerializeField, TextArea] private string measurementNotes;

        public int StructuralIntegrity { get => structuralIntegrity; set => structuralIntegrity = value; }
        public int SilhouettePreservation { get => silhouettePreservation; set => silhouettePreservation = value; }
        public int DeformationQuality { get => deformationQuality; set => deformationQuality = value; }
        public int BlendShapeFidelity { get => blendShapeFidelity; set => blendShapeFidelity = value; }
        public int NormalQuality { get => normalQuality; set => normalQuality = value; }
        public int UvStability { get => uvStability; set => uvStability = value; }
        public int BoneWeightIntegrity { get => boneWeightIntegrity; set => boneWeightIntegrity = value; }
        public int VisualEfficiency { get => visualEfficiency; set => visualEfficiency = value; }
        public string MeasurementNotes { get => measurementNotes; set => measurementNotes = value; }
    }

    [Serializable]
    internal sealed class MeshCandidate
    {
        [SerializeField] private string id;
        [SerializeField] private string label;
        [SerializeField] private float requestedRatio;
        [SerializeField] private Mesh mesh;
        [SerializeField] private int triangleCount;
        [SerializeField] private int vertexCount;
        [SerializeField] private int connectedComponents;
        [SerializeField] private MeshCandidateStatus status;
        [SerializeField, TextArea] private string validationMessage;
        [SerializeField] private MeshCandidateQuality quality = new MeshCandidateQuality();

        public string Id { get => id; set => id = value; }
        public string Label { get => label; set => label = value; }
        public float RequestedRatio { get => requestedRatio; set => requestedRatio = value; }
        public Mesh Mesh { get => mesh; set => mesh = value; }
        public int TriangleCount { get => triangleCount; set => triangleCount = value; }
        public int VertexCount { get => vertexCount; set => vertexCount = value; }
        public int ConnectedComponents { get => connectedComponents; set => connectedComponents = value; }
        public MeshCandidateStatus Status { get => status; set => status = value; }
        public string ValidationMessage { get => validationMessage; set => validationMessage = value; }
        public MeshCandidateQuality Quality => quality;
        public bool CanSelect => mesh != null && status != MeshCandidateStatus.Rejected;
    }

    [Serializable]
    internal sealed class RendererIdentity
    {
        [SerializeField] private string meshAssetGuid;
        [SerializeField] private long meshLocalFileId;
        [SerializeField] private string hierarchyPath;
        [SerializeField] private string rendererName;
        [SerializeField] private string meshSignature;
        [SerializeField] private string boneSignature;
        [SerializeField] private string materialSignature;
        [SerializeField] private string blendShapeSignature;

        public string MeshAssetGuid { get => meshAssetGuid; set => meshAssetGuid = value; }
        public long MeshLocalFileId { get => meshLocalFileId; set => meshLocalFileId = value; }
        public string HierarchyPath { get => hierarchyPath; set => hierarchyPath = value; }
        public string RendererName { get => rendererName; set => rendererName = value; }
        public string MeshSignature { get => meshSignature; set => meshSignature = value; }
        public string BoneSignature { get => boneSignature; set => boneSignature = value; }
        public string MaterialSignature { get => materialSignature; set => materialSignature = value; }
        public string BlendShapeSignature { get => blendShapeSignature; set => blendShapeSignature = value; }
    }

    [Serializable]
    internal sealed class RendererMeshChoice
    {
        [SerializeField] private string transformPath;
        [SerializeField] private string displayName;
        [SerializeField] private bool skinned;
        [SerializeField] private Mesh sourceMesh;
        [SerializeField] private int sourceTriangleCount;
        [SerializeField] private int sourceVertexCount;
        [SerializeField] private int sourceBlendShapeCount;
        [SerializeField] private int sourceConnectedComponents;
        [SerializeField] private int sourceBoneCount;
        [SerializeField] private bool sourceReadable;
        [SerializeField, Range(0, 100)] private int reductionRisk;
        [SerializeField, TextArea] private string reductionRiskReason;
        [SerializeField] private RendererIdentity identity = new RendererIdentity();
        [SerializeField] private bool generateCandidates = true;
        [SerializeField] private int selectedCandidateIndex;
        [SerializeField] private bool selectionApproved;
        [SerializeField] private string approvedSelectionId;
        [SerializeField] private string approvedUtc;
        [SerializeField] private string candidateGenerationSignature;
        [SerializeField] private List<MeshCandidate> candidates = new List<MeshCandidate>();
        [SerializeField] private MobileContentMode mobileContentMode;
        [SerializeField] private string mobileFallbackTransformPath;
        [SerializeField] private int mobileActivationBindingCount;
        [SerializeField] private List<string> mobileActivationClipPaths = new List<string>();
        [SerializeField] private List<string> mobileFallbackBehaviorClipPaths = new List<string>();

        public string TransformPath { get => transformPath; set => transformPath = value; }
        public string DisplayName { get => displayName; set => displayName = value; }
        public bool Skinned { get => skinned; set => skinned = value; }
        public Mesh SourceMesh { get => sourceMesh; set => sourceMesh = value; }
        public int SourceTriangleCount { get => sourceTriangleCount; set => sourceTriangleCount = value; }
        public int SourceVertexCount { get => sourceVertexCount; set => sourceVertexCount = value; }
        public int SourceBlendShapeCount { get => sourceBlendShapeCount; set => sourceBlendShapeCount = value; }
        public int SourceConnectedComponents { get => sourceConnectedComponents; set => sourceConnectedComponents = value; }
        public int SourceBoneCount { get => sourceBoneCount; set => sourceBoneCount = value; }
        public bool SourceReadable { get => sourceReadable; set => sourceReadable = value; }
        public int ReductionRisk { get => reductionRisk; set => reductionRisk = value; }
        public string ReductionRiskReason { get => reductionRiskReason; set => reductionRiskReason = value; }
        public RendererIdentity Identity => identity;
        public bool GenerateCandidates { get => generateCandidates; set => generateCandidates = value; }
        public int SelectedCandidateIndex { get => selectedCandidateIndex; set => selectedCandidateIndex = value; }
        public bool SelectionApproved { get => selectionApproved; set => selectionApproved = value; }
        public string ApprovedSelectionId { get => approvedSelectionId; set => approvedSelectionId = value; }
        public string ApprovedUtc { get => approvedUtc; set => approvedUtc = value; }
        public string CandidateGenerationSignature { get => candidateGenerationSignature; set => candidateGenerationSignature = value; }
        public List<MeshCandidate> Candidates => candidates;
        public MobileContentMode MobileContentMode { get => mobileContentMode; set => mobileContentMode = value; }
        public string MobileFallbackTransformPath { get => mobileFallbackTransformPath; set => mobileFallbackTransformPath = value; }
        public int MobileActivationBindingCount { get => mobileActivationBindingCount; set => mobileActivationBindingCount = value; }
        public List<string> MobileActivationClipPaths => mobileActivationClipPaths;
        public List<string> MobileFallbackBehaviorClipPaths => mobileFallbackBehaviorClipPaths;
        public bool IsExcludedFromMobile => mobileContentMode != MobileContentMode.Keep;
        public bool RedirectsToFallback => mobileContentMode == MobileContentMode.ExcludeWithFallback;

        public MeshCandidate SelectedCandidate
        {
            get
            {
                if (candidates == null || candidates.Count == 0) return null;
                selectedCandidateIndex = Mathf.Clamp(selectedCandidateIndex, 0, candidates.Count - 1);
                return candidates[selectedCandidateIndex];
            }
        }

        public Mesh SelectedMesh => SelectedCandidate?.Mesh != null ? SelectedCandidate.Mesh : sourceMesh;
        public int SelectedTriangleCount => SelectedCandidate?.Mesh != null ? SelectedCandidate.TriangleCount : sourceTriangleCount;
        public string CurrentSelectionId => SelectedCandidate?.Id ?? "source";
        public bool IsCurrentSelectionApproved => selectionApproved &&
                                                  string.Equals(approvedSelectionId, CurrentSelectionId, StringComparison.Ordinal);

        public void ApproveCurrentSelection()
        {
            selectionApproved = true;
            approvedSelectionId = CurrentSelectionId;
            approvedUtc = DateTime.UtcNow.ToString("O");
        }

        public void RevokeApproval()
        {
            selectionApproved = false;
            approvedSelectionId = string.Empty;
            approvedUtc = string.Empty;
        }
    }

    [Serializable]
    internal sealed class BehaviorContractCategory
    {
        [SerializeField] private string name;
        [SerializeField] private int entryCount;
        [SerializeField, TextArea] private string summary;

        public string Name { get => name; set => name = value; }
        public int EntryCount { get => entryCount; set => entryCount = value; }
        public string Summary { get => summary; set => summary = value; }
    }

    [Serializable]
    internal sealed class AvatarBehaviorContract
    {
        [SerializeField] private string capturedUtc;
        [SerializeField] private string contractHash;
        [SerializeField] private string resolutionState = "Authoring prefab - unresolved";
        [SerializeField] private List<BehaviorContractCategory> categories = new List<BehaviorContractCategory>();
        [SerializeField] private List<string> detectedBuildSystems = new List<string>();
        [SerializeField] private List<string> warnings = new List<string>();

        public string CapturedUtc { get => capturedUtc; set => capturedUtc = value; }
        public string ContractHash { get => contractHash; set => contractHash = value; }
        public string ResolutionState { get => resolutionState; set => resolutionState = value; }
        public List<BehaviorContractCategory> Categories => categories;
        public List<string> DetectedBuildSystems => detectedBuildSystems;
        public List<string> Warnings => warnings;

        public int Count(string category)
        {
            var item = categories.Find(entry => string.Equals(entry.Name, category, StringComparison.Ordinal));
            return item?.EntryCount ?? 0;
        }
    }

}
