using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal static class MeshCandidatePipeline
    {
        internal readonly struct CandidateLevel
        {
            public CandidateLevel(string id, string label, float ratio)
            {
                Id = id;
                Label = label;
                Ratio = ratio;
            }

            public string Id { get; }
            public string Label { get; }
            public float Ratio { get; }
        }

        public static MobileAvatarMeshRecipe Analyze(GameObject sourcePrefab, bool replaceExisting = false)
        {
            if (sourcePrefab == null) throw new ArgumentNullException(nameof(sourcePrefab));
            var sourcePath = AssetDatabase.GetAssetPath(sourcePrefab);
            if (string.IsNullOrEmpty(sourcePath) || PrefabUtility.GetPrefabAssetType(sourcePrefab) == PrefabAssetType.NotAPrefab)
                throw new InvalidOperationException("Select a prefab asset from the Project window, not a scene instance.");

            if (!replaceExisting)
            {
                var existing = FindExistingRecipe(sourcePrefab);
                if (existing != null) return existing;
            }

            var recipe = ScriptableObject.CreateInstance<MobileAvatarMeshRecipe>();
            recipe.name = sourcePrefab.name + " Mesh Recipe";
            recipe.SourcePrefab = sourcePrefab;
            recipe.SourceAssetPath = sourcePath;
            recipe.SourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            recipe.SourceFileHash = MeshAnalysisUtility.ComputeAssetHash(sourcePath);
            recipe.GeneratedUtc = DateTime.UtcNow.ToString("O");
            recipe.UnityVersion = Application.unityVersion;

            var capturedContract = AvatarBehaviorContractAnalyzer.Capture(sourcePrefab);
            CopyContract(capturedContract, recipe.SourceBehaviorContract);

            foreach (var renderer in sourcePrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null) continue;
                recipe.RendererChoices.Add(CreateChoice(sourcePrefab.transform, renderer, renderer.sharedMesh, true));
            }

            foreach (var filter in sourcePrefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || filter.GetComponent<MeshRenderer>() == null) continue;
                recipe.RendererChoices.Add(CreateChoice(sourcePrefab.transform, filter.GetComponent<MeshRenderer>(), filter.sharedMesh, false));
            }

            recipe.RendererChoices.Sort((left, right) =>
                string.Compare(left.TransformPath, right.TransformPath, StringComparison.OrdinalIgnoreCase));

            if (recipe.RendererChoices.Count == 0)
                throw new InvalidOperationException("The selected prefab contains no supported meshes.");
            MobileContentPipeline.Analyze(recipe);
            MaterialConversionPipeline.Analyze(recipe);
            EnsureWorkspace(recipe, replaceExisting);
            return recipe;
        }

        public static MobileAvatarMeshRecipe FindExistingRecipe(GameObject sourcePrefab)
        {
            if (sourcePrefab == null) return null;
            var sourcePath = AssetDatabase.GetAssetPath(sourcePrefab);
            if (string.IsNullOrEmpty(sourcePath)) return null;
            var sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            var avatarRoot = BuildAvatarRoot(sourcePrefab, sourceGuid);
            if (!AssetDatabase.IsValidFolder(avatarRoot)) return null;

            var expectedPath = avatarRoot + "/Current/" +
                               MeshAnalysisUtility.SanitizeFileName(sourcePrefab.name) + "_MeshRecipe.asset";
            var expected = AssetDatabase.LoadAssetAtPath<MobileAvatarMeshRecipe>(expectedPath);
            if (expected != null && expected.SourcePrefab == sourcePrefab) return expected;

            foreach (var guid in AssetDatabase.FindAssets("t:MobileAvatarMeshRecipe", new[] { avatarRoot }))
            {
                var candidate = AssetDatabase.LoadAssetAtPath<MobileAvatarMeshRecipe>(AssetDatabase.GUIDToAssetPath(guid));
                if (candidate != null && candidate.SourcePrefab == sourcePrefab) return candidate;
            }
            return null;
        }

        public static string EnsureWorkspace(MobileAvatarMeshRecipe recipe, bool replaceExisting = false)
        {
            if (recipe == null || recipe.SourcePrefab == null) throw new ArgumentNullException(nameof(recipe));
            if (string.IsNullOrEmpty(recipe.OutputRoot)) recipe.OutputRoot = CreateJobRoot(recipe, replaceExisting);
            if (!AssetDatabase.Contains(recipe))
            {
                var recipePath = recipe.OutputRoot + "/" +
                                 MeshAnalysisUtility.SanitizeFileName(recipe.SourcePrefab.name) + "_MeshRecipe.asset";
                AssetDatabase.CreateAsset(recipe, recipePath);
            }
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
            return recipe.OutputRoot;
        }

        public static bool GenerateCandidates(MobileAvatarMeshRecipe recipe,
            IReadOnlyList<CandidateLevel> levels, IMeshReductionBackend backend, bool adaptive,
            bool forceRegenerate = false)
        {
            if (recipe == null || recipe.SourcePrefab == null) throw new ArgumentNullException(nameof(recipe));
            if (backend == null || !backend.IsAvailable)
                throw new InvalidOperationException(backend?.AvailabilityMessage ?? "No mesh-reduction backend is available.");
            if (levels == null || levels.Count == 0) throw new ArgumentException("At least one reduction level is required.", nameof(levels));

            var jobRoot = EnsureWorkspace(recipe);
            var meshesRoot = jobRoot + "/Candidates";
            MeshAnalysisUtility.EnsureAssetFolder(meshesRoot);
            recipe.OutputRoot = jobRoot;
            recipe.BackendName = backend.Name;
            recipe.GeneratedUtc = DateTime.UtcNow.ToString("O");

            var selectedChoices = UvTileSplitPipeline.GetEffectiveMeshChoices(recipe)
                .Where(choice => choice.GenerateCandidates).ToArray();
            var pending = new List<GenerationWork>();
            foreach (var choice in selectedChoices)
            {
                var choiceLevels = adaptive ? CreateAdaptiveLevels(choice) : levels;
                var signature = BuildGenerationSignature(choice, choiceLevels, backend, adaptive);
                var allowLegacyAdoption = string.IsNullOrEmpty(choice.CandidateGenerationSignature) &&
                                          string.Equals(recipe.BackendName, backend.Name, StringComparison.Ordinal);
                if (!forceRegenerate && IsCompleteCandidateSet(choice, choiceLevels, signature, allowLegacyAdoption))
                    continue;
                pending.Add(new GenerationWork(choice, choiceLevels, signature));
            }

            var totalOperations = Math.Max(1, pending.Sum(item => item.Levels.Count + 1));
            var operation = 0;
            var completed = selectedChoices.Length - pending.Count;

            try
            {
                recipe.MeshGenerationState = pending.Count == 0
                    ? $"Complete ({selectedChoices.Length}/{selectedChoices.Length} checked renderers)"
                    : $"Running ({completed}/{selectedChoices.Length} checked renderers)";
                SaveGenerationCheckpoint(recipe);

                foreach (var work in pending)
                {
                    var choice = work.Choice;
                    if (DisplayProgress(choice, "Original", operation++, totalOperations))
                    {
                        recipe.MeshGenerationState = $"Paused ({completed}/{selectedChoices.Length} checked renderers)";
                        SaveGenerationCheckpoint(recipe);
                        WriteGenerationReports(recipe);
                        return false;
                    }

                    choice.Candidates.Clear();
                    choice.CandidateGenerationSignature = string.Empty;
                    choice.SelectedCandidateIndex = 0;
                    choice.RevokeApproval();

                    var rendererFolder = meshesRoot + "/" + BuildRendererFolderName(choice);
                    if (AssetDatabase.IsValidFolder(rendererFolder) && !AssetDatabase.DeleteAsset(rendererFolder))
                        throw new InvalidOperationException("Could not replace generated candidate folder: " + rendererFolder);
                    MeshAnalysisUtility.EnsureAssetFolder(rendererFolder);

                    var original = UnityEngine.Object.Instantiate(choice.SourceMesh);
                    original.name = choice.SourceMesh.name + "_Original";
                    var originalPath = rendererFolder + "/Original.asset";
                    AssetDatabase.CreateAsset(original, originalPath);
                    var originalCandidate = new MeshCandidate
                    {
                        Id = "original",
                        Label = "Original",
                        RequestedRatio = 1f,
                        Mesh = original,
                        TriangleCount = choice.SourceTriangleCount,
                        VertexCount = original.vertexCount,
                        ConnectedComponents = choice.SourceConnectedComponents,
                        Status = MeshCandidateStatus.Original,
                        ValidationMessage = "Exact isolated copy of the source mesh."
                    };
                    originalCandidate.Quality.StructuralIntegrity = 100;
                    originalCandidate.Quality.SilhouettePreservation = 100;
                    originalCandidate.Quality.DeformationQuality = 100;
                    originalCandidate.Quality.BlendShapeFidelity = 100;
                    originalCandidate.Quality.NormalQuality = 100;
                    originalCandidate.Quality.UvStability = 100;
                    originalCandidate.Quality.BoneWeightIntegrity = 100;
                    originalCandidate.Quality.VisualEfficiency = 0;
                    originalCandidate.Quality.MeasurementNotes = "Exact isolated copy of the source mesh.";
                    choice.Candidates.Add(originalCandidate);

                    foreach (var level in work.Levels)
                    {
                        if (DisplayProgress(choice, level.Label, operation++, totalOperations))
                        {
                            recipe.MeshGenerationState = $"Paused ({completed}/{selectedChoices.Length} checked renderers)";
                            SaveGenerationCheckpoint(recipe);
                            WriteGenerationReports(recipe);
                            return false;
                        }
                        var ratio = Mathf.Clamp(level.Ratio, 0.05f, 0.99f);
                        var target = Mathf.Clamp(Mathf.RoundToInt(choice.SourceTriangleCount * ratio), 4,
                            Math.Max(4, choice.SourceTriangleCount - 1));

                        Mesh reduced = null;
                        try
                        {
                            reduced = backend.Reduce(choice.SourceMesh, target, choice.SourceBlendShapeCount > 0);
                            reduced.name = choice.SourceMesh.name + "_" + level.Id;
                            var validation = MeshAnalysisUtility.ValidateCandidate(
                                choice.SourceMesh,
                                reduced,
                                choice.Skinned,
                                choice.SourceConnectedComponents);

                            if (MeshAnalysisUtility.TriangleCount(reduced) >= choice.SourceTriangleCount)
                            {
                                UnityEngine.Object.DestroyImmediate(reduced);
                                reduced = null;
                                choice.Candidates.Add(new MeshCandidate
                                {
                                    Id = level.Id,
                                    Label = level.Label,
                                    RequestedRatio = ratio,
                                    Mesh = null,
                                    TriangleCount = choice.SourceTriangleCount,
                                    VertexCount = choice.SourceVertexCount,
                                    ConnectedComponents = choice.SourceConnectedComponents,
                                    Status = MeshCandidateStatus.Unavailable,
                                    ValidationMessage = "The backend could not reduce this mesh at the requested level, so no duplicate candidate was saved."
                                });
                                continue;
                            }

                            var reducedPath = rendererFolder + "/" + level.Id + ".asset";
                            AssetDatabase.CreateAsset(reduced, reducedPath);
                            var candidate = new MeshCandidate
                            {
                                Id = level.Id,
                                Label = level.Label,
                                RequestedRatio = ratio,
                                Mesh = reduced,
                                TriangleCount = MeshAnalysisUtility.TriangleCount(reduced),
                                VertexCount = reduced.vertexCount,
                                ConnectedComponents = validation.ConnectedComponents,
                                Status = validation.Status,
                                ValidationMessage = validation.Message
                            };
                            PopulateQuality(candidate, choice, validation, ratio);
                            choice.Candidates.Add(candidate);
                            reduced = null;
                        }
                        catch (Exception exception)
                        {
                            if (reduced != null) UnityEngine.Object.DestroyImmediate(reduced);
                            choice.Candidates.Add(new MeshCandidate
                            {
                                Id = level.Id,
                                Label = level.Label,
                                RequestedRatio = ratio,
                                Mesh = null,
                                TriangleCount = 0,
                                VertexCount = 0,
                                ConnectedComponents = 0,
                                Status = MeshCandidateStatus.Rejected,
                                ValidationMessage = exception.Message
                            });
                        }
                    }

                    choice.CandidateGenerationSignature = work.Signature;
                    completed++;
                    recipe.MeshGenerationState = $"Running ({completed}/{selectedChoices.Length} checked renderers)";
                    SaveGenerationCheckpoint(recipe);
                }

                recipe.MeshGenerationState = $"Complete ({completed}/{selectedChoices.Length} checked renderers)";
                SaveGenerationCheckpoint(recipe);
                WriteGenerationReports(recipe);
                return true;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private readonly struct GenerationWork
        {
            public GenerationWork(RendererMeshChoice choice, IReadOnlyList<CandidateLevel> levels, string signature)
            {
                Choice = choice;
                Levels = levels;
                Signature = signature;
            }

            public RendererMeshChoice Choice { get; }
            public IReadOnlyList<CandidateLevel> Levels { get; }
            public string Signature { get; }
        }

        private static string BuildGenerationSignature(RendererMeshChoice choice,
            IReadOnlyList<CandidateLevel> levels, IMeshReductionBackend backend, bool adaptive)
        {
            var settings = backend.Name + "|" + adaptive + "|" + choice.Identity.MeshSignature + "|" +
                           string.Join("|", levels.Select(level =>
                               level.Id + ":" + Mathf.Clamp(level.Ratio, 0.05f, 0.99f).ToString("R", CultureInfo.InvariantCulture)));
            return Hash128.Compute(settings).ToString();
        }

        private static bool IsCompleteCandidateSet(RendererMeshChoice choice,
            IReadOnlyList<CandidateLevel> levels, string signature, bool allowLegacyAdoption)
        {
            if (!string.Equals(choice.CandidateGenerationSignature, signature, StringComparison.Ordinal) &&
                !allowLegacyAdoption) return false;
            if (choice.Candidates.Count != levels.Count + 1) return false;
            if (!string.Equals(choice.Candidates[0].Id, "original", StringComparison.Ordinal) ||
                choice.Candidates[0].Mesh == null) return false;

            for (var index = 0; index < levels.Count; index++)
            {
                var candidate = choice.Candidates[index + 1];
                var level = levels[index];
                if (!string.Equals(candidate.Id, level.Id, StringComparison.Ordinal) ||
                    Mathf.Abs(candidate.RequestedRatio - Mathf.Clamp(level.Ratio, 0.05f, 0.99f)) > 0.0001f)
                    return false;
                if (candidate.Status != MeshCandidateStatus.Unavailable &&
                    candidate.Status != MeshCandidateStatus.Rejected && candidate.Mesh == null)
                    return false;
            }

            if (allowLegacyAdoption) choice.CandidateGenerationSignature = signature;
            return true;
        }

        private static void SaveGenerationCheckpoint(MobileAvatarMeshRecipe recipe)
        {
            recipe.MeshGenerationCheckpointUtc = DateTime.UtcNow.ToString("O");
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
        }

        private static void WriteGenerationReports(MobileAvatarMeshRecipe recipe)
        {
            MeshCandidateReportWriter.Write(recipe);
            AssetDatabase.Refresh();
            Selection.activeObject = recipe;
        }

        public static string BuildSelectedPrefab(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null || recipe.SourcePrefab == null) throw new ArgumentNullException(nameof(recipe));
            if (string.IsNullOrEmpty(recipe.OutputRoot))
                throw new InvalidOperationException("Generate mesh candidates before building a prefab.");

            var currentPath = AssetDatabase.GetAssetPath(recipe.SourcePrefab);
            var currentHash = MeshAnalysisUtility.ComputeAssetHash(currentPath);
            if (!string.Equals(recipe.SourceAssetPath, currentPath, StringComparison.Ordinal) ||
                !string.Equals(recipe.SourceFileHash, currentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The source prefab changed after analysis. Re-analyze it before building.");

            foreach (var choice in UvTileSplitPipeline.GetEffectiveMeshChoices(recipe))
            {
                if (choice.SelectedCandidate != null && !choice.SelectedCandidate.CanSelect)
                    throw new InvalidOperationException($"{choice.TransformPath} has a rejected candidate selected.");
            }

            var instance = UnityEngine.Object.Instantiate(recipe.SourcePrefab);
            instance.name = recipe.SourcePrefab.name + "_MobileMesh_DRAFT";
            try
            {
                ValidateCurrentSourceMeshes(recipe);
                ApplySelectionsForBuild(instance, recipe);
                MobileContentPipeline.ApplyAuthoringPayloadExclusions(instance, recipe);
                var prefabRoot = recipe.OutputRoot + "/Checkpoints/Mesh";
                MeshAnalysisUtility.EnsureAssetFolder(prefabRoot);
                var prefabPath = prefabRoot + "/" + MeshAnalysisUtility.SanitizeFileName(instance.name) + ".prefab";
                var saved = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
                if (saved == null) throw new InvalidOperationException("Unity failed to save the selected-mesh prefab.");
                AssetDatabase.SaveAssets();
                MeshCandidateReportWriter.Write(recipe, prefabPath);
                AssetDatabase.Refresh();
                Selection.activeObject = saved;
                return prefabPath;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        public static void ApplySelections(GameObject instance, MobileAvatarMeshRecipe recipe)
        {
            UvTileSplitPipeline.ApplyToInstance(instance, recipe, true);
            foreach (var choice in recipe.RendererChoices)
            {
                if (choice.IsExcludedFromMobile) continue;
                if (UvTileSplitPipeline.FindEnabledSplit(recipe, choice.TransformPath) != null) continue;
                var transform = MeshAnalysisUtility.FindByPath(instance.transform, choice.TransformPath);
                if (transform == null)
                    throw new InvalidOperationException($"Missing renderer path in prefab: {choice.TransformPath}");

                if (choice.Skinned)
                {
                    var renderer = transform.GetComponent<SkinnedMeshRenderer>();
                    if (renderer == null) throw new InvalidOperationException($"Missing SkinnedMeshRenderer at {choice.TransformPath}");
                    renderer.sharedMesh = choice.SelectedMesh;
                }
                else
                {
                    var filter = transform.GetComponent<MeshFilter>();
                    if (filter == null) throw new InvalidOperationException($"Missing MeshFilter at {choice.TransformPath}");
                    filter.sharedMesh = choice.SelectedMesh;
                }
            }
        }

        private static void ApplySelectionsForBuild(GameObject instance, MobileAvatarMeshRecipe recipe)
        {
            var isolatedRoot = recipe.OutputRoot + "/SelectedOriginalMeshes";
            MeshAnalysisUtility.EnsureAssetFolder(isolatedRoot);
            UvTileSplitPipeline.ApplyToInstance(instance, recipe, true);
            foreach (var choice in recipe.RendererChoices)
            {
                if (choice.IsExcludedFromMobile) continue;
                if (UvTileSplitPipeline.FindEnabledSplit(recipe, choice.TransformPath) != null) continue;
                var selectedMesh = choice.SelectedCandidate?.Mesh;
                if (selectedMesh == null)
                {
                    var clone = UnityEngine.Object.Instantiate(choice.SourceMesh);
                    clone.name = choice.SourceMesh.name + "_ApprovedOriginal";
                    var stem = MeshAnalysisUtility.SanitizeFileName(
                        (string.IsNullOrEmpty(choice.TransformPath) ? "Root" : choice.TransformPath.Replace('/', '_')) + "_Original.asset");
                    var path = isolatedRoot + "/" + stem;
                    var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                    if (existing == null)
                    {
                        AssetDatabase.CreateAsset(clone, path);
                        selectedMesh = clone;
                    }
                    else
                    {
                        EditorUtility.CopySerialized(clone, existing);
                        existing.name = clone.name;
                        EditorUtility.SetDirty(existing);
                        UnityEngine.Object.DestroyImmediate(clone);
                        selectedMesh = existing;
                    }
                }

                AssignMesh(instance, choice, selectedMesh);
            }
        }

        private static void ValidateCurrentSourceMeshes(MobileAvatarMeshRecipe recipe)
        {
            foreach (var choice in recipe.RendererChoices)
            {
                var transform = MeshAnalysisUtility.FindByPath(recipe.SourcePrefab.transform, choice.TransformPath);
                if (transform == null)
                    throw new InvalidOperationException($"Source renderer moved or was removed after analysis: {choice.TransformPath}. Rebase the recipe before building.");
                var current = choice.Skinned
                    ? transform.GetComponent<SkinnedMeshRenderer>()?.sharedMesh
                    : transform.GetComponent<MeshFilter>()?.sharedMesh;
                if (current == null)
                    throw new InvalidOperationException($"Source mesh is missing after analysis: {choice.TransformPath}.");
                var signature = MeshAnalysisUtility.ComputeMeshSignature(current);
                if (!string.Equals(signature, choice.Identity.MeshSignature, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Source mesh changed after analysis: {choice.TransformPath}. Re-analyze or rebase the recipe before building.");
            }
        }

        private static void AssignMesh(GameObject instance, RendererMeshChoice choice, Mesh mesh)
        {
            var transform = MeshAnalysisUtility.FindByPath(instance.transform, choice.TransformPath);
            if (transform == null)
                throw new InvalidOperationException($"Missing renderer path in prefab: {choice.TransformPath}");
            if (choice.Skinned)
            {
                var renderer = transform.GetComponent<SkinnedMeshRenderer>();
                if (renderer == null) throw new InvalidOperationException($"Missing SkinnedMeshRenderer at {choice.TransformPath}");
                renderer.sharedMesh = mesh;
            }
            else
            {
                var filter = transform.GetComponent<MeshFilter>();
                if (filter == null) throw new InvalidOperationException($"Missing MeshFilter at {choice.TransformPath}");
                filter.sharedMesh = mesh;
            }
        }

        public static IReadOnlyList<CandidateLevel> CreateAdaptiveLevels(RendererMeshChoice choice)
        {
            if (choice.ReductionRisk >= 75)
            {
                return new[]
                {
                    new CandidateLevel("VeryLight", "Very Light", 0.97f),
                    new CandidateLevel("Light", "Light", 0.92f),
                    new CandidateLevel("Balanced", "Balanced", 0.86f)
                };
            }

            if (choice.ReductionRisk >= 50)
            {
                return new[]
                {
                    new CandidateLevel("VeryLight", "Very Light", 0.95f),
                    new CandidateLevel("Light", "Light", 0.87f),
                    new CandidateLevel("Balanced", "Balanced", 0.76f),
                    new CandidateLevel("Aggressive", "Aggressive", 0.65f)
                };
            }

            if (choice.ReductionRisk >= 25)
            {
                return new[]
                {
                    new CandidateLevel("VeryLight", "Very Light", 0.90f),
                    new CandidateLevel("Light", "Light", 0.76f),
                    new CandidateLevel("Balanced", "Balanced", 0.60f),
                    new CandidateLevel("Aggressive", "Aggressive", 0.45f)
                };
            }

            return new[]
            {
                new CandidateLevel("VeryLight", "Very Light", 0.80f),
                new CandidateLevel("Light", "Light", 0.60f),
                new CandidateLevel("Balanced", "Balanced", 0.40f),
                new CandidateLevel("Aggressive", "Aggressive", 0.22f)
            };
        }

        private static RendererMeshChoice CreateChoice(Transform root, Renderer renderer, Mesh source, bool skinned)
        {
            var path = MeshAnalysisUtility.CalculateTransformPath(renderer.transform, root);
            var sourceComponents = MeshAnalysisUtility.ConnectedTriangleComponents(source);
            var skinnedRenderer = renderer as SkinnedMeshRenderer;
            var boneCount = skinnedRenderer?.bones?.Length ?? 0;
            var riskReasons = new List<string>();
            var risk = CalculateReductionRisk(source, renderer, sourceComponents, boneCount, riskReasons);
            var choice = new RendererMeshChoice
            {
                TransformPath = path,
                DisplayName = string.IsNullOrEmpty(path) ? renderer.name : path,
                Skinned = skinned,
                SourceMesh = source,
                SourceTriangleCount = MeshAnalysisUtility.TriangleCount(source),
                SourceVertexCount = source.vertexCount,
                SourceBlendShapeCount = source.blendShapeCount,
                SourceConnectedComponents = sourceComponents,
                SourceBoneCount = boneCount,
                SourceReadable = source.isReadable,
                ReductionRisk = risk,
                ReductionRiskReason = riskReasons.Count == 0 ? "No elevated structural risk indicators were detected." : string.Join(" ", riskReasons),
                GenerateCandidates = true,
                SelectedCandidateIndex = 0
            };

            string meshGuid;
            long localFileId;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out meshGuid, out localFileId);
            choice.Identity.MeshAssetGuid = meshGuid;
            choice.Identity.MeshLocalFileId = localFileId;
            choice.Identity.HierarchyPath = path;
            choice.Identity.RendererName = renderer.name;
            choice.Identity.MeshSignature = MeshAnalysisUtility.ComputeMeshSignature(source);
            choice.Identity.BlendShapeSignature = MeshAnalysisUtility.ComputeStringSignature(
                Enumerable.Range(0, source.blendShapeCount).Select(index =>
                    source.GetBlendShapeName(index) + ":" + source.GetBlendShapeFrameCount(index)));
            choice.Identity.MaterialSignature = MeshAnalysisUtility.ComputeStringSignature(renderer.sharedMaterials.Select(material =>
                material == null ? "<null>" : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(material))));
            choice.Identity.BoneSignature = MeshAnalysisUtility.ComputeStringSignature(skinnedRenderer == null
                ? Enumerable.Empty<string>()
                : skinnedRenderer.bones.Select(bone => SafeTransformIdentity(bone, root)));
            return choice;
        }

        private static int CalculateReductionRisk(Mesh source, Renderer renderer, int components, int boneCount,
            ICollection<string> reasons)
        {
            var risk = 5;
            if (source.blendShapeCount > 0)
            {
                risk += source.blendShapeCount >= 50 ? 50 : 35;
                reasons.Add($"{source.blendShapeCount} blendshapes require deformation review.");
            }
            if (!source.isReadable)
            {
                risk += 15;
                reasons.Add("Mesh Read/Write is disabled; topology scoring and some reduction backends may be unavailable until a generated readable copy is prepared.");
            }
            if (boneCount >= 60)
            {
                risk += 18;
                reasons.Add($"{boneCount} renderer bones increase deformation sensitivity.");
            }
            else if (boneCount >= 25)
            {
                risk += 10;
                reasons.Add($"{boneCount} renderer bones require skinning review.");
            }
            if (components >= 20)
            {
                risk += 22;
                reasons.Add($"{components} disconnected geometry groups may contain fragile small islands.");
            }
            else if (components >= 5)
            {
                risk += 10;
                reasons.Add($"{components} disconnected geometry groups were detected.");
            }

            var size = source.bounds.size;
            var largest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            var smallest = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
            if (largest > 0.00001f && smallest / largest < 0.015f)
            {
                risk += 12;
                reasons.Add("Very thin geometry is vulnerable to collapsed edges.");
            }

            if (renderer.sharedMaterials.Any(material => material != null && material.renderQueue >= 2450))
            {
                risk += 8;
                reasons.Add("Cutout or transparent material usage makes silhouette loss more visible.");
            }
            return Mathf.Clamp(risk, 0, 100);
        }

        private static void PopulateQuality(MeshCandidate candidate, RendererMeshChoice choice,
            MeshAnalysisUtility.ValidationResult validation, float requestedRatio)
        {
            candidate.Quality.StructuralIntegrity = validation.Status == MeshCandidateStatus.Safe ? 100 :
                validation.Status == MeshCandidateStatus.ReviewRequired ? 75 : 0;
            candidate.Quality.BoneWeightIntegrity = choice.Skinned ?
                (candidate.Mesh != null && candidate.Mesh.boneWeights.Length == candidate.Mesh.vertexCount ? 100 : 0) : 100;
            candidate.Quality.UvStability = HasCoherentUvPayload(choice.SourceMesh, candidate.Mesh) ? 100 : 0;
            candidate.Quality.NormalQuality = candidate.Mesh != null &&
                                              candidate.Mesh.normals.Length == candidate.Mesh.vertexCount ? 100 : 0;
            candidate.Quality.VisualEfficiency = Mathf.Clamp(Mathf.RoundToInt((1f - requestedRatio) * 100f), 0, 100);
            candidate.Quality.SilhouettePreservation = -1;
            candidate.Quality.DeformationQuality = -1;
            candidate.Quality.BlendShapeFidelity = -1;
            candidate.Quality.MeasurementNotes =
                "Structural payload checks are measured. Silhouette, deformation, and blendshape visual fidelity require the visual regression pass and remain unmeasured.";

            var minimumSafeRatio = choice.ReductionRisk >= 75 ? 0.85f : choice.ReductionRisk >= 50 ? 0.62f :
                choice.ReductionRisk >= 25 ? 0.42f : 0.18f;
            if (candidate.Status != MeshCandidateStatus.Rejected && requestedRatio < minimumSafeRatio)
            {
                candidate.Status = MeshCandidateStatus.HighRisk;
                candidate.ValidationMessage += " The requested reduction is below the automatic safety floor for this mesh's risk profile.";
            }
        }

        private static bool HasCoherentUvPayload(Mesh source, Mesh candidate)
        {
            if (candidate == null) return false;
            var sourceUv = new List<Vector2>();
            var candidateUv = new List<Vector2>();
            source.GetUVs(0, sourceUv);
            candidate.GetUVs(0, candidateUv);
            if (sourceUv.Count == 0) return candidateUv.Count == 0;
            return candidateUv.Count == candidate.vertexCount;
        }

        private static void CopyContract(AvatarBehaviorContract source, AvatarBehaviorContract destination)
        {
            destination.CapturedUtc = source.CapturedUtc;
            destination.ContractHash = source.ContractHash;
            destination.ResolutionState = source.ResolutionState;
            destination.Categories.Clear();
            destination.Categories.AddRange(source.Categories);
            destination.DetectedBuildSystems.Clear();
            destination.DetectedBuildSystems.AddRange(source.DetectedBuildSystems);
            destination.Warnings.Clear();
            destination.Warnings.AddRange(source.Warnings);
        }

        private static string SafeTransformIdentity(Transform transform, Transform root)
        {
            if (transform == null) return "<null>";
            try
            {
                return MeshAnalysisUtility.CalculateTransformPath(transform, root);
            }
            catch
            {
                return "<external>/" + transform.name;
            }
        }

        private static string BuildAvatarRoot(GameObject sourcePrefab, string sourceGuid)
        {
            const string generatedRoot = "Assets/MobileAvatarStudioGenerated";
            var guidPrefix = string.IsNullOrEmpty(sourceGuid) ? "NoGuid" : sourceGuid.Substring(0, Math.Min(8, sourceGuid.Length));
            return generatedRoot + "/" + MeshAnalysisUtility.SanitizeFileName(sourcePrefab.name) + "_" + guidPrefix;
        }

        private static string CreateJobRoot(MobileAvatarMeshRecipe recipe, bool replaceExisting)
        {
            const string generatedRoot = "Assets/MobileAvatarStudioGenerated";
            MeshAnalysisUtility.EnsureAssetFolder(generatedRoot);
            var avatarRoot = BuildAvatarRoot(recipe.SourcePrefab, recipe.SourceGuid);
            if (!avatarRoot.StartsWith(generatedRoot + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to replace a generated workspace outside " + generatedRoot);
            if (AssetDatabase.IsValidFolder(avatarRoot))
            {
                if (!replaceExisting)
                    throw new InvalidOperationException(
                        "A generated workspace already exists for this avatar, but its recipe could not be opened. " +
                        "Use Start Fresh only if you intentionally want to replace that workspace.");
                foreach (var recipeGuid in AssetDatabase.FindAssets("t:MobileAvatarMeshRecipe", new[] { avatarRoot }))
                {
                    var previousRecipe = AssetDatabase.LoadAssetAtPath<MobileAvatarMeshRecipe>(AssetDatabase.GUIDToAssetPath(recipeGuid));
                    if (previousRecipe != null) TextureConversionPipeline.RestoreMobileOverrides(previousRecipe, false);
                }
                if (!AssetDatabase.DeleteAsset(avatarRoot))
                    throw new InvalidOperationException("Could not replace the previous generated workspace: " + avatarRoot);
            }
            MeshAnalysisUtility.EnsureAssetFolder(avatarRoot);
            var jobRoot = avatarRoot + "/Current";
            MeshAnalysisUtility.EnsureAssetFolder(jobRoot);
            return jobRoot;
        }

        private static string BuildRendererFolderName(RendererMeshChoice choice)
        {
            var path = string.IsNullOrEmpty(choice.TransformPath) ? "Root" : choice.TransformPath.Replace('/', '_');
            var hash = unchecked((uint)choice.TransformPath.GetHashCode()).ToString("x8");
            return MeshAnalysisUtility.SanitizeFileName(path) + "_" + hash;
        }

        private static bool DisplayProgress(RendererMeshChoice choice, string level, int operation, int totalOperations)
        {
            return EditorUtility.DisplayCancelableProgressBar(
                "Mobile Avatar Studio - Generating Mesh Candidates",
                choice.DisplayName + " / " + level + "\nCancel pauses after the current AutoLOD operation; completed renderers stay saved.",
                Mathf.Clamp01(operation / (float)totalOperations));
        }
    }
}
