using System;
using System.Collections.Generic;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal enum MaterialConversionRisk
    {
        Low,
        ReviewRequired
    }

    internal enum MaterialSurfaceClassification
    {
        Opaque,
        Cutout,
        TransparentMesh,
        ParticleAdditive,
        ParticleMultiply
    }

    internal enum MaterialRecommendationConfidence
    {
        High,
        Medium,
        NeedsVisualReview
    }

    [Serializable]
    internal sealed class MaterialConversionChoice
    {
        [SerializeField] private Material sourceMaterial;
        [SerializeField] private string sourceAssetPath;
        [SerializeField] private string sourceGuid;
        [SerializeField] private long sourceLocalFileId;
        [SerializeField] private string sourceSignature;
        [SerializeField] private string sourceShaderName;
        [SerializeField] private string targetShaderName = "VRChat/Mobile/Toon Standard";
        [SerializeField] private string recommendedShaderName = "VRChat/Mobile/Toon Standard";
        [SerializeField] private MaterialSurfaceClassification surfaceClassification;
        [SerializeField] private MaterialRecommendationConfidence recommendationConfidence;
        [SerializeField, TextArea] private string recommendationSummary;
        [SerializeField] private int sourceRenderQueue;
        [SerializeField] private bool usedByParticleRenderer;
        [SerializeField] private int rendererUsageCount;
        [SerializeField] private int animatedBindingCount;
        [SerializeField] private bool transparencyRisk;
        [SerializeField] private MaterialConversionRisk risk;
        [SerializeField, TextArea] private string riskSummary;
        [SerializeField] private List<string> rendererPaths = new List<string>();
        [SerializeField] private bool approved;
        [SerializeField] private string approvedSourceSignature;
        [SerializeField] private string approvedUtc;
        [SerializeField] private Material generatedMaterial;

        public Material SourceMaterial { get => sourceMaterial; set => sourceMaterial = value; }
        public string SourceAssetPath { get => sourceAssetPath; set => sourceAssetPath = value; }
        public string SourceGuid { get => sourceGuid; set => sourceGuid = value; }
        public long SourceLocalFileId { get => sourceLocalFileId; set => sourceLocalFileId = value; }
        public string SourceSignature { get => sourceSignature; set => sourceSignature = value; }
        public string SourceShaderName { get => sourceShaderName; set => sourceShaderName = value; }
        public string TargetShaderName { get => targetShaderName; set => targetShaderName = value; }
        public string RecommendedShaderName { get => recommendedShaderName; set => recommendedShaderName = value; }
        public MaterialSurfaceClassification SurfaceClassification
        {
            get => surfaceClassification;
            set => surfaceClassification = value;
        }
        public MaterialRecommendationConfidence RecommendationConfidence
        {
            get => recommendationConfidence;
            set => recommendationConfidence = value;
        }
        public string RecommendationSummary { get => recommendationSummary; set => recommendationSummary = value; }
        public int SourceRenderQueue { get => sourceRenderQueue; set => sourceRenderQueue = value; }
        public bool UsedByParticleRenderer { get => usedByParticleRenderer; set => usedByParticleRenderer = value; }
        public int RendererUsageCount { get => rendererUsageCount; set => rendererUsageCount = value; }
        public int AnimatedBindingCount { get => animatedBindingCount; set => animatedBindingCount = value; }
        public bool TransparencyRisk { get => transparencyRisk; set => transparencyRisk = value; }
        public MaterialConversionRisk Risk { get => risk; set => risk = value; }
        public string RiskSummary { get => riskSummary; set => riskSummary = value; }
        public List<string> RendererPaths => rendererPaths;
        public Material GeneratedMaterial { get => generatedMaterial; set => generatedMaterial = value; }
        public bool IsCurrentMappingApproved => approved &&
                                                string.Equals(approvedSourceSignature, CurrentMappingSignature,
                                                    StringComparison.Ordinal);

        private string CurrentMappingSignature =>
            (sourceSignature ?? string.Empty) + "|target=" + (targetShaderName ?? string.Empty);

        public void ApproveCurrentMapping()
        {
            approved = true;
            approvedSourceSignature = CurrentMappingSignature;
            approvedUtc = DateTime.UtcNow.ToString("O");
        }

        public void RevokeApproval()
        {
            approved = false;
            approvedSourceSignature = string.Empty;
            approvedUtc = string.Empty;
        }
    }
}
