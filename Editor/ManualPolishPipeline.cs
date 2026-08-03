using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal sealed class ManualPolishCheckpointResult
    {
        public int RendererCount;
        public int MaterialCount;
        public int TextureCount;
        public int NewTextureCount;
        public int SanitizedMaterialCount;
        public bool Complete;
        public string BlockingReason;
    }

    internal static class ManualPolishPipeline
    {
        public static void Invalidate(MobileAvatarMeshRecipe recipe, string status)
        {
            if (recipe == null) return;
            recipe.ManualPolishCheckpointUtc = string.Empty;
            recipe.ManualPolishFinalScanUtc = string.Empty;
            recipe.ManualPolishDependencyHash = string.Empty;
            recipe.ManualPolishTextureCount = 0;
            recipe.ManualPolishStatus = string.IsNullOrEmpty(status)
                ? "Manual polish checkpoint required after Stage 6"
                : status;
            recipe.MobileResolvedAuditPassed = false;
            recipe.MobileValidationStatus = recipe.ManualPolishStatus;
            EditorUtility.SetDirty(recipe);
        }

        public static ManualPolishCheckpointResult SaveAndRescan(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            if (!recipe.BehaviorAppliedToCombined)
                throw new InvalidOperationException("Complete Stage 6 behavior isolation before saving manual polish work.");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            if (prefab == null)
                throw new InvalidOperationException("The combined mobile prefab is missing. Rebuild Stage 5 and Stage 6.");

            Invalidate(recipe, "Saving manual material work and rescanning final dependencies");
            AssetDatabase.SaveAssets();

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var editingTarget = stage != null &&
                                string.Equals(stage.assetPath, recipe.CombinedQuestPrefabPath,
                                    StringComparison.OrdinalIgnoreCase);
            var editableRoot = editingTarget ? stage.prefabContentsRoot : null;
            FinalAssemblyPipeline.CaptureAndIsolateCurrentMaterialSlots(recipe, editableRoot);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            var sanitizedMaterialCount = MobileMaterialSanitizer.SanitizeOwnedMaterialAssets(prefab,
                recipe.OutputRoot);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var previousIds = new HashSet<string>(recipe.TextureChoices.Select(TextureIdentity),
                StringComparer.Ordinal);
            TextureConversionPipeline.AnalyzeFinalPrefab(recipe);
            recipe.ManualPolishFinalScanUtc = DateTime.UtcNow.ToString("O");
            var newTextures = recipe.TextureChoices.Count(choice => !previousIds.Contains(TextureIdentity(choice)));

            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            var result = new ManualPolishCheckpointResult
            {
                RendererCount = prefab.GetComponentsInChildren<Renderer>(true).Length,
                MaterialCount = prefab.GetComponentsInChildren<Renderer>(true)
                    .SelectMany(renderer => renderer.sharedMaterials)
                    .Where(material => material != null)
                    .Distinct().Count(),
                TextureCount = recipe.TextureChoices.Count,
                NewTextureCount = newTextures,
                SanitizedMaterialCount = sanitizedMaterialCount
            };

            if (!TextureConversionPipeline.ValidateAppliedMobileOverrides(recipe, out var reason))
            {
                result.BlockingReason = reason;
                recipe.ManualPolishStatus = newTextures > 0
                    ? $"Found {newTextures} new texture(s); review and apply their Android/iOS settings"
                    : "Final textures require review or Android/iOS overrides";
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();
                return result;
            }

            recipe.ManualPolishTextureCount = recipe.TextureChoices.Count;
            recipe.ManualPolishDependencyHash = ComputeDependencyHash(recipe);
            recipe.ManualPolishCheckpointUtc = DateTime.UtcNow.ToString("O");
            recipe.ManualPolishStatus = newTextures > 0
                ? $"Saved and verified; {newTextures} newly discovered texture(s) included"
                : "Saved and verified; final Android/iOS texture scan is clean";
            recipe.MobileResolvedAuditPassed = false;
            recipe.MobileValidationStatus = "Manual polish saved; Stage 7 resolved audit required";
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
            result.Complete = true;
            return result;
        }

        public static bool ValidateCheckpoint(MobileAvatarMeshRecipe recipe, out string reason)
        {
            if (recipe == null || !recipe.BehaviorAppliedToCombined)
            {
                reason = "Complete Stage 6 behavior isolation first.";
                return false;
            }
            if (string.IsNullOrEmpty(recipe.ManualPolishCheckpointUtc) ||
                string.IsNullOrEmpty(recipe.ManualPolishDependencyHash))
            {
                reason = "Finish the manual material work, then click Save Manual Work & Rescan in Stage 6.";
                return false;
            }
            if (recipe.ManualPolishTextureCount != recipe.TextureChoices.Count)
            {
                reason = "The final texture list changed after the manual checkpoint. Save and rescan Stage 6 again.";
                return false;
            }
            if (recipe.TextureChoices.Any(choice => !choice.IsCurrentSettingsApproved ||
                                                    !choice.MobileOverridesApplied))
            {
                reason = "One or more final texture settings still require approval and Android/iOS application.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public static bool ValidateCheckpointForExecution(MobileAvatarMeshRecipe recipe, out string reason)
        {
            if (!ValidateCheckpoint(recipe, out reason)) return false;
            if (!TextureConversionPipeline.ValidateAppliedMobileOverrides(recipe, out reason)) return false;
            var currentHash = ComputeDependencyHash(recipe);
            if (!string.Equals(currentHash, recipe.ManualPolishDependencyHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "The polished prefab or one of its materials changed after the checkpoint. Save and rescan Stage 6 again.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        private static string ComputeDependencyHash(MobileAvatarMeshRecipe recipe)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            if (prefab == null) return string.Empty;
            var values = new List<string>
            {
                recipe.CombinedQuestPrefabPath,
                MeshAnalysisUtility.ComputeAssetHash(recipe.CombinedQuestPrefabPath)
            };

            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true)
                         .OrderBy(renderer => AnimationUtility.CalculateTransformPath(renderer.transform, prefab.transform),
                             StringComparer.Ordinal)
                         .ThenBy(renderer => renderer.GetType().FullName, StringComparer.Ordinal))
            {
                values.Add(AnimationUtility.CalculateTransformPath(renderer.transform, prefab.transform));
                values.Add(renderer.GetType().AssemblyQualifiedName);
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null)
                    {
                        values.Add("<null-material>");
                        continue;
                    }
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long localId);
                    var path = AssetDatabase.GetAssetPath(material);
                    values.Add(guid + "|" + localId + "|" + path + "|" +
                               MeshAnalysisUtility.ComputeAssetHash(path));
                }
            }

            foreach (var choice in recipe.TextureChoices.OrderBy(TextureIdentity, StringComparer.Ordinal))
                values.Add(TextureIdentity(choice) + "|" + choice.CurrentSettingsId);
            return MeshAnalysisUtility.ComputeStringSignature(values);
        }

        private static string TextureIdentity(TextureConversionChoice choice) =>
            (choice?.SourceGuid ?? string.Empty) + "|" + (choice?.SourceLocalFileId ?? 0);
    }
}
