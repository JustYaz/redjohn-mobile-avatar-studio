using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal sealed class FinalAssemblyResult
    {
        public string PrefabPath { get; set; }
        public string CreatorReportPath { get; set; }
        public string MachineReportPath { get; set; }
        public bool StructuralValidationPassed { get; set; }
        public bool VisualApprovalComplete { get; set; }
        public int SelectedTriangleCount { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    internal static class FinalAssemblyPipeline
    {
        [Serializable]
        private sealed class MachineReport
        {
            public string generatedUtc;
            public string status;
            public string sourcePrefab;
            public string combinedPrefab;
            public string sourceHashBefore;
            public string sourceHashAfter;
            public int rendererCount;
            public int selectedTriangles;
            public int meshApprovals;
            public int meshChoices;
            public int materialApprovals;
            public int materialChoices;
            public int textureApprovals;
            public int textureChoices;
            public int mobileTextureOverrides;
            public bool structuralValidationPassed;
            public bool visualApprovalComplete;
            public List<string> contractDifferences = new List<string>();
            public List<string> warnings = new List<string>();
        }

        public static bool CanBuild(MobileAvatarMeshRecipe recipe, out string reason)
        {
            if (recipe == null || recipe.SourcePrefab == null)
            {
                reason = "Open a saved Mobile Avatar Studio recipe first.";
                return false;
            }
            if (string.IsNullOrEmpty(recipe.OutputRoot))
            {
                reason = "The generated workspace is missing.";
                return false;
            }
            var effectiveMeshes = UvTileSplitPipeline.GetEffectiveMeshChoices(recipe);
            if (effectiveMeshes.Count == 0 || effectiveMeshes.Any(choice =>
                    choice.SelectedCandidate == null || !choice.SelectedCandidate.CanSelect))
            {
                reason = "Every renderer must have a selectable generated mesh candidate.";
                return false;
            }
            if (recipe.MaterialChoices.Count == 0 || recipe.MaterialChoices.Any(choice =>
                    !choice.IsCurrentMappingApproved || choice.GeneratedMaterial == null))
            {
                reason = "Every material mapping must be approved and built.";
                return false;
            }
            if (!DateTime.TryParse(recipe.MaterialPassUtc, out var materialPassUtc) ||
                !DateTime.TryParse(recipe.TexturePassUtc, out var texturePassUtc) ||
                texturePassUtc < materialPassUtc)
            {
                reason = "Re-scan and apply Android/iOS texture settings after the latest material build.";
                return false;
            }
            if (recipe.TextureChoices.Count == 0 || recipe.TextureChoices.Any(choice =>
                    !choice.IsCurrentSettingsApproved || !choice.MobileOverridesApplied))
            {
                reason = "Every texture setting must be approved and applied to Android/iOS.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public static FinalAssemblyResult Build(MobileAvatarMeshRecipe recipe)
        {
            if (!CanBuild(recipe, out var reason)) throw new InvalidOperationException(reason);

            var sourcePath = AssetDatabase.GetAssetPath(recipe.SourcePrefab);
            var sourceHashBefore = MeshAnalysisUtility.ComputeAssetHash(sourcePath);
            if (!string.Equals(sourcePath, recipe.SourceAssetPath, StringComparison.Ordinal) ||
                !string.Equals(sourceHashBefore, recipe.SourceFileHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The PC source prefab changed after analysis. Resume the project and review a source-change rebase before rebuilding.");

            CaptureCurrentMaterialSlots(recipe);

            var prefabRoot = recipe.OutputRoot + "/Prefab";
            MeshAnalysisUtility.EnsureAssetFolder(prefabRoot);
            var prefabPath = PrepareCombinedOutputPath(recipe, prefabRoot);

            var instance = UnityEngine.Object.Instantiate(recipe.SourcePrefab);
            instance.name = recipe.SourcePrefab.name + "_Quest";
            try
            {
                ApplyIsolatedMeshes(instance, recipe);
                ApplyGeneratedMaterials(instance, recipe);
                RestoreCachedMaterialSlots(instance, recipe);
                MobileContentPipeline.ApplyAuthoringPayloadExclusions(instance, recipe);
                MobileComponentRepairPipeline.RefreshRestoreCacheFromBuildRoot(instance, recipe);
                MobileComponentRepairPipeline.ApplyMarkedRepairs(instance, recipe);
                MobileAvatarStudioBuildMarkerUtility.EnsureMarker(instance, recipe);

                var saved = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
                if (saved == null) throw new InvalidOperationException("Unity failed to save the combined Quest prefab.");

                var result = Validate(saved, recipe, sourceHashBefore, prefabPath);
                recipe.CombinedQuestPrefabPath = prefabPath;
                recipe.FinalAssemblyUtc = DateTime.UtcNow.ToString("O");
                recipe.BehaviorAppliedToCombined = false;
                ManualPolishPipeline.Invalidate(recipe,
                    "Combined prefab rebuilt; complete Stage 6 before manual polish");
                recipe.MobileResolvedAuditPassed = false;
                recipe.MobileValidationStatus = "Needs rerun after combined prefab assembly";
                if (!string.IsNullOrEmpty(recipe.BehaviorPassUtc))
                    recipe.BehaviorStatus = "Needs rebuild after combined prefab assembly";
                recipe.FinalAssemblyStatus = result.StructuralValidationPassed
                    ? result.VisualApprovalComplete
                        ? "Combined prefab structurally valid; later behavior and SDK validation remain"
                        : "Combined prefab structurally valid; visual mesh approval incomplete"
                    : "Combined prefab failed structural validation";
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();

                MoveLegacyCheckpoints(recipe);
                WriteReports(recipe, result, sourceHashBefore,
                    MeshAnalysisUtility.ComputeAssetHash(sourcePath));
                AssetDatabase.Refresh();
                Selection.activeObject = saved;
                return result;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static void ApplyIsolatedMeshes(GameObject instance, MobileAvatarMeshRecipe recipe)
        {
            var fallbackRoot = recipe.OutputRoot + "/Final/OriginalMeshes";
            UvTileSplitPipeline.ApplyToInstance(instance, recipe, true);
            foreach (var choice in recipe.RendererChoices)
            {
                if (choice.IsExcludedFromMobile) continue;
                if (UvTileSplitPipeline.FindEnabledSplit(recipe, choice.TransformPath) != null) continue;
                var transform = MeshAnalysisUtility.FindByPath(instance.transform, choice.TransformPath);
                if (transform == null)
                    throw new InvalidOperationException("Final assembly is missing renderer path: " + choice.TransformPath);

                var selected = choice.SelectedCandidate?.Mesh;
                if (selected == null) selected = choice.SourceMesh;
                var selectedPath = AssetDatabase.GetAssetPath(selected);
                if (!selectedPath.StartsWith(recipe.OutputRoot + "/", StringComparison.Ordinal))
                {
                    MeshAnalysisUtility.EnsureAssetFolder(fallbackRoot);
                    var stableId = Hash128.Compute(choice.TransformPath).ToString().Substring(0, 8);
                    var isolatedPath = fallbackRoot + "/" +
                                       MeshAnalysisUtility.SanitizeFileName(choice.DisplayName) + "_" + stableId + ".asset";
                    var isolated = AssetDatabase.LoadAssetAtPath<Mesh>(isolatedPath);
                    if (isolated == null)
                    {
                        isolated = UnityEngine.Object.Instantiate(selected);
                        isolated.name = selected.name + "_MobileOriginal";
                        AssetDatabase.CreateAsset(isolated, isolatedPath);
                    }
                    else
                    {
                        EditorUtility.CopySerialized(selected, isolated);
                        isolated.name = selected.name + "_MobileOriginal";
                        EditorUtility.SetDirty(isolated);
                    }
                    selected = isolated;
                }

                if (choice.Skinned)
                {
                    var renderer = transform.GetComponent<SkinnedMeshRenderer>();
                    if (renderer == null)
                        throw new InvalidOperationException("Missing SkinnedMeshRenderer at " + choice.TransformPath);
                    renderer.sharedMesh = selected;
                }
                else
                {
                    var filter = transform.GetComponent<MeshFilter>();
                    if (filter == null)
                        throw new InvalidOperationException("Missing MeshFilter at " + choice.TransformPath);
                    filter.sharedMesh = selected;
                }
            }
        }

        private static void ApplyGeneratedMaterials(GameObject instance, MobileAvatarMeshRecipe recipe)
        {
            var map = recipe.MaterialChoices.ToDictionary(choice => choice.SourceMaterial,
                choice => choice.GeneratedMaterial);
            var generated = new HashSet<Material>(map.Values.Where(material => material != null));
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                // Stage 2 exclusions deliberately do not require material mappings. Their renderer
                // payload is cleared later in this assembly pass, so attempting to translate those
                // material slots here is both unnecessary and incorrect.
                if (IsExcludedRenderer(renderer, instance, recipe)) continue;
                var materials = renderer.sharedMaterials;
                for (var index = 0; index < materials.Length; index++)
                {
                    var current = materials[index];
                    if (current == null || generated.Contains(current)) continue;
                    if (!map.TryGetValue(current, out var replacement) || replacement == null)
                        throw new InvalidOperationException(
                            $"No approved mobile material mapping exists for {current.name} on renderer {renderer.name}.");
                    materials[index] = replacement;
                }
                renderer.sharedMaterials = materials;
            }
        }

        private static bool IsExcludedRenderer(Renderer renderer, GameObject root,
            MobileAvatarMeshRecipe recipe)
        {
            if (renderer == null || root == null || recipe == null) return false;
            var rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
            return recipe.RendererChoices.Any(choice => choice.IsExcludedFromMobile &&
                (string.Equals(rendererPath, choice.TransformPath, StringComparison.Ordinal) ||
                 rendererPath.StartsWith(choice.TransformPath + "/", StringComparison.Ordinal)));
        }

        private static string PrepareCombinedOutputPath(MobileAvatarMeshRecipe recipe, string prefabRoot)
        {
            var preferredPath = prefabRoot + "/" +
                                MeshAnalysisUtility.SanitizeFileName(recipe.SourcePrefab.name + "_Quest") + ".prefab";
            var currentPath = recipe.CombinedQuestPrefabPath;
            if (string.IsNullOrEmpty(currentPath) ||
                string.Equals(currentPath, preferredPath, StringComparison.Ordinal))
                return preferredPath;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(currentPath) == null) return preferredPath;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(preferredPath) != null)
                throw new InvalidOperationException("Both the legacy and preferred combined prefabs exist. " +
                                                    "Resolve the duplicate before rebuilding: " + currentPath +
                                                    " and " + preferredPath);

            var moveError = AssetDatabase.MoveAsset(currentPath, preferredPath);
            if (!string.IsNullOrEmpty(moveError))
                throw new InvalidOperationException("Could not migrate the legacy combined prefab name: " + moveError);
            recipe.CombinedQuestPrefabPath = preferredPath;
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
            return preferredPath;
        }

        public static int CaptureCurrentMaterialSlots(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null || string.IsNullOrEmpty(recipe.CombinedQuestPrefabPath))
                return recipe?.MaterialSlotCache.Count ?? 0;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            if (prefab == null) return recipe.MaterialSlotCache.Count;

            CaptureMaterialSlots(prefab, recipe, false);
            return recipe.MaterialSlotCache.Count;
        }

        public static int CaptureAndIsolateCurrentMaterialSlots(MobileAvatarMeshRecipe recipe,
            GameObject editablePrefabRoot = null)
        {
            if (recipe == null || string.IsNullOrEmpty(recipe.CombinedQuestPrefabPath))
                throw new InvalidOperationException("Build the combined mobile prefab before saving manual material work.");
            if (editablePrefabRoot != null)
            {
                MobileAvatarStudioBuildMarkerUtility.EnsureMarker(editablePrefabRoot, recipe);
                CaptureMaterialSlots(editablePrefabRoot, recipe, true);
                if (PrefabUtility.SaveAsPrefabAsset(editablePrefabRoot, recipe.CombinedQuestPrefabPath) == null)
                    throw new InvalidOperationException("Unity could not save the isolated manual material slots.");
                AssetDatabase.SaveAssets();
                return recipe.MaterialSlotCache.Count;
            }
            var root = PrefabUtility.LoadPrefabContents(recipe.CombinedQuestPrefabPath);
            if (root == null) throw new InvalidOperationException("Could not open the combined mobile prefab.");
            try
            {
                MobileAvatarStudioBuildMarkerUtility.EnsureMarker(root, recipe);
                CaptureMaterialSlots(root, recipe, true);
                if (PrefabUtility.SaveAsPrefabAsset(root, recipe.CombinedQuestPrefabPath) == null)
                    throw new InvalidOperationException("Unity could not save the isolated manual material slots.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            AssetDatabase.SaveAssets();
            return recipe.MaterialSlotCache.Count;
        }

        private static void CaptureMaterialSlots(GameObject prefab, MobileAvatarMeshRecipe recipe,
            bool assignIsolatedMaterials)
        {
            recipe.MaterialSlotCache.Clear();
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                var entry = new RendererMaterialSlotCacheEntry
                {
                    TransformPath = AnimationUtility.CalculateTransformPath(renderer.transform, prefab.transform),
                    RendererType = renderer.GetType().AssemblyQualifiedName,
                    RendererTypeIndex = GetRendererTypeIndex(renderer)
                };
                foreach (var material in renderer.sharedMaterials)
                    entry.Materials.Add(IsolateCachedMaterial(material, recipe));
                if (assignIsolatedMaterials) renderer.sharedMaterials = entry.Materials.ToArray();
                recipe.MaterialSlotCache.Add(entry);
            }

            recipe.MaterialSlotCacheUtc = DateTime.UtcNow.ToString("O");
            recipe.MaterialSlotCacheSourcePrefabPath = recipe.CombinedQuestPrefabPath;
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
        }

        public static void ClearMaterialSlotCache(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null) return;
            recipe.MaterialSlotCache.Clear();
            recipe.MaterialSlotCacheUtc = string.Empty;
            recipe.MaterialSlotCacheSourcePrefabPath = string.Empty;
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
        }

        private static void RestoreCachedMaterialSlots(GameObject instance, MobileAvatarMeshRecipe recipe)
        {
            foreach (var entry in recipe.MaterialSlotCache)
            {
                if (recipe.RendererChoices.Any(choice => choice.IsExcludedFromMobile &&
                        (string.Equals(entry.TransformPath, choice.TransformPath, StringComparison.Ordinal) ||
                         entry.TransformPath.StartsWith(choice.TransformPath + "/", StringComparison.Ordinal))))
                    continue;
                var transform = MeshAnalysisUtility.FindByPath(instance.transform, entry.TransformPath);
                if (transform == null) continue;
                var renderer = FindRenderer(transform, entry.RendererType, entry.RendererTypeIndex);
                if (renderer == null) continue;
                renderer.sharedMaterials = entry.Materials.ToArray();
            }
        }

        private static Renderer FindRenderer(Transform transform, string rendererType, int rendererTypeIndex)
        {
            var matches = transform.GetComponents<Renderer>().Where(renderer =>
                string.Equals(renderer.GetType().AssemblyQualifiedName, rendererType, StringComparison.Ordinal)).ToArray();
            return rendererTypeIndex >= 0 && rendererTypeIndex < matches.Length ? matches[rendererTypeIndex] : null;
        }

        private static int GetRendererTypeIndex(Renderer target)
        {
            var matches = target.transform.GetComponents<Renderer>().Where(renderer => renderer.GetType() == target.GetType())
                .ToArray();
            return Array.IndexOf(matches, target);
        }

        private static Material IsolateCachedMaterial(Material source, MobileAvatarMeshRecipe recipe)
        {
            if (source == null) return null;
            var sourcePath = AssetDatabase.GetAssetPath(source).Replace('\\', '/');
            var stableMaterialsRoot = recipe.OutputRoot + "/Materials/";
            if (!string.IsNullOrEmpty(sourcePath) &&
                sourcePath.StartsWith(stableMaterialsRoot, StringComparison.Ordinal))
                return source;

            var cacheRoot = recipe.OutputRoot + "/Materials/ManualSlotCache";
            MeshAnalysisUtility.EnsureAssetFolder(cacheRoot);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId);
            var identity = string.IsNullOrEmpty(guid) ? sourcePath + "|" + source.name : guid + "|" + localId;
            var suffix = Hash128.Compute(identity).ToString().Substring(0, 10);
            var cachedPath = cacheRoot + "/" + MeshAnalysisUtility.SanitizeFileName(source.name) + "_" + suffix + ".mat";
            var cached = AssetDatabase.LoadAssetAtPath<Material>(cachedPath);
            if (cached == null)
            {
                cached = new Material(source) { name = source.name + "_MobileSlotCache" };
                AssetDatabase.CreateAsset(cached, cachedPath);
            }
            else
            {
                EditorUtility.CopySerialized(source, cached);
                cached.name = source.name + "_MobileSlotCache";
                EditorUtility.SetDirty(cached);
            }
            return cached;
        }

        private static FinalAssemblyResult Validate(GameObject saved, MobileAvatarMeshRecipe recipe,
            string sourceHashBefore, string prefabPath)
        {
            var effectiveMeshes = UvTileSplitPipeline.GetEffectiveMeshChoices(recipe);
            var result = new FinalAssemblyResult
            {
                PrefabPath = prefabPath,
                SelectedTriangleCount = effectiveMeshes.Sum(choice => choice.SelectedTriangleCount),
                VisualApprovalComplete = effectiveMeshes.All(choice => choice.IsCurrentSelectionApproved)
            };
            var errors = new List<string>();

            foreach (var choice in recipe.RendererChoices)
            {
                if (choice.IsExcludedFromMobile)
                {
                    var excludedTransform = MeshAnalysisUtility.FindByPath(saved.transform, choice.TransformPath);
                    if (excludedTransform == null)
                    {
                        errors.Add("Missing exclusion compatibility path: " + choice.TransformPath);
                        continue;
                    }
                    var payloadRemains = excludedTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                                             .Any(renderer => renderer.sharedMesh != null) ||
                                         excludedTransform.GetComponentsInChildren<MeshFilter>(true)
                                             .Any(filter => filter.sharedMesh != null);
                    if (payloadRemains) errors.Add("Excluded renderer payload remains: " + choice.TransformPath);
                    continue;
                }
                var split = UvTileSplitPipeline.FindEnabledSplit(recipe, choice.TransformPath);
                if (split != null)
                {
                    foreach (var piece in split.Pieces.Where(item => item.KeepOnMobile))
                    {
                        var childPath = UvTileSplitPipeline.BuildChildPath(split, piece);
                        var child = MeshAnalysisUtility.FindByPath(saved.transform, childPath);
                        var childMesh = split.Skinned
                            ? child?.GetComponent<SkinnedMeshRenderer>()?.sharedMesh
                            : child?.GetComponent<MeshFilter>()?.sharedMesh;
                        if (childMesh == null)
                        {
                            errors.Add("Missing UV tile piece mesh: " + childPath);
                            continue;
                        }
                        var childMeshPath = AssetDatabase.GetAssetPath(childMesh);
                        if (!childMeshPath.StartsWith(recipe.OutputRoot + "/", StringComparison.Ordinal))
                            errors.Add("UV tile piece mesh is not isolated: " + childPath + " -> " + childMeshPath);
                    }
                    continue;
                }
                var transform = MeshAnalysisUtility.FindByPath(saved.transform, choice.TransformPath);
                if (transform == null)
                {
                    errors.Add("Missing renderer path: " + choice.TransformPath);
                    continue;
                }
                var mesh = choice.Skinned
                    ? transform.GetComponent<SkinnedMeshRenderer>()?.sharedMesh
                    : transform.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null)
                {
                    errors.Add("Missing selected mesh: " + choice.TransformPath);
                    continue;
                }
                var meshPath = AssetDatabase.GetAssetPath(mesh);
                if (!meshPath.StartsWith(recipe.OutputRoot + "/", StringComparison.Ordinal))
                    errors.Add("Mesh is not isolated in the generated workspace: " + choice.TransformPath + " -> " + meshPath);
            }

            foreach (var renderer in saved.GetComponentsInChildren<Renderer>(true))
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null) continue;
                var materialPath = AssetDatabase.GetAssetPath(material);
                if (!materialPath.StartsWith(recipe.OutputRoot + "/", StringComparison.Ordinal))
                    errors.Add("Material is not isolated: " + renderer.name + " -> " + materialPath);
                if (material.shader == null ||
                    !material.shader.name.StartsWith("VRChat/Mobile/", StringComparison.Ordinal))
                    errors.Add("Non-mobile shader: " + renderer.name + " -> " +
                               (material.shader == null ? "Missing" : material.shader.name));
            }

            var descriptorCount = saved.GetComponentsInChildren<Component>(true).Count(component => component != null &&
                (component.GetType().FullName ?? component.GetType().Name)
                .IndexOf("VRCAvatarDescriptor", StringComparison.OrdinalIgnoreCase) >= 0);
            if (descriptorCount != 1) errors.Add("Expected exactly one VRC Avatar Descriptor; found " + descriptorCount + ".");

            if (!ValidateDirectExpressionParameterLayout(recipe.SourcePrefab, saved, out var parameterLayoutReason))
                errors.Add("Direct expression-parameter layout changed: " + parameterLayoutReason);

            var finalContract = AvatarBehaviorContractAnalyzer.Capture(saved);
            foreach (var sourceCategory in recipe.SourceBehaviorContract.Categories)
            {
                var finalCount = finalContract.Count(sourceCategory.Name);
                var expectedUvSplitRendererCount = recipe.SourceBehaviorContract.Count("Renderer paths") +
                    recipe.UvTileSplitRenderers.Where(split => split.Compatible && split.SplitEnabled &&
                        recipe.RendererChoices.Any(choice => !choice.IsExcludedFromMobile &&
                            string.Equals(choice.TransformPath, split.TransformPath, StringComparison.Ordinal)))
                        .Sum(split => split.Pieces.Count(piece => piece.KeepOnMobile));
                var intentionalUvSplitRenderers = string.Equals(sourceCategory.Name, "Renderer paths",
                    StringComparison.Ordinal) && finalCount == expectedUvSplitRendererCount;
                var sourceSplitBlendshapes = recipe.UvTileSplitRenderers
                    .Where(split => split.Compatible && split.SplitEnabled)
                    .Select(split => recipe.RendererChoices.FirstOrDefault(choice =>
                        string.Equals(choice.TransformPath, split.TransformPath, StringComparison.Ordinal)))
                    .Where(choice => choice != null && !choice.IsExcludedFromMobile)
                    .Sum(choice => choice.SourceBlendShapeCount);
                var pieceBlendshapes = UvTileSplitPipeline.GetRetainedPieces(recipe)
                    .Sum(piece => piece.MeshChoice.SourceBlendShapeCount);
                var expectedUvSplitBlendshapes = recipe.SourceBehaviorContract.Count("Blendshapes") -
                                                 sourceSplitBlendshapes + pieceBlendshapes;
                var intentionalUvSplitBlendshapes = string.Equals(sourceCategory.Name, "Blendshapes",
                    StringComparison.Ordinal) && finalCount == expectedUvSplitBlendshapes;
                var intentionalExcludedBlendshapes =
                    string.Equals(sourceCategory.Name, "Blendshapes", StringComparison.Ordinal) &&
                    recipe.RendererChoices.Any(choice => choice.IsExcludedFromMobile) &&
                    finalCount <= sourceCategory.EntryCount;
                var deferredBuildSystemParameters =
                    string.Equals(sourceCategory.Name, "Parameters", StringComparison.Ordinal) &&
                    recipe.SourceBehaviorContract.DetectedBuildSystems.Count > 0;
                if (finalCount != sourceCategory.EntryCount && deferredBuildSystemParameters)
                    result.Warnings.Add($"Build-system parameter dependency totals are deferred to resolved validation: " +
                                        $"{sourceCategory.EntryCount} -> {finalCount}. " +
                                        "The descriptor's ordered parameter layout was preserved.");
                else if (finalCount != sourceCategory.EntryCount && !intentionalExcludedBlendshapes &&
                         !intentionalUvSplitRenderers && !intentionalUvSplitBlendshapes)
                    errors.Add($"Behavior contract count changed: {sourceCategory.Name} " +
                               $"{sourceCategory.EntryCount} -> {finalCount}.");
                else if (intentionalExcludedBlendshapes && finalCount != sourceCategory.EntryCount)
                    result.Warnings.Add($"Intentional mobile exclusions reduced blendshapes: " +
                                        $"{sourceCategory.EntryCount} -> {finalCount}.");
            }

            if (!result.VisualApprovalComplete)
                result.Warnings.Add($"Mesh visual approvals are incomplete: " +
                                    $"{effectiveMeshes.Count(choice => choice.IsCurrentSelectionApproved)}/" +
                                    $"{effectiveMeshes.Count} retained meshes approved.");
            if (recipe.SourceBehaviorContract.DetectedBuildSystems.Count > 0)
                result.Warnings.Add("Build-time avatar systems remain unresolved: " +
                                    string.Join(", ", recipe.SourceBehaviorContract.DetectedBuildSystems) + ".");
            if (recipe.SourceBehaviorContract.Count("Material properties") > 0)
                result.Warnings.Add("Animated material-property curves have not yet been remapped to mobile shader properties.");
            result.Warnings.Add("A real VRChat SDK Android build, bundle-size measurement, and resolved behavior simulation have not run.");

            var sourceHashAfter = MeshAnalysisUtility.ComputeAssetHash(recipe.SourceAssetPath);
            if (!string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.OrdinalIgnoreCase))
                errors.Add("The PC source prefab changed during final assembly.");

            result.StructuralValidationPassed = errors.Count == 0;
            foreach (var error in errors) result.Warnings.Insert(0, "ERROR: " + error);
            return result;
        }

        private static bool ValidateDirectExpressionParameterLayout(GameObject source, GameObject generated,
            out string reason)
        {
            var sourceParameters = FindDescriptorObjectReference(source, "expressionParameters");
            var generatedParameters = FindDescriptorObjectReference(generated, "expressionParameters");
            if (sourceParameters == null && generatedParameters == null)
            {
                reason = string.Empty;
                return true;
            }
            if (sourceParameters == null || generatedParameters == null)
            {
                reason = "the descriptor parameter asset reference was added or removed";
                return false;
            }

            var sourceSignature = BuildParameterLayoutSignature(sourceParameters, out var sourceCount);
            var generatedSignature = BuildParameterLayoutSignature(generatedParameters, out var generatedCount);
            if (string.Equals(sourceSignature, generatedSignature, StringComparison.Ordinal))
            {
                reason = string.Empty;
                return true;
            }
            reason = $"ordered definitions differ ({sourceCount} source entries, {generatedCount} generated entries)";
            return false;
        }

        private static UnityEngine.Object FindDescriptorObjectReference(GameObject root, string propertyName)
        {
            var descriptor = root.GetComponentsInChildren<Component>(true).FirstOrDefault(component => component != null &&
                (component.GetType().FullName ?? component.GetType().Name)
                .IndexOf("VRCAvatarDescriptor", StringComparison.OrdinalIgnoreCase) >= 0);
            if (descriptor == null) return null;
            try
            {
                return new SerializedObject(descriptor).FindProperty(propertyName)?.objectReferenceValue;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildParameterLayoutSignature(UnityEngine.Object parameterAsset, out int count)
        {
            var serialized = new SerializedObject(parameterAsset);
            var parameters = serialized.FindProperty("parameters");
            if (parameters == null || !parameters.isArray)
            {
                count = -1;
                return "<missing-parameters-array>";
            }
            count = parameters.arraySize;
            var text = new StringBuilder();
            for (var index = 0; index < parameters.arraySize; index++)
            {
                var parameter = parameters.GetArrayElementAtIndex(index);
                AppendSerializedValue(text, parameter.FindPropertyRelative("name"));
                AppendSerializedValue(text, parameter.FindPropertyRelative("valueType"));
                AppendSerializedValue(text, parameter.FindPropertyRelative("defaultValue"));
                AppendSerializedValue(text, parameter.FindPropertyRelative("saved"));
                AppendSerializedValue(text, parameter.FindPropertyRelative("networkSynced"));
                text.AppendLine();
            }
            return text.ToString();
        }

        private static void AppendSerializedValue(StringBuilder text, SerializedProperty property)
        {
            if (property == null)
            {
                text.Append("<missing>|");
                return;
            }
            switch (property.propertyType)
            {
                case SerializedPropertyType.String:
                    text.Append(property.stringValue);
                    break;
                case SerializedPropertyType.Boolean:
                    text.Append(property.boolValue ? '1' : '0');
                    break;
                case SerializedPropertyType.Float:
                    text.Append(property.floatValue.ToString("R"));
                    break;
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.Integer:
                    text.Append(property.intValue);
                    break;
                default:
                    text.Append(property.propertyType).Append(':').Append(property.propertyPath);
                    break;
            }
            text.Append('|');
        }

        private static void MoveLegacyCheckpoints(MobileAvatarMeshRecipe recipe)
        {
            var prefabRoot = recipe.OutputRoot + "/Prefab";
            var sourceName = MeshAnalysisUtility.SanitizeFileName(recipe.SourcePrefab.name);
            MoveLegacyPrefab(prefabRoot + "/" + sourceName + "_MobileMesh_DRAFT.prefab",
                recipe.OutputRoot + "/Checkpoints/Mesh");
            var movedMaterialPath = MoveLegacyPrefab(prefabRoot + "/" + sourceName + "_QuestMaterial_DRAFT.prefab",
                recipe.OutputRoot + "/Checkpoints/Materials");
            if (!string.IsNullOrEmpty(movedMaterialPath)) recipe.GeneratedMaterialPrefabPath = movedMaterialPath;
        }

        private static string MoveLegacyPrefab(string sourcePath, string destinationFolder)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath) == null) return string.Empty;
            MeshAnalysisUtility.EnsureAssetFolder(destinationFolder);
            var destinationPath = destinationFolder + "/" + Path.GetFileName(sourcePath);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(destinationPath) != null)
                AssetDatabase.DeleteAsset(destinationPath);
            var error = AssetDatabase.MoveAsset(sourcePath, destinationPath);
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
            return destinationPath;
        }

        private static void WriteReports(MobileAvatarMeshRecipe recipe, FinalAssemblyResult result,
            string sourceHashBefore, string sourceHashAfter)
        {
            var reportRoot = recipe.OutputRoot + "/Reports";
            MeshAnalysisUtility.EnsureAssetFolder(reportRoot);
            result.CreatorReportPath = reportRoot + "/FinalAssemblyReport.txt";
            result.MachineReportPath = reportRoot + "/FinalAssemblyReport.json";

            var text = new StringBuilder();
            text.AppendLine("MOBILE AVATAR STUDIO - COMBINED QUEST PREFAB");
            text.AppendLine("Generated: " + DateTime.UtcNow.ToString("O"));
            text.AppendLine("Status: " + recipe.FinalAssemblyStatus);
            text.AppendLine("Source: " + recipe.SourceAssetPath);
            text.AppendLine("Combined prefab: " + result.PrefabPath);
            text.AppendLine("Source unchanged: " +
                            string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.OrdinalIgnoreCase));
            var effectiveMeshes = UvTileSplitPipeline.GetEffectiveMeshChoices(recipe);
            text.AppendLine($"Meshes: {effectiveMeshes.Count}; selected triangles: {result.SelectedTriangleCount:N0}; " +
                            $"visual approvals: {effectiveMeshes.Count(choice => choice.IsCurrentSelectionApproved)}/" +
                            effectiveMeshes.Count);
            text.AppendLine($"Mobile content exclusions: {recipe.RendererChoices.Count(choice => choice.IsExcludedFromMobile)}; " +
                            $"fallback redirects: {recipe.RendererChoices.Count(choice => choice.RedirectsToFallback)}");
            text.AppendLine($"Materials: {recipe.MaterialChoices.Count}; mobile shader: {MaterialConversionPipeline.TargetShaderName}");
            text.AppendLine($"Textures: {recipe.TextureChoices.Count}; Android/iOS overrides applied: " +
                            recipe.TextureChoices.Count(choice => choice.MobileOverridesApplied));
            text.AppendLine("Structural validation: " + (result.StructuralValidationPassed ? "PASS" : "FAIL"));
            text.AppendLine();
            text.AppendLine("WARNINGS / UNRESOLVED RELEASE CHECKS");
            foreach (var warning in result.Warnings) text.AppendLine("- " + warning);
            File.WriteAllText(result.CreatorReportPath, text.ToString(), Encoding.UTF8);

            var machine = new MachineReport
            {
                generatedUtc = DateTime.UtcNow.ToString("O"),
                status = recipe.FinalAssemblyStatus,
                sourcePrefab = recipe.SourceAssetPath,
                combinedPrefab = result.PrefabPath,
                sourceHashBefore = sourceHashBefore,
                sourceHashAfter = sourceHashAfter,
                rendererCount = effectiveMeshes.Count,
                selectedTriangles = result.SelectedTriangleCount,
                meshApprovals = effectiveMeshes.Count(choice => choice.IsCurrentSelectionApproved),
                meshChoices = effectiveMeshes.Count,
                materialApprovals = recipe.MaterialChoices.Count(choice => choice.IsCurrentMappingApproved),
                materialChoices = recipe.MaterialChoices.Count,
                textureApprovals = recipe.TextureChoices.Count(choice => choice.IsCurrentSettingsApproved),
                textureChoices = recipe.TextureChoices.Count,
                mobileTextureOverrides = recipe.TextureChoices.Count(choice => choice.MobileOverridesApplied),
                structuralValidationPassed = result.StructuralValidationPassed,
                visualApprovalComplete = result.VisualApprovalComplete,
                warnings = new List<string>(result.Warnings)
            };
            File.WriteAllText(result.MachineReportPath, JsonUtility.ToJson(machine, true), Encoding.UTF8);
            AssetDatabase.ImportAsset(result.CreatorReportPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(result.MachineReportPath, ImportAssetOptions.ForceUpdate);
        }
    }
}
