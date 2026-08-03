using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Animations;

namespace MobileAvatarStudio.Editor
{
    internal static class MobileComponentRepairPipeline
    {
        public static void Refresh(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            if (prefab == null) throw new InvalidOperationException("The clean mobile upload prefab is missing.");

            var hiddenRemoved = recipe.MobileComponentRepairChoices
                .Where(choice => choice.RemoveFromMobile && !choice.PresentInUploadPrefab)
                .ToList();
            if (hiddenRemoved.Count > 0 &&
                AssetDatabase.LoadAssetAtPath<GameObject>(recipe.MobileComponentRestoreCachePath) == null &&
                recipe.SourcePrefab != null)
                SaveRestoreCache(recipe.SourcePrefab, recipe, true);
            var restoreCache = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.MobileComponentRestoreCachePath);

            var previous = recipe.MobileComponentRepairChoices
                .Where(choice => !(choice.RemoveFromMobile && !choice.PresentInUploadPrefab))
                .GroupBy(choice => choice.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var choice in previous.Values) choice.PresentInUploadPrefab = false;

            var current = new List<MobileComponentRepairChoice>();
            foreach (var component in prefab.GetComponentsInChildren<Component>(true)
                         .Where(component => component != null && IsReviewable(component)))
            {
                var path = AnimationUtility.CalculateTransformPath(component.transform, prefab.transform);
                var type = component.GetType();
                var typeName = type.FullName ?? type.Name;
                var currentIndex = Array.IndexOf(component.transform.GetComponents(type), component);
                var stableIndex = ResolveStableIndex(restoreCache, path, typeName, currentIndex, hiddenRemoved);
                var key = path + "|" + typeName + "|" + stableIndex;
                if (!previous.TryGetValue(key, out var choice)) choice = new MobileComponentRepairChoice();
                choice.ObjectPath = path;
                choice.ComponentTypeName = typeName;
                choice.ComponentIndex = stableIndex;
                choice.CurrentComponentIndex = currentIndex;
                choice.Category = Category(component);
                choice.DisplayName = (string.IsNullOrEmpty(path) ? prefab.name : path) + " - " + type.Name;
                choice.EstimatedAffectedTransforms = TypeName(component) == "VRCPhysBone"
                    ? EstimatePhysBoneTransforms(component)
                    : 0;
                choice.PresentInUploadPrefab = true;
                current.Add(choice);
            }

            current.AddRange(hiddenRemoved);
            recipe.MobileComponentRepairChoices.Clear();
            recipe.MobileComponentRepairChoices.AddRange(current.OrderBy(choice => choice.Category,
                    StringComparer.Ordinal)
                .ThenBy(choice => choice.ObjectPath, StringComparer.Ordinal)
                .ThenBy(choice => choice.ComponentTypeName, StringComparer.Ordinal)
                .ThenBy(choice => choice.ComponentIndex));
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
        }

        public static void RefreshRestoreCacheFromBuildRoot(GameObject root, MobileAvatarMeshRecipe recipe)
        {
            if (root == null || recipe == null) return;
            SaveRestoreCache(root, recipe, true);
        }

