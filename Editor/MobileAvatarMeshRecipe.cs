using System.Collections.Generic;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    /// <summary>
    /// Persistent, versioned source identity and per-renderer mesh approval recipe. This type is
    /// public and lives in a matching file so Unity writes a stable MonoScript reference.
    /// </summary>
    public sealed class MobileAvatarMeshRecipe : ScriptableObject
    {
        [SerializeField] private GameObject sourcePrefab;
        [SerializeField] private string sourceAssetPath;
        [SerializeField] private string sourceGuid;
        [SerializeField] private string sourceFileHash;
        [SerializeField] private string generatedUtc;
        [SerializeField] private string backendName;
        [SerializeField] private string outputRoot;
        [SerializeField] private string toolVersion = "0.1.0";
        [SerializeField] private string unityVersion;
        [SerializeField] private string generatedMaterialPrefabPath;
        [SerializeField] private string materialPassUtc;
        [SerializeField] private string texturePassUtc;
        [SerializeField] private string meshGenerationState = "Not started";
        [SerializeField] private string meshGenerationCheckpointUtc;
        [SerializeField] private string combinedQuestPrefabPath;
        [SerializeField] private string finalAssemblyUtc;
        [SerializeField] private string finalAssemblyStatus = "Not built";
        [SerializeField] private string behaviorPassUtc;
        [SerializeField] private string behaviorStatus = "Not analyzed";
        [SerializeField] private string behaviorPrefabPath;
        [SerializeField] private bool behaviorAppliedToCombined;
        [SerializeField] private string mobileResolvedAuditUtc;
        [SerializeField] private string mobileResolvedAuditTarget;
        [SerializeField] private string mobileResolvedPrefabPath;
        [SerializeField] private string mobileResolvedPrefabHash;
        [SerializeField] private string mobileValidationStatus = "Not run";
        [SerializeField] private bool mobileResolvedAuditPassed;
        [SerializeField] private string mobileSdkBuildUtc;
        [SerializeField] private string mobileSdkBuildTarget;
        [SerializeField] private string mobileSdkBundlePath;
        [SerializeField] private long mobileSdkDownloadBytes;
        [SerializeField] private long mobileSdkUncompressedBytes = -1;
        [SerializeField] private string mobileContentAnalysisUtc;
        [SerializeField] private string mobileContentStatus = "Not analyzed";
        [SerializeField] private string mobileContentReportPath;
        [SerializeField] private string mobileComponentRestoreCachePath;
        [SerializeField] private string mobileComponentRestoreCacheUtc;
        [SerializeField] private string materialSlotCacheUtc;
        [SerializeField] private string materialSlotCacheSourcePrefabPath;
        [SerializeField] private string manualPolishCheckpointUtc;
        [SerializeField] private string manualPolishFinalScanUtc;
        [SerializeField] private string manualPolishDependencyHash;
        [SerializeField] private string manualPolishStatus = "Required after Stage 6";
        [SerializeField] private int manualPolishTextureCount;
        [SerializeField] private List<RendererMaterialSlotCacheEntry> materialSlotCache =
            new List<RendererMaterialSlotCacheEntry>();
        [SerializeField] private List<MobileValidationIssue> mobileValidationIssues = new List<MobileValidationIssue>();
        [SerializeField] private List<MobileComponentRepairChoice> mobileComponentRepairChoices =
            new List<MobileComponentRepairChoice>();
        [SerializeField] private AvatarBehaviorContract sourceBehaviorContract = new AvatarBehaviorContract();
        [SerializeField] private List<RendererMeshChoice> rendererChoices = new List<RendererMeshChoice>();
        [SerializeField] private List<UvTileSplitRenderer> uvTileSplitRenderers = new List<UvTileSplitRenderer>();
        [SerializeField] private string uvTileSplitScanUtc;
        [SerializeField] private string uvTileSplitStatus = "Not scanned";
        [SerializeField] private List<MaterialConversionChoice> materialChoices = new List<MaterialConversionChoice>();
        [SerializeField] private List<TextureConversionChoice> textureChoices = new List<TextureConversionChoice>();
        [SerializeField] private List<BehaviorCurveMappingChoice> behaviorCurveChoices = new List<BehaviorCurveMappingChoice>();

        internal GameObject SourcePrefab { get => sourcePrefab; set => sourcePrefab = value; }
        internal string SourceAssetPath { get => sourceAssetPath; set => sourceAssetPath = value; }
        internal string SourceGuid { get => sourceGuid; set => sourceGuid = value; }
        internal string SourceFileHash { get => sourceFileHash; set => sourceFileHash = value; }
        internal string GeneratedUtc { get => generatedUtc; set => generatedUtc = value; }
        internal string BackendName { get => backendName; set => backendName = value; }
        internal string OutputRoot { get => outputRoot; set => outputRoot = value; }
        internal string ToolVersion { get => toolVersion; set => toolVersion = value; }
        internal string UnityVersion { get => unityVersion; set => unityVersion = value; }
        internal string GeneratedMaterialPrefabPath { get => generatedMaterialPrefabPath; set => generatedMaterialPrefabPath = value; }
        internal string MaterialPassUtc { get => materialPassUtc; set => materialPassUtc = value; }
        internal string TexturePassUtc { get => texturePassUtc; set => texturePassUtc = value; }
        internal string MeshGenerationState { get => meshGenerationState; set => meshGenerationState = value; }
        internal string MeshGenerationCheckpointUtc { get => meshGenerationCheckpointUtc; set => meshGenerationCheckpointUtc = value; }
        internal string CombinedQuestPrefabPath { get => combinedQuestPrefabPath; set => combinedQuestPrefabPath = value; }
        internal string FinalAssemblyUtc { get => finalAssemblyUtc; set => finalAssemblyUtc = value; }
        internal string FinalAssemblyStatus { get => finalAssemblyStatus; set => finalAssemblyStatus = value; }
        internal string BehaviorPassUtc { get => behaviorPassUtc; set => behaviorPassUtc = value; }
        internal string BehaviorStatus { get => behaviorStatus; set => behaviorStatus = value; }
        internal string BehaviorPrefabPath { get => behaviorPrefabPath; set => behaviorPrefabPath = value; }
        internal bool BehaviorAppliedToCombined { get => behaviorAppliedToCombined; set => behaviorAppliedToCombined = value; }
        internal string MobileResolvedAuditUtc { get => mobileResolvedAuditUtc; set => mobileResolvedAuditUtc = value; }
        internal string MobileResolvedAuditTarget { get => mobileResolvedAuditTarget; set => mobileResolvedAuditTarget = value; }
        internal string MobileResolvedPrefabPath { get => mobileResolvedPrefabPath; set => mobileResolvedPrefabPath = value; }
        internal string MobileResolvedPrefabHash { get => mobileResolvedPrefabHash; set => mobileResolvedPrefabHash = value; }
        internal string MobileValidationStatus { get => mobileValidationStatus; set => mobileValidationStatus = value; }
        internal bool MobileResolvedAuditPassed { get => mobileResolvedAuditPassed; set => mobileResolvedAuditPassed = value; }
        internal string MobileSdkBuildUtc { get => mobileSdkBuildUtc; set => mobileSdkBuildUtc = value; }
        internal string MobileSdkBuildTarget { get => mobileSdkBuildTarget; set => mobileSdkBuildTarget = value; }
        internal string MobileSdkBundlePath { get => mobileSdkBundlePath; set => mobileSdkBundlePath = value; }
        internal long MobileSdkDownloadBytes { get => mobileSdkDownloadBytes; set => mobileSdkDownloadBytes = value; }
        internal long MobileSdkUncompressedBytes { get => mobileSdkUncompressedBytes; set => mobileSdkUncompressedBytes = value; }
        internal string MobileContentAnalysisUtc { get => mobileContentAnalysisUtc; set => mobileContentAnalysisUtc = value; }
        internal string MobileContentStatus { get => mobileContentStatus; set => mobileContentStatus = value; }
        internal string MobileContentReportPath { get => mobileContentReportPath; set => mobileContentReportPath = value; }
        internal string MobileComponentRestoreCachePath
        {
            get => mobileComponentRestoreCachePath;
            set => mobileComponentRestoreCachePath = value;
        }
        internal string MobileComponentRestoreCacheUtc
        {
            get => mobileComponentRestoreCacheUtc;
            set => mobileComponentRestoreCacheUtc = value;
        }
        internal string MaterialSlotCacheUtc { get => materialSlotCacheUtc; set => materialSlotCacheUtc = value; }
        internal string MaterialSlotCacheSourcePrefabPath
        {
            get => materialSlotCacheSourcePrefabPath;
            set => materialSlotCacheSourcePrefabPath = value;
        }
        internal string ManualPolishCheckpointUtc
        {
            get => manualPolishCheckpointUtc;
            set => manualPolishCheckpointUtc = value;
        }
        internal string ManualPolishFinalScanUtc
        {
            get => manualPolishFinalScanUtc;
            set => manualPolishFinalScanUtc = value;
        }
        internal string ManualPolishDependencyHash
        {
            get => manualPolishDependencyHash;
            set => manualPolishDependencyHash = value;
        }
        internal string ManualPolishStatus { get => manualPolishStatus; set => manualPolishStatus = value; }
        internal int ManualPolishTextureCount { get => manualPolishTextureCount; set => manualPolishTextureCount = value; }
        internal List<RendererMaterialSlotCacheEntry> MaterialSlotCache => materialSlotCache;
        internal AvatarBehaviorContract SourceBehaviorContract => sourceBehaviorContract;
        internal List<RendererMeshChoice> RendererChoices => rendererChoices;
        internal List<UvTileSplitRenderer> UvTileSplitRenderers =>
            uvTileSplitRenderers ?? (uvTileSplitRenderers = new List<UvTileSplitRenderer>());
        internal string UvTileSplitScanUtc { get => uvTileSplitScanUtc; set => uvTileSplitScanUtc = value; }
        internal string UvTileSplitStatus { get => uvTileSplitStatus; set => uvTileSplitStatus = value; }
        internal List<MaterialConversionChoice> MaterialChoices => materialChoices;
        internal List<TextureConversionChoice> TextureChoices => textureChoices;
        internal List<BehaviorCurveMappingChoice> BehaviorCurveChoices => behaviorCurveChoices;
        internal List<MobileValidationIssue> MobileValidationIssues => mobileValidationIssues;
        internal List<MobileComponentRepairChoice> MobileComponentRepairChoices => mobileComponentRepairChoices;
    }
}
