using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    /// <summary>
    /// Project-local development smoke test. The test prefab is selected at runtime, so no
    /// avatar path, renderer name, or conversion rule is part of the package.
    /// </summary>
    public static class MobileAvatarStudioSmokeTest
    {
        private static string testPrefabPath;
        private const string ReportPath = "Library/MobileAvatarStudioReports/SmokeTest.txt";

        [MenuItem("Tools/Mobile Avatar Studio/Development/Run Smoke Test")]
        public static void Run()
        {
            var absolutePrefabPath = EditorUtility.OpenFilePanel(
                "Select a prefab for the Mobile Avatar Studio smoke test", Application.dataPath, "prefab");
            if (string.IsNullOrEmpty(absolutePrefabPath)) return;
            testPrefabPath = ToAssetPath(absolutePrefabPath);
            if (string.IsNullOrEmpty(testPrefabPath))
                Fail("Select a prefab inside this Unity project's Assets folder.");

            var report = new StringBuilder();
            report.AppendLine("MOBILE AVATAR STUDIO - GENERIC SMOKE TEST");
            report.AppendLine("Generated: " + DateTime.UtcNow.ToString("O"));
            report.AppendLine("The test prefab was selected at runtime; no avatar path is embedded in this harness.");

            var before = CaptureProtectedHashes();
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(testPrefabPath);
            if (source == null) Fail("Missing test prefab: " + testPrefabPath);

            var recipe = MeshCandidatePipeline.Analyze(source);
            report.AppendLine($"ANALYZE meshes={recipe.RendererChoices.Count} contract={recipe.SourceBehaviorContract.ContractHash}");
            report.AppendLine("BUILD SYSTEMS " + string.Join(",", recipe.SourceBehaviorContract.DetectedBuildSystems));
            foreach (var category in recipe.SourceBehaviorContract.Categories)
                report.AppendLine($"CONTRACT {category.Name}={category.EntryCount} detail={category.Summary}");

            if (recipe.RendererChoices.Count < 1) Fail("Analyzer found no meshes.");
            if (string.IsNullOrEmpty(recipe.SourceBehaviorContract.ContractHash)) Fail("Behavior Contract has no hash.");

            foreach (var choice in recipe.RendererChoices) choice.GenerateCandidates = false;
            var highRisk = recipe.RendererChoices.OrderByDescending(choice => choice.ReductionRisk)
                .ThenByDescending(choice => choice.SourceTriangleCount).First();
            highRisk.GenerateCandidates = true;

            var backend = new AutoLodReflectionBackend();
            report.AppendLine($"BACKEND available={backend.IsAvailable} name={backend.Name} detail={backend.AvailabilityMessage}");
            if (!backend.IsAvailable) Fail(backend.AvailabilityMessage);

            var ignoredExpertDefaults = new[]
            {
                new MeshCandidatePipeline.CandidateLevel("VeryLight", "Very Light", 0.90f)
            };
            MeshCandidatePipeline.GenerateCandidates(recipe, ignoredExpertDefaults, backend, true);

            foreach (var choice in recipe.RendererChoices.Where(choice => choice.GenerateCandidates))
            {
                report.AppendLine($"MESH path={choice.TransformPath} risk={choice.ReductionRisk} reason={choice.ReductionRiskReason}");
                foreach (var candidate in choice.Candidates)
                {
                    report.AppendLine(
                        $"  CANDIDATE label={candidate.Label} requested={candidate.RequestedRatio:P0} " +
                        $"tris={candidate.TriangleCount} status={candidate.Status} structure={Format(candidate.Quality.StructuralIntegrity)} " +
                        $"silhouette={Format(candidate.Quality.SilhouettePreservation)} deform={Format(candidate.Quality.DeformationQuality)} " +
                        $"blendshape={Format(candidate.Quality.BlendShapeFidelity)} note={candidate.ValidationMessage}");
                }
                if (choice.Candidates.Count < 2) Fail("Candidate generation did not create a choice ladder for " + choice.TransformPath);
                if (choice.Candidates[0].Status != MeshCandidateStatus.Original) Fail("Original is not the first/default candidate.");
                if (choice.SelectedCandidateIndex != 0) Fail("A reduced candidate was silently selected.");
            }

            var prefabPath = MeshCandidatePipeline.BuildSelectedPrefab(recipe);
            report.AppendLine("DRAFT PREFAB " + prefabPath);
            report.AppendLine("DRAFT NOTICE Mesh selection only; visual approval and remaining conversion passes are incomplete.");

            if (recipe.MaterialChoices.Count == 0) Fail("Material analyzer found no material mappings.");
            foreach (var materialChoice in recipe.MaterialChoices) materialChoice.ApproveCurrentMapping();
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
            var materialPrefabPath = MaterialConversionPipeline.BuildMaterialDraft(recipe);
            var materialPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(materialPrefabPath);
            if (materialPrefab == null) Fail("Material draft was not created: " + materialPrefabPath);
            var invalidMaterialReferences = materialPrefab.GetComponentsInChildren<Renderer>(true)
                .SelectMany(renderer => renderer.sharedMaterials)
                .Where(material => material != null)
                .Where(material => material.shader == null || material.shader.name != MaterialConversionPipeline.TargetShaderName ||
                                   !AssetDatabase.GetAssetPath(material).StartsWith(recipe.OutputRoot + "/Materials/", StringComparison.Ordinal))
                .Select(material => material.name + " -> " + AssetDatabase.GetAssetPath(material))
                .Distinct()
                .ToArray();
            if (invalidMaterialReferences.Length > 0)
                Fail("Material draft has non-isolated or non-mobile materials: " + string.Join(", ", invalidMaterialReferences));
            report.AppendLine($"MATERIAL DRAFT {materialPrefabPath} materials={recipe.MaterialChoices.Count} isolated=PASS");
            report.AppendLine("MATERIAL POLICY one-to-one copies; no automatic merges or atlases.");

            var after = CaptureProtectedHashes();
            var changed = before.Keys.Union(after.Keys).Where(path =>
                !before.TryGetValue(path, out var beforeHash) ||
                !after.TryGetValue(path, out var afterHash) ||
                !string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (changed.Length > 0) Fail("Protected PC/Quest assets changed: " + string.Join(", ", changed));

            report.AppendLine($"SOURCE SAFETY protectedFiles={before.Count} changed=0 PASS");
            report.AppendLine("RESULT: SUCCESS");
            WriteReport(report.ToString());
            Debug.Log("Mobile Avatar Studio smoke test passed. Report: " + ReportPath);
        }

        public static void AuditLatestGeneratedDraft()
        {
            var recipePath = AssetDatabase.FindAssets("t:MobileAvatarMeshRecipe", new[] { "Assets/MobileAvatarStudioGenerated" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(recipePath)) Fail("No generated recipe exists.");
            var recipe = AssetDatabase.LoadAssetAtPath<MobileAvatarMeshRecipe>(recipePath);
            if (recipe == null) Fail("Latest recipe could not be loaded: " + recipePath);
            var prefabPath = !string.IsNullOrEmpty(recipe.CombinedQuestPrefabPath)
                ? recipe.CombinedQuestPrefabPath
                : recipe.OutputRoot + "/Checkpoints/Mesh/" +
                  MeshAnalysisUtility.SanitizeFileName(recipe.SourcePrefab.name + "_MobileMesh_DRAFT") + ".prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) Fail("Latest draft prefab is missing: " + prefabPath);

            var externalMeshes = new List<string>();
            foreach (var renderer in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var path = AssetDatabase.GetAssetPath(renderer.sharedMesh);
                if (!path.StartsWith(recipe.OutputRoot + "/", StringComparison.Ordinal)) externalMeshes.Add(renderer.name + " -> " + path);
            }
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.GetComponent<MeshRenderer>() == null) continue;
                var path = AssetDatabase.GetAssetPath(filter.sharedMesh);
                if (!path.StartsWith(recipe.OutputRoot + "/", StringComparison.Ordinal)) externalMeshes.Add(filter.name + " -> " + path);
            }
            if (externalMeshes.Count > 0) Fail("Draft prefab still references source meshes: " + string.Join(", ", externalMeshes));

            MeshCandidateReportWriter.Write(recipe, prefabPath);
            var text = new StringBuilder();
            text.AppendLine("MOBILE AVATAR STUDIO LATEST DRAFT AUDIT");
            text.AppendLine("Recipe: " + recipePath);
            text.AppendLine("Prefab: " + prefabPath);
            text.AppendLine("Meshes: " + recipe.RendererChoices.Count);
            text.AppendLine("External mesh references: 0");
            text.AppendLine("Behavior resolution: " + recipe.SourceBehaviorContract.ResolutionState);
            text.AppendLine("RESULT: SUCCESS");
            WriteReport(text.ToString());
            Debug.Log("Mobile Avatar Studio latest generated draft audit passed. Report: " + ReportPath);
        }

        private static Dictionary<string, string> CaptureProtectedHashes()
        {
            if (string.IsNullOrEmpty(testPrefabPath)) return new Dictionary<string, string>();
            return new[] { testPrefabPath }.ToDictionary(path => path, HashAssetFile, StringComparer.Ordinal);
        }

        private static string ToAssetPath(string absolutePath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            var normalizedRoot = projectRoot.Replace('\\', '/').TrimEnd('/');
            var normalizedPath = absolutePath.Replace('\\', '/');
            var assetsPrefix = normalizedRoot + "/Assets/";
            if (!normalizedPath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            return normalizedPath.Substring(normalizedRoot.Length + 1);
        }

        private static string HashAssetFile(string assetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            var absolute = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute)) return "<missing>";
            using (var stream = File.OpenRead(absolute))
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static string Format(int value) => value < 0 ? "not-measured" : value + "%";

        private static void WriteReport(string text)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            var absolute = Path.Combine(projectRoot, ReportPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute) ?? projectRoot);
            File.WriteAllText(absolute, text);
        }

        private static void Fail(string message)
        {
            throw new InvalidOperationException("Mobile Avatar Studio smoke test failed: " + message);
        }
    }
}