        public static int ApplyMarkedRepairsToUploadPrefab(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            var root = PrefabUtility.LoadPrefabContents(recipe.CombinedQuestPrefabPath);
            if (root == null) throw new InvalidOperationException("The clean mobile upload prefab is missing.");
            var removed = 0;
            try
            {
                EnsureRestoreCache(root, recipe);
                var marked = recipe.MobileComponentRepairChoices.Where(choice => choice.PresentInUploadPrefab &&
                    choice.RemoveFromMobile).ToArray();
                removed = ApplyMarkedRepairs(root, recipe, true);
                MobileAvatarStudioBuildMarkerUtility.EnsureMarker(root, recipe);
                if (PrefabUtility.SaveAsPrefabAsset(root, recipe.CombinedQuestPrefabPath) == null)
                    throw new InvalidOperationException("Unity failed to save the repaired mobile upload prefab.");
                var removedUtc = DateTime.UtcNow.ToString("O");
                foreach (var choice in marked)
                {
                    choice.PresentInUploadPrefab = false;
                    choice.RemovedUtc = removedUtc;
                }
                recipe.MobileResolvedAuditPassed = false;
                recipe.MobileValidationStatus = "Mobile component repairs applied; rerun Stage 6 save and Stage 7 validation";
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            Refresh(recipe);
            return removed;
        }

        public static int ApplyMarkedRepairs(GameObject root, MobileAvatarMeshRecipe recipe,
            bool useCurrentIndices = false)
        {
            if (root == null || recipe == null) return 0;
            var removed = 0;
            foreach (var choice in recipe.MobileComponentRepairChoices
                         .Where(choice => choice.RemoveFromMobile &&
                                          (!useCurrentIndices || choice.PresentInUploadPrefab))
                         .OrderBy(choice => choice.ObjectPath, StringComparer.Ordinal)
                         .ThenBy(choice => choice.ComponentTypeName, StringComparer.Ordinal)
                         .ThenByDescending(choice => useCurrentIndices
                             ? choice.CurrentComponentIndex
                             : choice.ComponentIndex))
            {
                var transform = FindTransform(root, choice.ObjectPath);
                if (transform == null) continue;
                var matches = ComponentsOfType(transform, choice.ComponentTypeName);
                var index = useCurrentIndices ? choice.CurrentComponentIndex : choice.ComponentIndex;
                if (index < 0 || index >= matches.Length) continue;
                var component = matches[index];
                if (component is ParticleSystem)
                {
                    var renderer = transform.GetComponent<ParticleSystemRenderer>();
                    if (renderer != null) UnityEngine.Object.DestroyImmediate(renderer);
                }
                UnityEngine.Object.DestroyImmediate(component);
                removed++;
            }
            return removed;
        }

        public static int RestoreToUploadPrefab(MobileAvatarMeshRecipe recipe,
            MobileComponentRepairChoice choice)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            if (choice == null) throw new ArgumentNullException(nameof(choice));
            if (choice.PresentInUploadPrefab || !choice.RemoveFromMobile) return 0;

            EnsureRestoreCache(null, recipe);
            var source = PrefabUtility.LoadPrefabContents(recipe.MobileComponentRestoreCachePath);
            var target = PrefabUtility.LoadPrefabContents(recipe.CombinedQuestPrefabPath);
            if (source == null || target == null)
            {
                if (source != null) PrefabUtility.UnloadPrefabContents(source);
                if (target != null) PrefabUtility.UnloadPrefabContents(target);
                throw new InvalidOperationException("The component restore cache or mobile prefab could not be opened.");
            }

            try
            {
                var sourceTransform = FindTransform(source, choice.ObjectPath);
                var targetTransform = FindTransform(target, choice.ObjectPath);
                if (sourceTransform == null || targetTransform == null)
                    throw new InvalidOperationException("The original component object path no longer exists: " +
                                                        choice.ObjectPath);
                var sourceMatches = ComponentsOfType(sourceTransform, choice.ComponentTypeName);
                if (choice.ComponentIndex < 0 || choice.ComponentIndex >= sourceMatches.Length)
                    throw new InvalidOperationException("The restore cache does not contain the original component: " +
                                                        choice.DisplayName);

                var sourceComponent = sourceMatches[choice.ComponentIndex];
                var restored = targetTransform.gameObject.AddComponent(sourceComponent.GetType());
                EditorUtility.CopySerialized(sourceComponent, restored);
                var desiredIndex = DesiredCurrentIndex(recipe, choice);
                MoveToTypeIndex(restored, desiredIndex);
                RemapHierarchyReferences(restored, source.transform, target.transform);

                if (sourceComponent is ParticleSystem)
                {
                    var sourceRenderer = sourceTransform.GetComponent<ParticleSystemRenderer>();
                    var targetRenderer = targetTransform.GetComponent<ParticleSystemRenderer>();
                    if (sourceRenderer != null && targetRenderer != null)
                    {
                        EditorUtility.CopySerialized(sourceRenderer, targetRenderer);
                        RemapHierarchyReferences(targetRenderer, source.transform, target.transform);
                    }
                }

                MobileAvatarStudioBuildMarkerUtility.EnsureMarker(target, recipe);
                if (PrefabUtility.SaveAsPrefabAsset(target, recipe.CombinedQuestPrefabPath) == null)
                    throw new InvalidOperationException("Unity failed to save the restored mobile component.");
                choice.RemoveFromMobile = false;
                choice.PresentInUploadPrefab = true;
                choice.CurrentComponentIndex = desiredIndex;
                choice.RemovedUtc = string.Empty;
                recipe.MobileResolvedAuditPassed = false;
                recipe.MobileValidationStatus = "Mobile component restored; rerun Stage 6 save and Stage 7 validation";
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(source);
                PrefabUtility.UnloadPrefabContents(target);
            }
            Refresh(recipe);
            return 1;
        }

