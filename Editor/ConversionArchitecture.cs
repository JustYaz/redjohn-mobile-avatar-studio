using System;
using System.Collections.Generic;

namespace MobileAvatarStudio.Editor
{
    internal enum ContractImpactKind
    {
        Reads,
        Preserves,
        Changes,
        Approximates,
        Removes,
        Risks
    }

    [Serializable]
    internal sealed class ContractImpact
    {
        public ContractImpactKind kind;
        public string category;
        public string entry;
        public string explanation;
    }

    [Serializable]
    internal sealed class ConversionPassDeclaration
    {
        public string passId;
        public string displayName;
        public string version;
        public string inputCheckpoint;
        public string outputCheckpoint;
        public List<ContractImpact> contractImpacts = new List<ContractImpact>();
    }

    /// <summary>
    /// All mutating conversion stages must expose their contract impact before execution and write
    /// only to the supplied generated/checkpoint workspace.
    /// </summary>
    internal interface IAvatarConversionPass
    {
        ConversionPassDeclaration Describe();
        bool CanRun(ConversionContext context, out string reason);
        ConversionPassResult Run(ConversionContext context);
    }

    internal sealed class ConversionContext
    {
        public MobileAvatarMeshRecipe Recipe { get; set; }
        public string GeneratedWorkspace { get; set; }
        public string InputCheckpoint { get; set; }
        public bool ExpertMode { get; set; }
    }

    internal sealed class ConversionPassResult
    {
        public bool Success { get; set; }
        public string OutputCheckpoint { get; set; }
        public string CreatorSummary { get; set; }
        public string TechnicalReportPath { get; set; }
        public List<string> GeneratedAssets { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<ContractImpact> ActualContractImpacts { get; } = new List<ContractImpact>();
    }

    internal interface IMobileAvatarStudioExtension
    {
        string ExtensionId { get; }
        string DisplayName { get; }
        string Version { get; }
        string SupportedToolVersion { get; }
        IReadOnlyList<string> SupportedUnityVersions { get; }
        IReadOnlyList<string> SupportedSdkVersions { get; }
        bool WritesGeneratedData { get; }
        bool AddsRuntimeDependencies { get; }
    }

    [Serializable]
    internal sealed class ConversionHealth
    {
        public int sourceSafety = -1;
        public int behaviorPreservation = -1;
        public int visualApproval = -1;
        public int mobileCompliance = -1;
        public int buildReproducibility = -1;

        public bool IsReleaseReady => sourceSafety == 100 && behaviorPreservation == 100 &&
                                      visualApproval == 100 && mobileCompliance == 100 &&
                                      buildReproducibility == 100;
    }
}
