using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace MobileAvatarStudio.Editor
{
    internal static class MobileAvatarStudioBuildMarkerUtility
    {
        public static void EnsureMarker(GameObject root, MobileAvatarMeshRecipe recipe)
        {
            if (root == null || recipe == null) throw new ArgumentNullException();
            var recipePath = AssetDatabase.GetAssetPath(recipe);
            var recipeGuid = AssetDatabase.AssetPathToGUID(recipePath);
            if (string.IsNullOrEmpty(recipeGuid))
                throw new InvalidOperationException("The Mobile Avatar Studio recipe must be saved before building.");
            var marker = root.GetComponent<MobileAvatarStudioBuildMarker>();
            if (marker == null) marker = root.AddComponent<MobileAvatarStudioBuildMarker>();
            marker.RecipeAssetGuid = recipeGuid;
            marker.hideFlags = HideFlags.HideInInspector;
            EditorUtility.SetDirty(marker);
        }

        public static bool HasValidMarker(GameObject root, MobileAvatarMeshRecipe recipe)
        {
            if (root == null || recipe == null) return false;
            var marker = root.GetComponent<MobileAvatarStudioBuildMarker>();
            if (marker == null) return false;
            var expected = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(recipe));
            return !string.IsNullOrEmpty(expected) &&
                   string.Equals(marker.RecipeAssetGuid, expected, StringComparison.Ordinal);
        }

        public static bool IsPreprocessedTestCopy(GameObject root)
        {
            if (root == null) return false;
            foreach (var component in root.GetComponents<Component>())
            {
                if (component == null) continue;
                var typeName = component.GetType().FullName ?? component.GetType().Name;
                if (string.Equals(typeName, "VF.Model.VRCFuryTest", StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    internal sealed class MobileAvatarStudioMobileBuildCallback : IVRCSDKPreprocessAvatarCallback
    {
        private static readonly Dictionary<int, MobileContentRewriteResult> Results =
            new Dictionary<int, MobileContentRewriteResult>();
        private static readonly HashSet<int> ExpectedResults = new HashSet<int>();

        // VRCFury's final parameter/controller processing is int.MaxValue - 100. Apply mobile
        // exclusions after that, but before the SDK's int.MaxValue editor-component cleanup.
        public int callbackOrder => int.MaxValue - 50;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            var marker = avatarGameObject != null
                ? avatarGameObject.GetComponent<MobileAvatarStudioBuildMarker>()
                : null;
            if (marker == null) return true;
            try
            {
                if (!MobilePlatformValidationPipeline.IsMobileTarget(EditorUserBuildSettings.activeBuildTarget))
                    throw new InvalidOperationException(
                        "This is a Mobile Avatar Studio upload prefab. Switch the project to Android or iOS before building it.");
                var recipePath = AssetDatabase.GUIDToAssetPath(marker.RecipeAssetGuid);
                var recipe = AssetDatabase.LoadAssetAtPath<MobileAvatarMeshRecipe>(recipePath);
                if (recipe == null)
                    throw new InvalidOperationException(
                        "The Mobile Avatar Studio recipe referenced by this upload prefab is missing.");
                if (!recipe.BehaviorAppliedToCombined)
                    throw new InvalidOperationException(
                        "Stage 6 behavior isolation is not current. Rebuild Stage 6 before building this mobile avatar.");
                if (!ManualPolishPipeline.ValidateCheckpointForExecution(recipe, out var checkpointReason))
                    throw new InvalidOperationException(checkpointReason);
                var sourceHash = MeshAnalysisUtility.ComputeAssetHash(recipe.SourceAssetPath);
                if (!string.Equals(sourceHash, recipe.SourceFileHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The protected PC source changed after analysis. Re-analyze before building the mobile avatar.");

                MobileComponentRepairPipeline.ApplyMarkedRepairs(avatarGameObject, recipe);
                var result = MobileContentPipeline.ApplyResolvedExclusions(avatarGameObject, recipe);
                var instanceId = avatarGameObject.GetInstanceID();
                if (ExpectedResults.Contains(instanceId)) Results[instanceId] = result;
                UnityEngine.Object.DestroyImmediate(marker);
                return true;
            }
            catch (Exception exception)
            {
                var instanceId = avatarGameObject != null ? avatarGameObject.GetInstanceID() : 0;
                Results.Remove(instanceId);
                ExpectedResults.Remove(instanceId);
                Debug.LogException(exception);
                return false;
            }
        }

        internal static bool TryTakeResult(GameObject root, out MobileContentRewriteResult result)
        {
            if (root != null && Results.TryGetValue(root.GetInstanceID(), out result))
            {
                Results.Remove(root.GetInstanceID());
                ExpectedResults.Remove(root.GetInstanceID());
                return true;
            }
            result = null;
            return false;
        }

        internal static void ExpectResult(GameObject root)
        {
            if (root != null) ExpectedResults.Add(root.GetInstanceID());
        }

        internal static void ClearResult(GameObject root)
        {
            if (root == null) return;
            Results.Remove(root.GetInstanceID());
            ExpectedResults.Remove(root.GetInstanceID());
        }
    }
}