        public static void SelectInUploadPrefab(MobileAvatarMeshRecipe recipe,
            MobileComponentRepairChoice choice)
        {
            var prefab = recipe != null
                ? AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath)
                : null;
            if (prefab == null || choice == null) return;
            AssetDatabase.OpenAsset(prefab);
            EditorApplication.delayCall += () =>
            {
                var transform = FindTransform(prefab, choice.ObjectPath);
                if (transform == null)
                {
                    Selection.activeObject = prefab;
                    EditorGUIUtility.PingObject(prefab);
                    return;
                }
                var component = ComponentsOfType(transform, choice.ComponentTypeName)
                    .ElementAtOrDefault(choice.CurrentComponentIndex);
                Selection.activeObject = component != null ? (UnityEngine.Object)component : transform.gameObject;
                EditorGUIUtility.PingObject(Selection.activeObject);
            };
        }

        private static void EnsureRestoreCache(GameObject currentRoot, MobileAvatarMeshRecipe recipe)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(recipe.MobileComponentRestoreCachePath) != null) return;
            var hasPreviouslyRemoved = recipe.MobileComponentRepairChoices.Any(choice =>
                choice.RemoveFromMobile && !choice.PresentInUploadPrefab);
            var source = hasPreviouslyRemoved && recipe.SourcePrefab != null ? recipe.SourcePrefab : currentRoot;
            if (source == null) source = recipe.SourcePrefab;
            if (source == null)
                throw new InvalidOperationException("No source is available to create the component restore cache.");
            SaveRestoreCache(source, recipe, true);
        }

        private static void SaveRestoreCache(GameObject source, MobileAvatarMeshRecipe recipe, bool overwrite)
        {
            var cacheRoot = recipe.OutputRoot + "/Checkpoints/ComponentRepair";
            MeshAnalysisUtility.EnsureAssetFolder(cacheRoot);
            var cachePath = cacheRoot + "/MobileComponentRestoreCache.prefab";
            if (!overwrite && AssetDatabase.LoadAssetAtPath<GameObject>(cachePath) != null) return;
            var copy = UnityEngine.Object.Instantiate(source);
            copy.name = "MobileComponentRestoreCache";
            try
            {
                if (PrefabUtility.SaveAsPrefabAsset(copy, cachePath) == null)
                    throw new InvalidOperationException("Unity failed to save the mobile component restore cache.");
                recipe.MobileComponentRestoreCachePath = cachePath;
                recipe.MobileComponentRestoreCacheUtc = DateTime.UtcNow.ToString("O");
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        private static int ResolveStableIndex(GameObject cache, string path, string typeName, int currentIndex,
            IReadOnlyCollection<MobileComponentRepairChoice> hiddenRemoved)
        {
            if (cache == null) return currentIndex;
            var transform = FindTransform(cache, path);
            if (transform == null) return currentIndex;
            var cachedCount = ComponentsOfType(transform, typeName).Length;
            var removed = new HashSet<int>(hiddenRemoved.Where(choice =>
                    string.Equals(choice.ObjectPath, path, StringComparison.Ordinal) &&
                    string.Equals(choice.ComponentTypeName, typeName, StringComparison.Ordinal))
                .Select(choice => choice.ComponentIndex));
            var available = Enumerable.Range(0, cachedCount).Where(index => !removed.Contains(index)).ToArray();
            return currentIndex >= 0 && currentIndex < available.Length ? available[currentIndex] : currentIndex;
        }

        private static int DesiredCurrentIndex(MobileAvatarMeshRecipe recipe, MobileComponentRepairChoice restored)
        {
            var removedBefore = recipe.MobileComponentRepairChoices.Count(choice => choice != restored &&
                choice.RemoveFromMobile && !choice.PresentInUploadPrefab &&
                string.Equals(choice.ObjectPath, restored.ObjectPath, StringComparison.Ordinal) &&
                string.Equals(choice.ComponentTypeName, restored.ComponentTypeName, StringComparison.Ordinal) &&
                choice.ComponentIndex < restored.ComponentIndex);
            return Mathf.Max(0, restored.ComponentIndex - removedBefore);
        }

        private static void MoveToTypeIndex(Component component, int desiredIndex)
        {
            var guard = 0;
            while (Array.IndexOf(component.transform.GetComponents(component.GetType()), component) > desiredIndex &&
                   guard++ < 256)
                if (!ComponentUtility.MoveComponentUp(component)) break;
        }

        private static void RemapHierarchyReferences(Component component, Transform sourceRoot, Transform targetRoot)
        {
            var serialized = new SerializedObject(component);
            var iterator = serialized.GetIterator();
            while (iterator.Next(true))
            {
                if (iterator.propertyType != SerializedPropertyType.ObjectReference ||
                    iterator.objectReferenceValue == null)
                    continue;
                if (TryRemapHierarchyReference(iterator.objectReferenceValue, sourceRoot, targetRoot,
                        out var remapped))
                    iterator.objectReferenceValue = remapped;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static bool TryRemapHierarchyReference(UnityEngine.Object reference, Transform sourceRoot,
            Transform targetRoot, out UnityEngine.Object remapped)
        {
            remapped = reference;
            Transform sourceTransform = null;
            if (reference is GameObject gameObject) sourceTransform = gameObject.transform;
            else if (reference is Component component) sourceTransform = component.transform;
            if (sourceTransform == null ||
                !(sourceTransform == sourceRoot || sourceTransform.IsChildOf(sourceRoot)))
                return false;
            var path = AnimationUtility.CalculateTransformPath(sourceTransform, sourceRoot);
            var targetTransform = FindTransform(targetRoot.gameObject, path);
            if (targetTransform == null)
            {
                remapped = null;
                return true;
            }
            if (reference is GameObject)
            {
                remapped = targetTransform.gameObject;
                return true;
            }
            if (reference is Transform)
            {
                remapped = targetTransform;
                return true;
            }
            if (reference is Component sourceComponent)
            {
                var index = Array.IndexOf(sourceTransform.GetComponents(sourceComponent.GetType()), sourceComponent);
                remapped = targetTransform.GetComponents(sourceComponent.GetType()).ElementAtOrDefault(index);
                return true;
            }
            return false;
        }

        private static Transform FindTransform(GameObject root, string path) =>
            string.IsNullOrEmpty(path) ? root.transform : root.transform.Find(path);

        private static Component[] ComponentsOfType(Transform transform, string typeName) =>
            transform.GetComponents<Component>().Where(component => component != null &&
                string.Equals(component.GetType().FullName ?? component.GetType().Name,
                    typeName, StringComparison.Ordinal)).ToArray();

        private static bool IsReviewable(Component component)
        {
            if (component is ParticleSystem || component is AudioSource || component is Camera ||
                component is Light || component is Cloth || component is Rigidbody || component is Collider ||
                component is Joint || component is IConstraint) return true;
            var type = TypeName(component);
            return type == "VRCPhysBone" || type == "VRCPhysBoneCollider" ||
                   type.IndexOf("VRCContact", StringComparison.Ordinal) >= 0;
        }

        private static string Category(Component component)
        {
            if (component is ParticleSystem) return "Particles";
            var type = TypeName(component);
            if (type == "VRCPhysBone" || type == "VRCPhysBoneCollider") return "PhysBones";
            if (type.IndexOf("VRCContact", StringComparison.Ordinal) >= 0) return "Contacts";
            return "Unsupported components";
        }

        private static string TypeName(Component component) => component.GetType().Name ?? string.Empty;

        private static int EstimatePhysBoneTransforms(Component component)
        {
            try
            {
                var serialized = new SerializedObject(component);
                var rootProperty = serialized.FindProperty("rootTransform");
                var root = rootProperty?.objectReferenceValue as Transform;
                if (root == null) root = component.transform;
                var count = root.GetComponentsInChildren<Transform>(true).Length;
                var ignored = serialized.FindProperty("ignoreTransforms");
                if (ignored != null && ignored.isArray)
                    for (var index = 0; index < ignored.arraySize; index++)
                        if (ignored.GetArrayElementAtIndex(index).objectReferenceValue is Transform ignoredRoot)
                            count -= ignoredRoot.GetComponentsInChildren<Transform>(true).Length;
                return Mathf.Max(0, count);
            }
            catch
            {
                return 0;
            }
        }
    }
}
