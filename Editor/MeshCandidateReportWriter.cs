using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal static class MeshCandidateReportWriter
    {
        [Serializable]
        private sealed class MachineReport
        {
            public string toolVersion;
            public string unityVersion;
            public string sourcePrefab;
            public string sourceGuid;
            public string sourceHash;
            public string behaviorContractHash;
            public string behaviorResolution;
            public string backend;
            public string outputRoot;
            public bool draft = true;
            public List<MachineContractCategory> contract = new List<MachineContractCategory>();
            public List<MachineRenderer> renderers = new List<MachineRenderer>();
        }

        [Serializable]
        private sealed class MachineContractCategory
        {
            public string name;
            public int count;
            public string summary;
        }

        [Serializable]
        private sealed class MachineRenderer
        {
            public string path;
            public string mobileContentMode;
            public string mobileFallbackPath;
            public string meshGuid;
            public long meshLocalFileId;
            public string meshSignature;
            public string boneSignature;
            public string materialSignature;
            public string blendShapeSignature;
            public int reductionRisk;
            public string reductionRiskReason;
            public string selectedCandidate;
            public List<MachineCandidate> candidates = new List<MachineCandidate>();
        }

        [Serializable]
        private sealed class MachineCandidate
        {
            public string id;
            public string label;
            public float requestedRatio;
            public int triangles;
            public int vertices;
            public string status;
            public string assetPath;
            public string validation;
            public int structuralIntegrity;
            public int silhouettePreservation;
            public int deformationQuality;
            public int blendShapeFidelity;
            public int normalQuality;
            public int uvStability;
            public int boneWeightIntegrity;
            public int visualEfficiency;
        }

        public static void Write(MobileAvatarMeshRecipe recipe, string builtPrefabPath = null)
        {
            if (recipe == null || string.IsNullOrEmpty(recipe.OutputRoot)) return;
            var reportsRoot = recipe.OutputRoot + "/Reports";
            MeshAnalysisUtility.EnsureAssetFolder(reportsRoot);

            WriteAssetText(reportsRoot + "/CreatorReport.md", BuildCreatorReport(recipe, builtPrefabPath));
            WriteAssetText(reportsRoot + "/TechnicalReport.txt", BuildTechnicalReport(recipe, builtPrefabPath));
            WriteAssetText(reportsRoot + "/MachineReport.json", JsonUtility.ToJson(BuildMachineReport(recipe), true));
            AssetDatabase.Refresh();
        }

        private static string BuildCreatorReport(MobileAvatarMeshRecipe recipe, string builtPrefabPath)
        {
            var retained = UvTileSplitPipeline.GetEffectiveMeshChoices(recipe).ToArray();
            var original = retained.Sum(choice => (long)choice.SourceTriangleCount);
            var selected = retained.Sum(choice => (long)choice.SelectedTriangleCount);
            var generated = retained.Count(choice => choice.Candidates.Count > 0);
            var approved = retained.Count(choice => choice.IsCurrentSelectionApproved);
            var text = new StringBuilder();
            text.AppendLine("# Mobile Avatar Studio mesh-stage creator report");
            text.AppendLine();
            text.AppendLine("**Status: Draft - not a completed mobile conversion.**");
            text.AppendLine();
            text.AppendLine($"- Source: `{recipe.SourceAssetPath}`");
            text.AppendLine($"- Behavior Contract: `{recipe.SourceBehaviorContract.ContractHash}`");
            text.AppendLine($"- Contract resolution: {recipe.SourceBehaviorContract.ResolutionState}");
            text.AppendLine($"- Effective meshes: {retained.Length}");
            text.AppendLine($"- Retained mobile meshes: {retained.Length}");
            text.AppendLine($"- Excluded source renderers: {recipe.RendererChoices.Count(choice => choice.IsExcludedFromMobile)}");
            text.AppendLine($"- Meshes with generated choices: {generated}");
            text.AppendLine($"- Explicitly approved selections: {approved}/{retained.Length}");
            text.AppendLine($"- Original triangles: {original:N0}");
            text.AppendLine($"- Currently selected triangles: {selected:N0}");
            if (!string.IsNullOrEmpty(builtPrefabPath)) text.AppendLine($"- Draft prefab: `{builtPrefabPath}`");
            text.AppendLine();
            text.AppendLine("## Important limitations");
            text.AppendLine();
            text.AppendLine("- Silhouette, deformation, and blendshape visual fidelity remain unmeasured until their visual regression passes are implemented and approved.");
            text.AppendLine("- Shader conversion, atlasing, animation remapping, dynamics optimization, Android/iOS build measurement, and final contract diff have not run.");
            text.AppendLine("- No candidate is silently approved; Original remains the default after generation.");
            return text.ToString();
        }

        private static string BuildTechnicalReport(MobileAvatarMeshRecipe recipe, string builtPrefabPath)
        {
            var text = new StringBuilder();
            text.AppendLine("MOBILE AVATAR STUDIO MESH-STAGE TECHNICAL REPORT");
            text.AppendLine("Generated: " + DateTime.UtcNow.ToString("O"));
            text.AppendLine("Source: " + recipe.SourceAssetPath);
            text.AppendLine("Source GUID: " + recipe.SourceGuid);
            text.AppendLine("Source hash: " + recipe.SourceFileHash);
            text.AppendLine("Behavior Contract: " + recipe.SourceBehaviorContract.ContractHash);
            text.AppendLine("Resolution: " + recipe.SourceBehaviorContract.ResolutionState);
            text.AppendLine("Backend: " + recipe.BackendName);
            if (!string.IsNullOrEmpty(builtPrefabPath)) text.AppendLine("Draft prefab: " + builtPrefabPath);
            foreach (var choice in UvTileSplitPipeline.GetEffectiveMeshChoices(recipe))
            {
                text.AppendLine($"RENDERER path={choice.TransformPath} tris={choice.SourceTriangleCount} vertices={choice.SourceVertexCount} " +
                                $"blendshapes={choice.SourceBlendShapeCount} bones={choice.SourceBoneCount} components={choice.SourceConnectedComponents} " +
                                $"risk={choice.ReductionRisk} meshSignature={choice.Identity.MeshSignature} " +
                                $"mobileContent={choice.MobileContentMode} fallback={choice.MobileFallbackTransformPath}");
                text.AppendLine("  RISK " + choice.ReductionRiskReason);
                foreach (var candidate in choice.Candidates)
                    text.AppendLine($"  CANDIDATE id={candidate.Id} ratio={candidate.RequestedRatio:R} tris={candidate.TriangleCount} " +
                                    $"status={candidate.Status} asset={AssetDatabase.GetAssetPath(candidate.Mesh)} validation={candidate.ValidationMessage}");
            }
            return text.ToString();
        }

        private static MachineReport BuildMachineReport(MobileAvatarMeshRecipe recipe)
        {
            var report = new MachineReport
            {
                toolVersion = recipe.ToolVersion,
                unityVersion = recipe.UnityVersion,
                sourcePrefab = recipe.SourceAssetPath,
                sourceGuid = recipe.SourceGuid,
                sourceHash = recipe.SourceFileHash,
                behaviorContractHash = recipe.SourceBehaviorContract.ContractHash,
                behaviorResolution = recipe.SourceBehaviorContract.ResolutionState,
                backend = recipe.BackendName,
                outputRoot = recipe.OutputRoot
            };
            foreach (var category in recipe.SourceBehaviorContract.Categories)
                report.contract.Add(new MachineContractCategory { name = category.Name, count = category.EntryCount, summary = category.Summary });
            foreach (var choice in UvTileSplitPipeline.GetEffectiveMeshChoices(recipe))
            {
                var renderer = new MachineRenderer
                {
                    path = choice.TransformPath,
                    mobileContentMode = choice.MobileContentMode.ToString(),
                    mobileFallbackPath = choice.MobileFallbackTransformPath,
                    meshGuid = choice.Identity.MeshAssetGuid,
                    meshLocalFileId = choice.Identity.MeshLocalFileId,
                    meshSignature = choice.Identity.MeshSignature,
                    boneSignature = choice.Identity.BoneSignature,
                    materialSignature = choice.Identity.MaterialSignature,
                    blendShapeSignature = choice.Identity.BlendShapeSignature,
                    reductionRisk = choice.ReductionRisk,
                    reductionRiskReason = choice.ReductionRiskReason,
                    selectedCandidate = choice.SelectedCandidate?.Id ?? "source-pending-isolated-copy"
                };
                foreach (var candidate in choice.Candidates)
                {
                    renderer.candidates.Add(new MachineCandidate
                    {
                        id = candidate.Id,
                        label = candidate.Label,
                        requestedRatio = candidate.RequestedRatio,
                        triangles = candidate.TriangleCount,
                        vertices = candidate.VertexCount,
                        status = candidate.Status.ToString(),
                        assetPath = AssetDatabase.GetAssetPath(candidate.Mesh),
                        validation = candidate.ValidationMessage,
                        structuralIntegrity = candidate.Quality.StructuralIntegrity,
                        silhouettePreservation = candidate.Quality.SilhouettePreservation,
                        deformationQuality = candidate.Quality.DeformationQuality,
                        blendShapeFidelity = candidate.Quality.BlendShapeFidelity,
                        normalQuality = candidate.Quality.NormalQuality,
                        uvStability = candidate.Quality.UvStability,
                        boneWeightIntegrity = candidate.Quality.BoneWeightIntegrity,
                        visualEfficiency = candidate.Quality.VisualEfficiency
                    });
                }
                report.renderers.Add(renderer);
            }
            return report;
        }

        private static void WriteAssetText(string assetPath, string text)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            var absolute = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute) ?? projectRoot);
            File.WriteAllText(absolute, text, new UTF8Encoding(false));
        }
    }
}
