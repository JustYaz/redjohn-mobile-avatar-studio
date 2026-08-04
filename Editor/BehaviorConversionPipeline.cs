using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal sealed class BehaviorConversionResult
    {
        public string PrefabPath { get; set; }
        public string ReportPath { get; set; }
        public int CopiedControllers { get; set; }
        public int CopiedClips { get; set; }
        public int CopiedMenusAndParameters { get; set; }
        public int RemappedFloatBindings { get; set; }
        public int RemovedUnsupportedShaderBindings { get; set; }
        public int RemappedMaterialKeys { get; set; }
        public int RemappedUvTileBindings { get; set; }
        public int RemovedContentBindings { get; set; }
        public int FallbackContentBindings { get; set; }
        public int QuarantinedBrokenControllerTransitions { get; set; }
        public bool ValidationPassed { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    internal static class BehaviorConversionPipeline
    {
        private sealed class BehaviorGraph
        {
            public readonly HashSet<UnityEngine.Object> Assets = new HashSet<UnityEngine.Object>();
            public IEnumerable<AnimationClip> Clips => Assets.OfType<AnimationClip>();
        }

        private sealed class CurveSource
        {
            public AnimationCurve Curve;
            public BehaviorCurveMappingChoice Choice;
        }

        private sealed class CurveTarget
        {
            public EditorCurveBinding Binding;
            public readonly List<CurveSource> Sources = new List<CurveSource>();
        }

        public static void Analyze(MobileAvatarMeshRecipe recipe)
        {
            ValidateRecipeAndSource(recipe, false);
            var graph = CollectBehaviorGraph(recipe.SourcePrefab);
            var oldApprovals = recipe.BehaviorCurveChoices
                .Where(choice => choice.IsCurrentMappingApproved)
                .GroupBy(choice => choice.SourceProperty, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().MappingSignature, StringComparer.Ordinal);
            var oldStaticResolutions = recipe.BehaviorCurveChoices
                .Where(choice => choice.IsCurrentUnsupportedResolution)
                .GroupBy(choice => choice.SourceProperty, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().MappingSignature, StringComparer.Ordinal);

            var grouped = graph.Clips
                .SelectMany(clip => AnimationUtility.GetCurveBindings(clip)
                    .Where(binding => binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
                    .Select(binding => new { Clip = clip, Binding = binding }))
                .GroupBy(item => item.Binding.propertyName, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToArray();

            recipe.BehaviorCurveChoices.Clear();
            foreach (var group in grouped)
            {
                var choice = CreateMappingChoice(group.Key);
                if (group.All(item => IsUvTileGeometryBinding(recipe, item.Binding)))
                {
                    choice.Kind = BehaviorCurveMappingKind.GeometryActivation;
                    choice.TargetProperties = "generated child GameObject m_IsActive";
                    choice.Summary = "UV Tile Dissolve is rewritten to ordinary activation curves on isolated geometry pieces.";
                }
                choice.BindingCount = group.Count();
                foreach (var path in group.Select(item => AssetDatabase.GetAssetPath(item.Clip))
                             .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
                    choice.SourceClipPaths.Add(path);
                choice.MappingSignature = ComputeMappingSignature(choice);
                if (oldApprovals.TryGetValue(choice.SourceProperty, out var signature) &&
                    string.Equals(signature, choice.MappingSignature, StringComparison.Ordinal))
                    choice.ApproveCurrentMapping();
                if (choice.Kind == BehaviorCurveMappingKind.Unsupported &&
                    oldStaticResolutions.TryGetValue(choice.SourceProperty, out var resolutionSignature) &&
                    string.Equals(resolutionSignature, choice.MappingSignature, StringComparison.Ordinal))
                    choice.ResolveUnsupportedAsStaticMobileMaterial();
                recipe.BehaviorCurveChoices.Add(choice);
            }

            recipe.BehaviorStatus = graph.Assets.Count == 0
                ? "No custom behavior assets discovered"
                : $"Analyzed {graph.Assets.Count} behavior assets; review shader-property translations";
            recipe.BehaviorAppliedToCombined = false;
            ManualPolishPipeline.Invalidate(recipe, "Behavior graph changed; rebuild Stage 6 before manual polish");
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
        }

        public static bool CanBuild(MobileAvatarMeshRecipe recipe, out string reason)
        {
            try
            {
                ValidateRecipeAndSource(recipe, true);
            }
            catch (Exception exception)
            {
                reason = exception.Message;
                return false;
            }

            if (string.IsNullOrEmpty(recipe.FinalAssemblyUtc))
            {
                reason = "Rebuild the combined mobile prefab in Stage 5 after the latest material changes.";
                return false;
            }

            var unresolvedUnsupported = recipe.BehaviorCurveChoices.Count(choice =>
                choice.Kind == BehaviorCurveMappingKind.Unsupported && !choice.IsCurrentUnsupportedResolution);
            if (unresolvedUnsupported > 0)
            {
                reason = $"Choose a mobile fallback for the {unresolvedUnsupported} unsupported animated shader " +
                         "propert" + (unresolvedUnsupported == 1 ? "y" : "ies") + ".";
                return false;
            }
            if (!MobileContentPipeline.ValidateConfiguration(recipe, out reason)) return false;
            var unapproved = recipe.BehaviorCurveChoices.Count(choice =>
                choice.Kind == BehaviorCurveMappingKind.SuggestedTranslation && !choice.IsCurrentMappingApproved);
            if (unapproved > 0)
            {
                reason = $"Approve the {unapproved} suggested shader-property translation(s) first.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public static BehaviorConversionResult Build(MobileAvatarMeshRecipe recipe)
        {
            if (!CanBuild(recipe, out var reason)) throw new InvalidOperationException(reason);
            var sourceHashBefore = MeshAnalysisUtility.ComputeAssetHash(recipe.SourceAssetPath);
            var graph = CollectBehaviorGraph(recipe.SourcePrefab);
            ValidateMappingCoverage(graph, recipe);

            var behaviorRoot = recipe.OutputRoot + "/Behavior";
            if (AssetDatabase.IsValidFolder(behaviorRoot) && !AssetDatabase.DeleteAsset(behaviorRoot))
                throw new InvalidOperationException("Could not overwrite the generated Behavior checkpoint.");
            MeshAnalysisUtility.EnsureAssetFolder(behaviorRoot);

            var result = new BehaviorConversionResult { PrefabPath = recipe.CombinedQuestPrefabPath };
            var objectMap = CopyBehaviorAssets(graph, behaviorRoot, result);
            var materialMap = recipe.MaterialChoices
                .Where(choice => choice.SourceMaterial != null && choice.GeneratedMaterial != null)
                .ToDictionary(choice => choice.SourceMaterial, choice => choice.GeneratedMaterial);

            RemapCopiedAssetReferences(objectMap, materialMap);
            ApplyAnimationMappings(objectMap, recipe, materialMap, result);
            var contentResult = MobileContentPipeline.RewriteCopiedBehavior(objectMap, recipe);
            result.RemovedContentBindings = contentResult.RemovedBindings;
            result.FallbackContentBindings = contentResult.FallbackBindings;
            ApplyToCombinedPrefab(recipe, objectMap);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            ValidateOutput(recipe, graph, objectMap, materialMap, result);
            var sourceHashAfter = MeshAnalysisUtility.ComputeAssetHash(recipe.SourceAssetPath);
            if (!string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The PC source prefab changed during behavior conversion.");

            recipe.BehaviorPassUtc = DateTime.UtcNow.ToString("O");
            recipe.BehaviorPrefabPath = recipe.CombinedQuestPrefabPath;
            recipe.BehaviorAppliedToCombined = result.ValidationPassed;
            ManualPolishPipeline.Invalidate(recipe, result.ValidationPassed
                ? "Stage 6 rebuilt; manual material polish and final texture rescan required"
                : "Stage 6 validation failed; manual polish checkpoint unavailable");
            recipe.MobileResolvedAuditPassed = false;
            recipe.MobileValidationStatus = "Needs resolved mobile audit after behavior rebuild";
            recipe.BehaviorStatus = result.ValidationPassed
                ? "Isolated behavior applied; resolved build-system and Android SDK validation remain"
                : "Behavior validation failed";
            EditorUtility.SetDirty(recipe);
            WriteReport(recipe, result, sourceHashBefore, sourceHashAfter);
            MobileContentPipeline.WriteReport(recipe, contentResult, "Stage 6 isolated behavior rewrite");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            return result;
        }

        private static BehaviorGraph CollectBehaviorGraph(GameObject root)
        {
            var graph = new BehaviorGraph();
            var pendingPaths = new Queue<string>();
            var inspectedPaths = new HashSet<string>(StringComparer.Ordinal);

            foreach (var component in root.GetComponentsInChildren<Component>(true).Where(value => value != null))
            {
                foreach (var reference in EnumerateObjectReferences(component))
                    if (IsBehaviorObject(reference) && graph.Assets.Add(reference))
                        EnqueueAssetPath(reference, pendingPaths);
            }

            while (pendingPaths.Count > 0)
            {
                var path = pendingPaths.Dequeue();
                if (string.IsNullOrEmpty(path) || !inspectedPaths.Add(path)) continue;
                foreach (var dependencyPath in AssetDatabase.GetDependencies(path, true))
                foreach (var dependency in AssetDatabase.LoadAllAssetsAtPath(dependencyPath).Where(value => value != null))
                {
                    if (!IsBehaviorObject(dependency) || !graph.Assets.Add(dependency)) continue;
                    EnqueueAssetPath(dependency, pendingPaths);
                }
            }
            return graph;
        }

        private static void EnqueueAssetPath(UnityEngine.Object asset, Queue<string> pending)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(path)) pending.Enqueue(path);
        }

        private static bool IsBehaviorObject(UnityEngine.Object asset)
        {
            if (asset == null) return false;
            if (asset is RuntimeAnimatorController || asset is Motion || asset is AvatarMask) return true;
            var name = asset.GetType().FullName ?? asset.GetType().Name;
            return name.IndexOf("VRCExpressionsMenu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("VRCExpressionParameters", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<UnityEngine.Object> EnumerateObjectReferences(UnityEngine.Object owner)
        {
            SerializedObject serialized;
            try { serialized = new SerializedObject(owner); }
            catch { yield break; }
            var iterator = serialized.GetIterator();
            if (!iterator.Next(true)) yield break;
            do
            {
                if (iterator.propertyType == SerializedPropertyType.ObjectReference &&
                    iterator.objectReferenceValue != null)
                    yield return iterator.objectReferenceValue;
            } while (iterator.Next(true));
        }

        private static Dictionary<UnityEngine.Object, UnityEngine.Object> CopyBehaviorAssets(
            BehaviorGraph graph, string root, BehaviorConversionResult result)
        {
            var objectMap = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
            var copiedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in graph.Assets.OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal)
                         .ThenBy(asset => asset.name, StringComparer.Ordinal))
            {
                var sourcePath = AssetDatabase.GetAssetPath(source);
                if (string.IsNullOrEmpty(sourcePath) || sourcePath.StartsWith("Packages/", StringComparison.Ordinal))
                    continue;
                var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                var extractedModelClip = source is AnimationClip && extension != ".anim";
                var copyWholeFile = !extractedModelClip && (source is Motion ||
                                    extension == ".controller" || extension == ".overridecontroller" ||
                                    extension == ".anim" || extension == ".mask" ||
                                    extension == ".asset" && IsMenuOrParameters(source));
                if (copyWholeFile)
                {
                    if (!copiedPaths.TryGetValue(sourcePath, out var destination))
                    {
                        destination = BuildDestinationPath(root, sourcePath, source);
                        MeshAnalysisUtility.EnsureAssetFolder(Path.GetDirectoryName(destination)?.Replace('\\', '/'));
                        var copied = extension == ".controller"
                            ? CopyControllerWithoutOrphanedTransitions(sourcePath, destination, result)
                            : AssetDatabase.CopyAsset(sourcePath, destination);
                        if (!copied)
                            throw new InvalidOperationException("Unity failed to copy behavior asset: " + sourcePath);
                        copiedPaths[sourcePath] = destination;
                    }
                    var replacement = FindEquivalentAsset(source, destination);
                    if (replacement == null)
                        throw new InvalidOperationException("Could not identify copied behavior object: " + sourcePath + " / " + source.name);
                    objectMap[source] = replacement;
                }
                else if (source is AnimationClip sourceClip)
                {
                    var destination = BuildExtractedClipPath(root, sourceClip);
                    MeshAnalysisUtility.EnsureAssetFolder(root + "/Clips");
                    var copiedClip = new AnimationClip { name = sourceClip.name };
                    EditorUtility.CopySerialized(sourceClip, copiedClip);
                    AssetDatabase.CreateAsset(copiedClip, destination);
                    objectMap[source] = copiedClip;
                }
            }

            AssetDatabase.SaveAssets();
            foreach (var source in graph.Assets)
            {
                if (objectMap.ContainsKey(source)) continue;
                var path = AssetDatabase.GetAssetPath(source);
                if (string.IsNullOrEmpty(path) || path.StartsWith("Packages/", StringComparison.Ordinal)) continue;
                if (copiedPaths.TryGetValue(path, out var destination))
                {
                    var replacement = FindEquivalentAsset(source, destination);
                    if (replacement != null) objectMap[source] = replacement;
                }
            }

            result.CopiedControllers = objectMap.Keys.OfType<RuntimeAnimatorController>().Count();
            result.CopiedClips = objectMap.Keys.OfType<AnimationClip>().Count();
            result.CopiedMenusAndParameters = objectMap.Keys.Count(IsMenuOrParameters);
            return objectMap;
        }

        private static string BuildDestinationPath(string root, string sourcePath, UnityEngine.Object source)
        {
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            var category = extension == ".controller" || extension == ".overridecontroller" ||
                           source is RuntimeAnimatorController ? "Controllers" :
                source is AnimationClip ? "Clips" : source is Motion ? "Motions" : source is AvatarMask ? "Masks" :
                IsMenuOrParameters(source) ? "Expressions" : "Other";
            var guid = AssetDatabase.AssetPathToGUID(sourcePath);
            var suffix = string.IsNullOrEmpty(guid) ? Hash128.Compute(sourcePath).ToString().Substring(0, 8) : guid.Substring(0, 8);
            return root + "/" + category + "/" +
                   MeshAnalysisUtility.SanitizeFileName(Path.GetFileNameWithoutExtension(sourcePath)) + "_" + suffix +
                   Path.GetExtension(sourcePath);
        }

        private static string BuildExtractedClipPath(string root, AnimationClip clip)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out var guid, out long localId);
            var identity = (guid ?? string.Empty).Substring(0, Math.Min(8, (guid ?? string.Empty).Length)) + "_" + localId;
            return root + "/Clips/" + MeshAnalysisUtility.SanitizeFileName(clip.name) + "_" + identity + ".anim";
        }

        private static bool IsMenuOrParameters(UnityEngine.Object asset)
        {
            if (asset == null) return false;
            var name = asset.GetType().FullName ?? asset.GetType().Name;
            return name.IndexOf("VRCExpressionsMenu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("VRCExpressionParameters", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static UnityEngine.Object FindEquivalentAsset(UnityEngine.Object source, string destinationPath)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out _, out long sourceLocalId);
            var candidates = AssetDatabase.LoadAllAssetsAtPath(destinationPath)
                .Where(candidate => candidate != null && candidate.GetType() == source.GetType()).ToArray();
            foreach (var candidate in candidates)
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out _, out long candidateLocalId);
                if (candidateLocalId == sourceLocalId) return candidate;
            }
            return candidates.FirstOrDefault(candidate => candidate.name == source.name) ??
                   (candidates.Length == 1 ? candidates[0] : null);
        }

        private static void RemapCopiedAssetReferences(
            IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> objectMap,
            IReadOnlyDictionary<Material, Material> materialMap)
        {
            var identityMap = BuildStableIdentityMap(objectMap);
            var destinationPaths = objectMap.Values.Select(AssetDatabase.GetAssetPath)
                .Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var destinationPath in destinationPaths)
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(destinationPath).Where(value => value != null))
                RemapSerializedReferences(asset, objectMap, identityMap, materialMap);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private static bool RemapSerializedReferences(UnityEngine.Object owner,
            IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> objectMap,
            IReadOnlyDictionary<string, UnityEngine.Object> identityMap,
            IReadOnlyDictionary<Material, Material> materialMap)
        {
            SerializedObject serialized;
            try { serialized = new SerializedObject(owner); }
            catch { return false; }
            var iterator = serialized.GetIterator();
            if (!iterator.Next(true)) return false;
            var changed = false;
            do
            {
                if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                var current = iterator.objectReferenceValue;
                if (current == null)
                {
                    if (TryRecoverPathBackedReference(serialized, iterator, objectMap, identityMap,
                            out var recovered))
                    {
                        iterator.objectReferenceValue = recovered;
                        changed = true;
                    }
                    continue;
                }
                if (objectMap.TryGetValue(current, out var replacement))
                {
                    iterator.objectReferenceValue = replacement;
                    changed = true;
                }
                else if (identityMap.TryGetValue(StableIdentity(current), out replacement))
                {
                    iterator.objectReferenceValue = replacement;
                    changed = true;
                }
                else if (current is Material material && materialMap.TryGetValue(material, out var generated))
                {
                    iterator.objectReferenceValue = generated;
                    changed = true;
                }
            } while (iterator.Next(true));
            if (!changed) return false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(owner);
            return true;
        }

        private static bool TryRecoverPathBackedReference(SerializedObject serialized,
            SerializedProperty referenceProperty,
            IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> objectMap,
            IReadOnlyDictionary<string, UnityEngine.Object> identityMap,
            out UnityEngine.Object replacement)
        {
            replacement = null;
            if (!string.Equals(referenceProperty.name, "objRef", StringComparison.Ordinal)) return false;
            const string suffix = ".objRef";
            var propertyPath = referenceProperty.propertyPath;
            if (!propertyPath.EndsWith(suffix, StringComparison.Ordinal)) return false;
            var idProperty = serialized.FindProperty(
                propertyPath.Substring(0, propertyPath.Length - suffix.Length) + ".id");
            if (idProperty == null || idProperty.propertyType != SerializedPropertyType.String) return false;
            var id = idProperty.stringValue;
            var separator = string.IsNullOrEmpty(id) ? -1 : id.IndexOf('|');
            if (separator < 0 || separator + 1 >= id.Length) return false;
            var sourcePath = id.Substring(separator + 1).Replace('\\', '/');
            var source = AssetDatabase.LoadMainAssetAtPath(sourcePath);
            if (source == null) return false;
            if (objectMap.TryGetValue(source, out replacement)) return replacement != null;
            return identityMap.TryGetValue(StableIdentity(source), out replacement) && replacement != null;
        }

        private static Dictionary<string, UnityEngine.Object> BuildStableIdentityMap(
            IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> objectMap)
        {
            var result = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            foreach (var pair in objectMap)
            {
                var identity = StableIdentity(pair.Key);
                if (!string.IsNullOrEmpty(identity)) result[identity] = pair.Value;
            }
            return result;
        }

        private static string StableIdentity(UnityEngine.Object asset)
        {
            if (asset == null) return string.Empty;
            var path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path)) return string.Empty;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out _, out long localId);
            return path + "|" + localId + "|" + (asset.GetType().FullName ?? asset.GetType().Name);
        }

        private static void ApplyAnimationMappings(
            IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> objectMap,
            MobileAvatarMeshRecipe recipe,
            IReadOnlyDictionary<Material, Material> materialMap,
            BehaviorConversionResult result)
        {
            var choiceMap = recipe.BehaviorCurveChoices.ToDictionary(choice => choice.SourceProperty,
                choice => choice, StringComparer.Ordinal);
            foreach (var pair in objectMap.Where(pair => pair.Key is AnimationClip && pair.Value is AnimationClip))
            {
                var clip = (AnimationClip)pair.Value;
                var materialBindings = AnimationUtility.GetCurveBindings(clip)
                    .Where(binding => binding.propertyName.StartsWith("material.", StringComparison.Ordinal)).ToArray();
                var targets = new Dictionary<string, CurveTarget>(StringComparer.Ordinal);
                foreach (var binding in materialBindings)
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (TryRewriteUvTileGeometryBinding(recipe, clip, binding, curve, result))
                        continue;
                    if (!choiceMap.TryGetValue(binding.propertyName, out var choice))
                        throw new InvalidOperationException("Missing reviewed mapping for " + binding.propertyName);
                    if (choice.Kind == BehaviorCurveMappingKind.Unsupported)
                    {
                        if (!choice.IsCurrentUnsupportedResolution)
                            throw new InvalidOperationException("Unsupported shader property has no approved mobile fallback: " +
                                                                binding.propertyName);
                        AnimationUtility.SetEditorCurve(clip, binding, null);
                        result.RemovedUnsupportedShaderBindings++;
                        continue;
                    }
                    foreach (var targetProperty in choice.EnumerateTargets())
                    {
                        foreach (var targetPath in GetSplitAwareTargetPaths(recipe, binding.path))
                        {
                            var targetBinding = binding;
                            targetBinding.path = targetPath;
                            targetBinding.propertyName = targetProperty;
                            var key = targetBinding.path + "|" + targetBinding.type.AssemblyQualifiedName + "|" + targetProperty;
                            if (!targets.TryGetValue(key, out var target))
                                targets[key] = target = new CurveTarget { Binding = targetBinding };
                            target.Sources.Add(new CurveSource { Curve = curve, Choice = choice });
                        }
                    }
                    AnimationUtility.SetEditorCurve(clip, binding, null);
                    result.RemappedFloatBindings++;
                }

                foreach (var target in targets.Values)
                {
                    var selected = target.Sources.OrderByDescending(source => CurveMaximum(source.Curve)).First();
                    var scale = selected.Choice.Scale;
                    if (selected.Choice.NormalizeStrength)
                    {
                        var maximum = target.Sources.Select(source => CurveMaximum(source.Curve)).DefaultIfEmpty(2f).Max();
                        scale = maximum > 2f ? 2f / maximum : 1f;
                    }
                    AnimationUtility.SetEditorCurve(clip, target.Binding,
                        TransformCurve(selected.Curve, scale, selected.Choice.Offset));
                }

                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var split = UvTileSplitPipeline.FindEnabledSplit(recipe, binding.path);
                    if (split != null && binding.propertyName.IndexOf("material", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        RewriteSplitMaterialObjectBinding(clip, binding, split, materialMap, result);
                        continue;
                    }
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    var changed = false;
                    for (var index = 0; index < keys.Length; index++)
                    {
                        if (!(keys[index].value is Material material) ||
                            !TryResolveMaterialMapping(material, materialMap, out var generated))
                            continue;
                        keys[index].value = generated;
                        changed = true;
                        result.RemappedMaterialKeys++;
                    }
                    if (changed) AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);
                }
                RewriteSplitRendererFloatBindings(recipe, clip);
                EditorUtility.SetDirty(clip);
            }
        }

        private static void RewriteSplitMaterialObjectBinding(AnimationClip clip, EditorCurveBinding sourceBinding,
            UvTileSplitRenderer split, IReadOnlyDictionary<Material, Material> materialMap,
            BehaviorConversionResult result)
        {
            var keys = AnimationUtility.GetObjectReferenceCurve(clip, sourceBinding);
            var sourceSlot = TryGetMaterialArraySlot(sourceBinding.propertyName, out var slot) ? slot : -1;
            var pieces = split.Pieces.Where(piece => piece.KeepOnMobile &&
                (sourceSlot < 0 || piece.MaterialSlot == sourceSlot)).ToArray();
            if (pieces.Length == 0)
            {
                // The renderer or this material slot was intentionally excluded
                // from mobile. Its source material curve must not block the build
                // or leave a dangling reference on the mobile prefab.
                AnimationUtility.SetObjectReferenceCurve(clip, sourceBinding, null);
                return;
            }

            foreach (var piece in pieces)
            {
                var targetBinding = sourceBinding;
                targetBinding.path = UvTileSplitPipeline.BuildChildPath(split, piece);
                // Every generated UV piece has exactly one material slot, even when the
                // source renderer had several. Preserve the material curve on that slot.
                targetBinding.propertyName = RewriteMaterialArraySlot(sourceBinding.propertyName);
                var mapped = new ObjectReferenceKeyframe[keys.Length];
                for (var index = 0; index < keys.Length; index++)
                {
                    mapped[index].time = keys[index].time;
                    var source = keys[index].value;
                    if (source == null)
                    {
                        mapped[index].value = null;
                        continue;
                    }
                    if (!(source is Material material) ||
                        (!TryResolveMaterialMapping(material, materialMap, out var generated) &&
                         !TryResolveSplitPieceMaterialMapping(material, piece, materialMap, out generated)))
                        throw new InvalidOperationException($"{clip.name} references material '{source.name}' on " +
                                                            $"UV-split renderer {sourceBinding.path}, but no generated mobile " +
                                                            "material mapping exists for that animated material.");
                    mapped[index].value = generated;
                    result.RemappedMaterialKeys++;
                }
                AnimationUtility.SetObjectReferenceCurve(clip, targetBinding, mapped);
            }
            AnimationUtility.SetObjectReferenceCurve(clip, sourceBinding, null);
        }

        private static bool TryResolveMaterialMapping(Material source,
            IReadOnlyDictionary<Material, Material> materialMap, out Material generated)
        {
            generated = null;
            if (source == null || materialMap == null) return false;
            if (materialMap.TryGetValue(source, out generated) && generated != null) return true;
            if (materialMap.Values.Contains(source))
            {
                generated = source;
                return true;
            }

            // Animation object-reference curves can deserialize a fresh Material
            // wrapper even when its asset GUID/local ID is the same. Resolve by
            // stable asset identity before falling back to a unique material name.
            var sourceIdentity = StableIdentity(source);
            if (!string.IsNullOrEmpty(sourceIdentity))
            {
                foreach (var pair in materialMap)
                {
                    if (string.Equals(StableIdentity(pair.Key), sourceIdentity, StringComparison.Ordinal) &&
                        pair.Value != null)
                    {
                        generated = pair.Value;
                        return true;
                    }
                }
            }

            Material named = null;
            foreach (var pair in materialMap)
            {
                if (pair.Key == null || !string.Equals(pair.Key.name, source.name, StringComparison.Ordinal)) continue;
                if (named != null) return false; // ambiguous names are unsafe
                named = pair.Value;
            }
            if (named == null) return false;
            generated = named;
            return true;
        }

        private static bool TryResolveSplitPieceMaterialMapping(Material source, UvTilePieceChoice piece,
            IReadOnlyDictionary<Material, Material> materialMap, out Material generated)
        {
            generated = null;
            if (piece == null || piece.IsolatedSourceMaterial == null) return false;
            if (piece.SourceMaterial != source &&
                !string.Equals(StableIdentity(piece.SourceMaterial), StableIdentity(source), StringComparison.Ordinal))
                return false;
            return TryResolveMaterialMapping(piece.IsolatedSourceMaterial, materialMap, out generated);
        }

        private static bool TryGetMaterialArraySlot(string propertyName, out int slot)
        {
            slot = -1;
            const string prefix = "m_Materials.Array.data[";
            if (string.IsNullOrEmpty(propertyName) || !propertyName.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            var start = prefix.Length;
            var end = propertyName.IndexOf(']', start);
            return end > start && int.TryParse(propertyName.Substring(start, end - start), out slot) && slot >= 0;
        }

        private static string RewriteMaterialArraySlot(string propertyName)
        {
            return TryGetMaterialArraySlot(propertyName, out _) ? "m_Materials.Array.data[0]" : propertyName;
        }

        private static IEnumerable<string> GetSplitAwareTargetPaths(MobileAvatarMeshRecipe recipe, string sourcePath)
        {
            var split = UvTileSplitPipeline.FindEnabledSplit(recipe, sourcePath);
            if (split == null) return new[] { sourcePath };
            return split.Pieces.Where(piece => piece.KeepOnMobile)
                .Select(piece => UvTileSplitPipeline.BuildChildPath(split, piece)).ToArray();
        }

        private static void RewriteSplitRendererFloatBindings(MobileAvatarMeshRecipe recipe, AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip).ToArray())
            {
                var split = UvTileSplitPipeline.FindEnabledSplit(recipe, binding.path);
                if (split == null || binding.propertyName.StartsWith("material.", StringComparison.Ordinal)) continue;
                var blendShape = binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal);
                var rendererEnabled = string.Equals(binding.propertyName, "m_Enabled", StringComparison.Ordinal) &&
                                      typeof(Renderer).IsAssignableFrom(binding.type);
                if (!blendShape && !rendererEnabled) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                foreach (var piece in split.Pieces.Where(piece => piece.KeepOnMobile))
                {
                    var target = binding;
                    target.path = UvTileSplitPipeline.BuildChildPath(split, piece);
                    AnimationUtility.SetEditorCurve(clip, target, curve);
                }
                AnimationUtility.SetEditorCurve(clip, binding, null);
            }
        }

        private static bool IsUvTileGeometryBinding(MobileAvatarMeshRecipe recipe, EditorCurveBinding binding)
        {
            var property = binding.propertyName.StartsWith("material.", StringComparison.Ordinal)
                ? binding.propertyName.Substring("material.".Length)
                : binding.propertyName;
            return UvTileSplitPipeline.TryParseTileProperty(property, out _, out _) &&
                   UvTileSplitPipeline.FindEnabledSplit(recipe, binding.path) != null;
        }

        private static bool TryRewriteUvTileGeometryBinding(MobileAvatarMeshRecipe recipe, AnimationClip clip,
            EditorCurveBinding binding, AnimationCurve sourceCurve, BehaviorConversionResult result)
        {
            if (!IsUvTileGeometryBinding(recipe, binding)) return false;
            var property = binding.propertyName.StartsWith("material.", StringComparison.Ordinal)
                ? binding.propertyName.Substring("material.".Length)
                : binding.propertyName;
            UvTileSplitPipeline.TryParseTileProperty(property, out var row, out var column);
            var split = UvTileSplitPipeline.FindEnabledSplit(recipe, binding.path);
            var targets = split.Pieces.Where(piece => piece.KeepOnMobile && !piece.AlwaysVisible &&
                                                     piece.BehaviorMode == UvTilePieceBehaviorMode.FollowSourceToggle &&
                                                     piece.TileRow == row && piece.TileColumn == column).ToArray();
            if (sourceCurve != null && sourceCurve.keys.Any(key =>
                    Mathf.Abs(key.value) > 0.001f && Mathf.Abs(key.value - 1f) > 0.001f))
                throw new InvalidOperationException($"{clip.name} animates {binding.path}/{property} with non-binary " +
                                                    "values. Geometry activation cannot reproduce a dissolve fade, so the build was safely stopped.");

            foreach (var piece in targets)
            {
                var targetBinding = EditorCurveBinding.FloatCurve(
                    UvTileSplitPipeline.BuildChildPath(split, piece), typeof(GameObject), "m_IsActive");
                var keys = sourceCurve == null
                    ? Array.Empty<Keyframe>()
                    : sourceCurve.keys.Select(key => new Keyframe(key.time, key.value > 0.999f ? 0f : 1f,
                        -key.inTangent, -key.outTangent, key.inWeight, key.outWeight)).ToArray();
                var targetCurve = new AnimationCurve(keys)
                {
                    preWrapMode = sourceCurve?.preWrapMode ?? WrapMode.Default,
                    postWrapMode = sourceCurve?.postWrapMode ?? WrapMode.Default
                };
                AnimationUtility.SetEditorCurve(clip, targetBinding, targetCurve);
                result.RemappedUvTileBindings++;
            }
            AnimationUtility.SetEditorCurve(clip, binding, null);
            result.RemovedUnsupportedShaderBindings++;
            return true;
        }

        private static void ApplyToCombinedPrefab(MobileAvatarMeshRecipe recipe,
            IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> objectMap)
        {
            var root = PrefabUtility.LoadPrefabContents(recipe.CombinedQuestPrefabPath);
            if (root == null) throw new InvalidOperationException("Could not load the combined Quest prefab.");
            try
            {
                var emptyMaterials = new Dictionary<Material, Material>();
                var identityMap = BuildStableIdentityMap(objectMap);
                RestoreSourceBehaviorReferences(root, recipe.SourcePrefab, objectMap, identityMap);
                foreach (var component in root.GetComponentsInChildren<Component>(true).Where(value => value != null))
                    RemapSerializedReferences(component, objectMap, identityMap, emptyMaterials);
                MobileAvatarStudioBuildMarkerUtility.EnsureMarker(root, recipe);
                if (PrefabUtility.SaveAsPrefabAsset(root, recipe.CombinedQuestPrefabPath) == null)
                    throw new InvalidOperationException("Unity failed to save the behavior-isolated Quest prefab.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void RestoreSourceBehaviorReferences(GameObject generatedRoot, GameObject sourceRoot,
            IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> objectMap,
            IReadOnlyDictionary<string, UnityEngine.Object> identityMap)
        {
            if (generatedRoot == null || sourceRoot == null) return;
            foreach (var sourceComponent in sourceRoot.GetComponentsInChildren<Component>(true)
                         .Where(value => value != null))
            {
                var sourceTransform = sourceComponent.transform;
                var path = AnimationUtility.CalculateTransformPath(sourceTransform, sourceRoot.transform);
                var generatedTransform = string.IsNullOrEmpty(path)
                    ? generatedRoot.transform
                    : generatedRoot.transform.Find(path);
                if (generatedTransform == null) continue;

                var componentType = sourceComponent.GetType();
                var sourcePeers = sourceTransform.GetComponents(componentType);
                var componentIndex = Array.IndexOf(sourcePeers, sourceComponent);
                var generatedPeers = generatedTransform.GetComponents(componentType);
                if (componentIndex < 0 || componentIndex >= generatedPeers.Length) continue;
                RestoreSourceBehaviorReferences(sourceComponent, generatedPeers[componentIndex], objectMap,
                    identityMap);
            }
        }

        private static void RestoreSourceBehaviorReferences(Component source, Component generated,
            IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> objectMap,
            IReadOnlyDictionary<string, UnityEngine.Object> identityMap)
        {
            SerializedObject sourceSerialized;
            SerializedObject generatedSerialized;
            try
            {
                sourceSerialized = new SerializedObject(source);
                generatedSerialized = new SerializedObject(generated);
            }
            catch
            {
                return;
            }

            var iterator = sourceSerialized.GetIterator();
            if (!iterator.Next(true)) return;
            var changed = false;
            do
            {
                if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                var sourceReference = iterator.objectReferenceValue;
                if (!IsBehaviorObject(sourceReference) ||
                    !TryResolveGeneratedBehaviorReference(sourceReference, objectMap, identityMap,
                        out var replacement)) continue;
                var targetProperty = generatedSerialized.FindProperty(iterator.propertyPath);
                if (targetProperty == null ||
                    targetProperty.propertyType != SerializedPropertyType.ObjectReference ||
                    targetProperty.objectReferenceValue == replacement) continue;
                targetProperty.objectReferenceValue = replacement;
                changed = true;
            } while (iterator.Next(true));

            if (!changed) return;
            generatedSerialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(generated);
        }

        private static bool TryResolveGeneratedBehaviorReference(UnityEngine.Object sourceReference,
            IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> objectMap,
            IReadOnlyDictionary<string, UnityEngine.Object> identityMap,
            out UnityEngine.Object replacement)
        {
            if (objectMap.TryGetValue(sourceReference, out replacement) && replacement != null) return true;
            if (identityMap.TryGetValue(StableIdentity(sourceReference), out replacement) && replacement != null)
                return true;
            var sourcePath = AssetDatabase.GetAssetPath(sourceReference).Replace('\\', '/');
            if (sourcePath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                replacement = sourceReference;
                return true;
            }
            replacement = null;
            return false;
        }

        private static void ValidateOutput(MobileAvatarMeshRecipe recipe, BehaviorGraph graph,
            IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> objectMap,
            IReadOnlyDictionary<Material, Material> materialMap, BehaviorConversionResult result)
        {
            var errors = new List<string>();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            if (prefab == null) errors.Add("Combined Quest prefab did not reload.");
            else
            {
                ValidateRequiredBehaviorReferences(recipe.SourcePrefab, prefab, objectMap, errors);
                foreach (var component in prefab.GetComponentsInChildren<Component>(true).Where(value => value != null))
                {
                foreach (var reference in EnumerateObjectReferences(component).Where(IsBehaviorObject))
                {
                    var path = AssetDatabase.GetAssetPath(reference);
                    if (path.StartsWith("Assets/", StringComparison.Ordinal) &&
                        !path.StartsWith(recipe.OutputRoot + "/Behavior/", StringComparison.Ordinal))
                        errors.Add("Prefab still references source behavior asset: " + path);
                }
                    foreach (var dangling in FindDanglingPathBackedReferences(component))
                        errors.Add("Prefab contains a missing path-backed behavior reference: " + dangling);
                }
            }

            var generatedMaterials = new HashSet<Material>(materialMap.Values);
            foreach (var copiedClip in objectMap.Values.OfType<AnimationClip>().Distinct())
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(copiedClip)
                             .Where(binding => binding.propertyName.StartsWith("material.", StringComparison.Ordinal)))
                    if (!TargetSupports(binding.propertyName))
                        errors.Add("Copied clip targets unsupported Toon Standard property: " +
                                   AssetDatabase.GetAssetPath(copiedClip) + " -> " + binding.propertyName);
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(copiedClip))
                foreach (var key in AnimationUtility.GetObjectReferenceCurve(copiedClip, binding))
                    if (key.value is Material material && !generatedMaterials.Contains(material))
                        errors.Add("Copied clip still references a source material: " +
                                   AssetDatabase.GetAssetPath(copiedClip) + " -> " + AssetDatabase.GetAssetPath(material));
            }

            foreach (var copied in objectMap.Values.Where(value => value != null))
            {
                var copiedPath = AssetDatabase.GetAssetPath(copied);
                foreach (var dependency in AssetDatabase.GetDependencies(copiedPath, true))
                {
                    if (!dependency.StartsWith("Assets/", StringComparison.Ordinal) ||
                        dependency.StartsWith(recipe.OutputRoot + "/Behavior/", StringComparison.Ordinal)) continue;
                    if (graph.Assets.Any(source => string.Equals(AssetDatabase.GetAssetPath(source), dependency,
                            StringComparison.Ordinal)))
                        errors.Add("Copied behavior asset " + copiedPath +
                                   " still depends on source behavior asset: " + dependency);
                }
            }

            result.ValidationPassed = errors.Count == 0;
            foreach (var error in errors.Distinct(StringComparer.Ordinal)) result.Warnings.Add("ERROR: " + error);
            if (recipe.SourceBehaviorContract.DetectedBuildSystems.Count > 0)
                result.Warnings.Add("Build-time systems remain unresolved: " +
                                    string.Join(", ", recipe.SourceBehaviorContract.DetectedBuildSystems) + ".");
            result.Warnings.Add("A real VRChat SDK Android build and runtime menu/parameter test have not run.");
        }

        private static void ValidateRequiredBehaviorReferences(GameObject sourceRoot, GameObject generatedRoot,
            IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> objectMap, ICollection<string> errors)
        {
            if (sourceRoot == null || generatedRoot == null) return;
            var identityMap = BuildStableIdentityMap(objectMap);
            foreach (var sourceComponent in sourceRoot.GetComponentsInChildren<Component>(true)
                         .Where(value => value != null))
            {
                var path = AnimationUtility.CalculateTransformPath(sourceComponent.transform, sourceRoot.transform);
                var generatedTransform = string.IsNullOrEmpty(path)
                    ? generatedRoot.transform
                    : generatedRoot.transform.Find(path);
                if (generatedTransform == null) continue;
                var componentType = sourceComponent.GetType();
                var componentIndex = Array.IndexOf(sourceComponent.transform.GetComponents(componentType),
                    sourceComponent);
                var generatedPeers = generatedTransform.GetComponents(componentType);
                if (componentIndex < 0 || componentIndex >= generatedPeers.Length) continue;

                SerializedObject sourceSerialized;
                SerializedObject generatedSerialized;
                try
                {
                    sourceSerialized = new SerializedObject(sourceComponent);
                    generatedSerialized = new SerializedObject(generatedPeers[componentIndex]);
                }
                catch
                {
                    continue;
                }
                var iterator = sourceSerialized.GetIterator();
                if (!iterator.Next(true)) continue;
                do
                {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference ||
                        !IsBehaviorObject(iterator.objectReferenceValue) ||
                        !TryResolveGeneratedBehaviorReference(iterator.objectReferenceValue, objectMap, identityMap,
                            out var expected)) continue;
                    var actualProperty = generatedSerialized.FindProperty(iterator.propertyPath);
                    if (actualProperty != null &&
                        actualProperty.propertyType == SerializedPropertyType.ObjectReference &&
                        actualProperty.objectReferenceValue == expected) continue;
                    errors.Add("Prefab behavior reference is missing after regeneration: " +
                               (string.IsNullOrEmpty(path) ? "<avatar root>" : path) + " / " +
                               componentType.Name + " / " + iterator.propertyPath);
                } while (iterator.Next(true));
            }
        }

        private static IEnumerable<string> FindDanglingPathBackedReferences(UnityEngine.Object owner)
        {
            SerializedObject serialized;
            try { serialized = new SerializedObject(owner); }
            catch { yield break; }
            var iterator = serialized.GetIterator();
            if (!iterator.Next(true)) yield break;
            do
            {
                if (iterator.propertyType != SerializedPropertyType.ObjectReference ||
                    iterator.objectReferenceValue != null ||
                    !string.Equals(iterator.name, "objRef", StringComparison.Ordinal)) continue;
                const string suffix = ".objRef";
                var propertyPath = iterator.propertyPath;
                if (!propertyPath.EndsWith(suffix, StringComparison.Ordinal)) continue;
                var idProperty = serialized.FindProperty(
                    propertyPath.Substring(0, propertyPath.Length - suffix.Length) + ".id");
                if (idProperty == null || idProperty.propertyType != SerializedPropertyType.String ||
                    string.IsNullOrEmpty(idProperty.stringValue)) continue;
                yield return owner.name + " -> " + idProperty.stringValue;
            } while (iterator.Next(true));
        }

        private static void ValidateMappingCoverage(BehaviorGraph graph, MobileAvatarMeshRecipe recipe)
        {
            var current = new HashSet<string>(graph.Clips.SelectMany(AnimationUtility.GetCurveBindings)
                .Where(binding => binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
                .Select(binding => binding.propertyName), StringComparer.Ordinal);
            var reviewed = new HashSet<string>(recipe.BehaviorCurveChoices.Select(choice => choice.SourceProperty),
                StringComparer.Ordinal);
            if (!current.SetEquals(reviewed))
                throw new InvalidOperationException("The reachable animation graph changed after behavior analysis. Scan it again.");
            foreach (var choice in recipe.BehaviorCurveChoices.Where(choice =>
                         choice.Kind == BehaviorCurveMappingKind.GeometryActivation))
            {
                var bindings = graph.Clips.SelectMany(clip => AnimationUtility.GetCurveBindings(clip))
                    .Where(binding => string.Equals(binding.propertyName, choice.SourceProperty,
                        StringComparison.Ordinal));
                if (bindings.Any(binding => !IsUvTileGeometryBinding(recipe, binding)))
                    throw new InvalidOperationException("UV tile split selections changed after behavior analysis. " +
                                                        "Scan / Refresh Behavior Graph again before Stage 6.");
            }
        }

        private static BehaviorCurveMappingChoice CreateMappingChoice(string sourceProperty)
        {
            var choice = new BehaviorCurveMappingChoice { SourceProperty = sourceProperty };
            if (Contains(sourceProperty, "EmissionHueShift"))
                SetSuggestion(choice, "material._EmissionHueShift", Mathf.PI * 2f, 0f, false,
                    "Hue values are translated from normalized turns to Toon Standard radians.");
            else if (Contains(sourceProperty, "MainHueShift") || Contains(sourceProperty, "MatcapHueShift") ||
                     Contains(sourceProperty, "GlobalThemeHue"))
                SetSuggestion(choice, "material._HueShift", Mathf.PI * 2f, 0f, false,
                    "Main, matcap, or theme hue is approximated with Toon Standard global hue.");
            else if (Contains(sourceProperty, "EmissionStrength") || Contains(sourceProperty, "PPEmissionMultiplier"))
                SetSuggestion(choice, "material._EmissionStrength", 1f, 0f, true,
                    "Emission strength is normalized to Toon Standard's practical range.");
            else if (Contains(sourceProperty, "EmissionSaturation"))
                SetSuggestion(choice,
                    "material._EmissionColor.r, material._EmissionColor.g, material._EmissionColor.b",
                    1f, 1f, false, "Emission saturation is approximated across the RGB emission color channels.");
            else if (Contains(sourceProperty, "_Saturation_LongHair"))
                SetSuggestion(choice, "material._ShadowAlbedo", 1f / 1.65f, 1f / 1.65f, false,
                    "Hair saturation is approximated using Toon Standard shadow albedo.");
            else if (Contains(sourceProperty, "AlphaAlphaAdd_LongHair"))
                SetSuggestion(choice, "material._Color.r, material._Color.g, material._Color.b", .5f, .5f, false,
                    "Hair value is approximated across the RGB base-color channels.");
            else if (Contains(sourceProperty, "DecalBlendAlpha1_LongHair"))
                SetSuggestion(choice,
                    "material._ColorMaskColor1.r, material._ColorMaskColor1.g, material._ColorMaskColor1.b",
                    -1f, 1f, false, "The first hair split mask is translated to Toon Standard color-mask channel one.");
            else if (Contains(sourceProperty, "Decal1GlobalMaskBlendType_LongHair"))
                SetSuggestion(choice, "material._ColorMaskBlendMode", -1f, 2f, false,
                    "The hair split mode is translated to Toon Standard's color-mask blend mode.");
            else if (TargetSupports(sourceProperty))
            {
                choice.TargetProperties = sourceProperty;
                choice.Kind = BehaviorCurveMappingKind.ExactProperty;
                choice.Summary = "The generated mobile shader exposes this exact property. Curve values are preserved.";
            }
            else
            {
                choice.Kind = BehaviorCurveMappingKind.Unsupported;
                choice.TargetProperties = string.Empty;
                choice.Summary = "No verified Toon Standard property translation is available. The tool refuses to silently drop this control.";
            }

            if (choice.Kind != BehaviorCurveMappingKind.Unsupported &&
                choice.EnumerateTargets().Any(target => !TargetSupports(target)))
            {
                choice.Kind = BehaviorCurveMappingKind.Unsupported;
                choice.Summary = "The proposed translation is unavailable in the installed Toon Standard shader.";
            }
            return choice;
        }

        private static void SetSuggestion(BehaviorCurveMappingChoice choice, string targets, float scale,
            float offset, bool normalizeStrength, string summary)
        {
            choice.TargetProperties = targets;
            choice.Scale = scale;
            choice.Offset = offset;
            choice.NormalizeStrength = normalizeStrength;
            choice.Kind = BehaviorCurveMappingKind.SuggestedTranslation;
            choice.Summary = summary;
        }

        private static bool Contains(string value, string part) =>
            value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool TargetSupports(string property)
        {
            var shader = Shader.Find(MaterialConversionPipeline.TargetShaderName);
            if (shader == null) return false;
            var propertyName = property.StartsWith("material.", StringComparison.Ordinal)
                ? property.Substring("material.".Length) : property;
            var separator = propertyName.LastIndexOf('.');
            if (separator > 0 && propertyName.Length - separator == 2 &&
                "rgba xyzw".IndexOf(propertyName[separator + 1]) >= 0)
                propertyName = propertyName.Substring(0, separator);
            var material = new Material(shader);
            try { return material.HasProperty(propertyName); }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        private static string ComputeMappingSignature(BehaviorCurveMappingChoice choice)
        {
            return MeshAnalysisUtility.ComputeStringSignature(new[]
            {
                choice.SourceProperty ?? string.Empty,
                choice.TargetProperties ?? string.Empty,
                choice.Scale.ToString("R"), choice.Offset.ToString("R"),
                choice.NormalizeStrength.ToString(), choice.Kind.ToString(),
                choice.BindingCount.ToString(),
                string.Join("|", choice.SourceClipPaths.OrderBy(path => path, StringComparer.Ordinal))
            });
        }

        private static float CurveMaximum(AnimationCurve curve) =>
            curve == null || curve.length == 0 ? 0f : curve.keys.Max(key => key.value);

        private static AnimationCurve TransformCurve(AnimationCurve source, float scale, float offset)
        {
            var keys = source.keys;
            for (var index = 0; index < keys.Length; index++)
            {
                var key = keys[index];
                key.value = key.value * scale + offset;
                if (!float.IsInfinity(key.inTangent)) key.inTangent *= scale;
                if (!float.IsInfinity(key.outTangent)) key.outTangent *= scale;
                keys[index] = key;
            }
            return new AnimationCurve(keys) { preWrapMode = source.preWrapMode, postWrapMode = source.postWrapMode };
        }

        private static void ValidateRecipeAndSource(MobileAvatarMeshRecipe recipe, bool requireCombined)
        {
            if (recipe == null || recipe.SourcePrefab == null)
                throw new InvalidOperationException("Open a saved Mobile Avatar Studio recipe first.");
            var sourcePath = AssetDatabase.GetAssetPath(recipe.SourcePrefab);
            if (!string.Equals(sourcePath, recipe.SourceAssetPath, StringComparison.Ordinal) ||
                !string.Equals(MeshAnalysisUtility.ComputeAssetHash(sourcePath), recipe.SourceFileHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The PC source prefab changed after analysis. Re-analyze before behavior conversion.");
            if (requireCombined && (string.IsNullOrEmpty(recipe.CombinedQuestPrefabPath) ||
                                    AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath) == null))
                throw new InvalidOperationException("Build the combined Quest prefab in Stage 5 first.");
        }

        private static void WriteReport(MobileAvatarMeshRecipe recipe, BehaviorConversionResult result,
            string sourceHashBefore, string sourceHashAfter)
        {
            var reportRoot = recipe.OutputRoot + "/Reports";
            MeshAnalysisUtility.EnsureAssetFolder(reportRoot);
            result.ReportPath = reportRoot + "/BehaviorConversionReport.txt";
            var text = new StringBuilder();
            text.AppendLine("MOBILE AVATAR STUDIO - ISOLATED BEHAVIOR DRAFT");
            text.AppendLine("Generated: " + DateTime.UtcNow.ToString("O"));
            text.AppendLine("Status: " + recipe.BehaviorStatus);
            text.AppendLine("Source: " + recipe.SourceAssetPath);
            text.AppendLine("Combined prefab: " + recipe.CombinedQuestPrefabPath);
            text.AppendLine("Source unchanged: " + string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.OrdinalIgnoreCase));
            text.AppendLine($"Copied controllers: {result.CopiedControllers}; clips: {result.CopiedClips}; menus/parameters: {result.CopiedMenusAndParameters}");
            if (result.QuarantinedBrokenControllerTransitions > 0)
                text.AppendLine($"Quarantined broken orphaned controller transitions: {result.QuarantinedBrokenControllerTransitions}");
            text.AppendLine($"Remapped shader-property bindings: {result.RemappedFloatBindings}; " +
                            $"unsupported bindings removed from mobile copies: {result.RemovedUnsupportedShaderBindings}; " +
                            $"material keyframes: {result.RemappedMaterialKeys}");
            text.AppendLine($"Mobile content bindings removed: {result.RemovedContentBindings}; fallback bindings added: {result.FallbackContentBindings}");
            text.AppendLine("Validation: " + (result.ValidationPassed ? "PASS" : "FAIL"));
            text.AppendLine();
            text.AppendLine("MAPPINGS");
            foreach (var choice in recipe.BehaviorCurveChoices)
                text.AppendLine($"- {choice.SourceProperty} -> {choice.TargetProperties} | {choice.Kind} | " +
                                $"unsupportedResolution={choice.UnsupportedResolution} | bindings={choice.BindingCount} | " +
                                $"scale={choice.Scale:R} offset={choice.Offset:R} normalize={choice.NormalizeStrength}");
            text.AppendLine();
            text.AppendLine("WARNINGS / UNRESOLVED RELEASE CHECKS");
            foreach (var warning in result.Warnings) text.AppendLine("- " + warning);
            File.WriteAllText(result.ReportPath, text.ToString(), Encoding.UTF8);
            AssetDatabase.ImportAsset(result.ReportPath, ImportAssetOptions.ForceUpdate);
        }

        private static bool CopyControllerWithoutOrphanedTransitions(string sourcePath, string destinationPath,
            BehaviorConversionResult result)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot)) return false;
            var sourceAbsolute = Path.Combine(projectRoot, sourcePath.Replace('/', Path.DirectorySeparatorChar));
            var destinationAbsolute = Path.Combine(projectRoot, destinationPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourceAbsolute)) return false;

            try
            {
                var yaml = File.ReadAllText(sourceAbsolute);
                var sanitized = RemoveUnreferencedBrokenStateTransitions(yaml, out var removed);
                MeshAnalysisUtility.EnsureAssetFolder(Path.GetDirectoryName(destinationPath)?.Replace('\\', '/'));
                File.Copy(sourceAbsolute, destinationAbsolute, true);
                if (removed > 0)
                {
                    File.WriteAllText(destinationAbsolute, sanitized, new UTF8Encoding(false));
                    result.QuarantinedBrokenControllerTransitions += removed;
                }

                AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceSynchronousImport);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Mobile Avatar Studio could not sanitize controller copy; falling back to Unity copy: " +
                                 exception.Message);
                return AssetDatabase.CopyAsset(sourcePath, destinationPath);
            }
        }

        private static string RemoveUnreferencedBrokenStateTransitions(string yaml, out int removed)
        {
            removed = 0;
            if (string.IsNullOrEmpty(yaml)) return yaml;

            var stateIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(yaml, "^--- !u!1102 &(-?\\d+)\\s*$",
                         RegexOptions.Multiline))
                stateIds.Add(match.Groups[1].Value);

            var transitions = Regex.Matches(yaml, "^--- !u!1101 &(-?\\d+)\\s*$.*?(?=^--- !u!|\\z)",
                RegexOptions.Multiline | RegexOptions.Singleline);
            var ranges = new List<Tuple<int, int>>();
            foreach (Match transition in transitions)
            {
                var destination = Regex.Match(transition.Value,
                    "m_DstState:\\s*\\{fileID:\\s*(-?\\d+)\\}");
                if (!destination.Success || destination.Groups[1].Value == "0" ||
                    stateIds.Contains(destination.Groups[1].Value)) continue;

                // Only remove an orphan object. If a live state still references this transition,
                // leave it intact so a real behavior problem is surfaced instead of silently changing it.
                var transitionId = transition.Groups[1].Value;
                var withoutBlock = yaml.Remove(transition.Index, transition.Length);
                if (Regex.IsMatch(withoutBlock, "\\{fileID:\\s*" + Regex.Escape(transitionId) + "\\s*\\}"))
                    continue;
                ranges.Add(Tuple.Create(transition.Index, transition.Length));
            }

            if (ranges.Count == 0) return yaml;
            var builder = new StringBuilder(yaml);
            for (var index = ranges.Count - 1; index >= 0; index--)
                builder.Remove(ranges[index].Item1, ranges[index].Item2);
            removed = ranges.Count;
            return builder.ToString();
        }
    }
}
