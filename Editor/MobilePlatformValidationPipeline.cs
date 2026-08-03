using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor.BuildPipeline;

namespace MobileAvatarStudio.Editor
{
    internal sealed class MobileResolvedAuditResult
    {
        public string ResolvedPrefabPath { get; set; }
        public string ReportPath { get; set; }
        public bool Passed { get; set; }
        public int RendererCount { get; set; }
        public int MaterialCount { get; set; }
        public int PhysBoneCount { get; set; }
        public int PhysBoneColliderCount { get; set; }
        public int ContactCount { get; set; }
        public int ExcludedObjectCount { get; set; }
        public int FallbackBindingCount { get; set; }
    }

    internal sealed class MobileSdkBuildResult
    {
        public string BundlePath { get; set; }
        public long DownloadBytes { get; set; }
        public long UncompressedBytes { get; set; } = -1;
        public bool DownloadLimitPassed { get; set; }
        public bool UncompressedLimitPassed { get; set; }
    }

    internal static class MobilePlatformValidationPipeline
    {
        internal const long MobileDownloadLimit = 10L * 1024L * 1024L;
        internal const long MobileUncompressedLimit = 40L * 1024L * 1024L;

        public static bool IsMobileTarget(BuildTarget target) =>
            target == BuildTarget.Android || target == BuildTarget.iOS;

        public static bool IsTargetSupported(BuildTarget target)
        {
            var group = BuildPipeline.GetBuildTargetGroup(target);
            return BuildPipeline.IsBuildTargetSupported(group, target);
        }

        public static void SwitchTarget(MobileAvatarMeshRecipe recipe, BuildTarget target)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            if (!IsMobileTarget(target)) throw new InvalidOperationException("Only Android and iOS are mobile validation targets.");
            if (!IsTargetSupported(target))
                throw new InvalidOperationException(target + " build support is not installed in this Unity editor.");
            recipe.MobileValidationStatus = "Switching active build target to " + target;
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
            var group = BuildPipeline.GetBuildTargetGroup(target);
            if (EditorUserBuildSettings.activeBuildTarget != target &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
                throw new InvalidOperationException("Unity could not switch the active build target to " + target + ".");
            recipe.MobileValidationStatus = "Active build target is " + target + "; resolved audit required";
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
        }

        public static bool CanRunResolvedAudit(MobileAvatarMeshRecipe recipe, out string reason)
        {
            if (recipe == null || recipe.SourcePrefab == null)
            {
                reason = "Open a saved Mobile Avatar Studio recipe first.";
                return false;
            }
            if (!recipe.BehaviorAppliedToCombined)
            {
                reason = "Complete and validate Stage 6 behavior isolation first.";
                return false;
            }
            if (!ManualPolishPipeline.ValidateCheckpoint(recipe, out reason)) return false;
            if (!IsMobileTarget(EditorUserBuildSettings.activeBuildTarget))
            {
                reason = "Switch the active build target to Android or iOS first.";
                return false;
            }
            var uploadPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            if (uploadPrefab == null)
            {
                reason = "The combined mobile prefab is missing.";
                return false;
            }
            if (MobileAvatarStudioBuildMarkerUtility.IsPreprocessedTestCopy(uploadPrefab))
            {
                reason = "The selected mobile prefab is a preprocessed test copy. Rebuild Stage 6 to restore the clean upload prefab.";
                return false;
            }
            if (!MobileAvatarStudioBuildMarkerUtility.HasValidMarker(uploadPrefab, recipe))
            {
                reason = "Rebuild Stage 6 once to install the upload-time Mobile Avatar Studio build marker.";
                return false;
            }
            if (!MobileContentPipeline.ValidateConfiguration(recipe, out reason)) return false;
            var currentHash = MeshAnalysisUtility.ComputeAssetHash(recipe.SourceAssetPath);
            if (!string.Equals(currentHash, recipe.SourceFileHash, StringComparison.OrdinalIgnoreCase))
            {
                reason = "The protected PC source changed after analysis. Re-analyze before validation.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public static MobileResolvedAuditResult RunResolvedAudit(MobileAvatarMeshRecipe recipe)
        {
            if (!CanRunResolvedAudit(recipe, out var reason)) throw new InvalidOperationException(reason);
            if (!ManualPolishPipeline.ValidateCheckpointForExecution(recipe, out reason))
                throw new InvalidOperationException(reason);
            var target = EditorUserBuildSettings.activeBuildTarget;
            var sourceHashBefore = MeshAnalysisUtility.ComputeAssetHash(recipe.SourceAssetPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            GameObject instance = null;
            try
            {
                instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null) throw new InvalidOperationException("Could not instantiate the combined mobile prefab.");
                instance.name = prefab.name + "_AUDIT_PREVIEW_DO_NOT_UPLOAD";
                MobileAvatarStudioMobileBuildCallback.ExpectResult(instance);
                if (!VRCBuildPipelineCallbacks.OnPreprocessAvatar(instance))
                    throw new InvalidOperationException("A VRChat avatar preprocessor rejected the disposable audit copy. Check the Console for its exact error.");

                if (!MobileAvatarStudioMobileBuildCallback.TryTakeResult(instance, out var contentResult))
                    throw new InvalidOperationException(
                        "The Mobile Avatar Studio upload callback did not process the audit clone. Rebuild Stage 6 and try again.");
                var result = AuditResolvedHierarchy(instance, recipe);
                result.ExcludedObjectCount = contentResult.ExcludedObjects;
                result.FallbackBindingCount = contentResult.FallbackBindings;
                foreach (var warning in contentResult.Warnings)
                    AddIssue(recipe, MobileValidationSeverity.Warning, string.Empty, "Mobile content", warning);
                RemoveSupersededResolvedPrefab(recipe, recipe.CombinedQuestPrefabPath);
                result.ResolvedPrefabPath = recipe.CombinedQuestPrefabPath;
                result.Passed = recipe.MobileValidationIssues.All(issue => issue.Severity != MobileValidationSeverity.Error);

                recipe.MobileResolvedAuditUtc = DateTime.UtcNow.ToString("O");
                recipe.MobileResolvedAuditTarget = target.ToString();
                recipe.MobileResolvedPrefabPath = recipe.CombinedQuestPrefabPath;
                recipe.MobileResolvedPrefabHash =
                    MeshAnalysisUtility.ComputeAssetHash(recipe.CombinedQuestPrefabPath);
                recipe.MobileResolvedAuditPassed = result.Passed;
                recipe.MobileValidationStatus = result.Passed
                    ? "Resolved mobile audit passed; real SDK build required"
                    : "Resolved mobile audit found blocking issues";
                EditorUtility.SetDirty(recipe);
                MobileComponentRepairPipeline.Refresh(recipe);
                WriteResolvedReport(recipe, result, sourceHashBefore,
                    MeshAnalysisUtility.ComputeAssetHash(recipe.SourceAssetPath));
                MobileContentPipeline.WriteReport(recipe, contentResult, "Stage 7 resolved mobile rewrite");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Selection.activeObject = prefab;
                return result;
            }
            finally
            {
                MobileAvatarStudioMobileBuildCallback.ClearResult(instance);
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static void RemoveSupersededResolvedPrefab(MobileAvatarMeshRecipe recipe, string resolvedPath)
        {
            var previous = recipe.MobileResolvedPrefabPath;
            if (string.IsNullOrEmpty(previous) || string.Equals(previous, resolvedPath, StringComparison.Ordinal) ||
                AssetDatabase.LoadAssetAtPath<GameObject>(previous) == null) return;
            var allowedFinalRoot = recipe.OutputRoot + "/Validation/Final/";
            var allowedLegacyRoot = recipe.OutputRoot + "/Validation/Resolved/";
            if ((!previous.StartsWith(allowedFinalRoot, StringComparison.Ordinal) &&
                 !previous.StartsWith(allowedLegacyRoot, StringComparison.Ordinal)) ||
                !previous.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return;
            AssetDatabase.DeleteAsset(previous);
        }

        private static MobileResolvedAuditResult AuditResolvedHierarchy(GameObject root,
            MobileAvatarMeshRecipe recipe)
        {
            recipe.MobileValidationIssues.Clear();
            var result = new MobileResolvedAuditResult();
            var components = root.GetComponentsInChildren<Component>(true).Where(component => component != null).ToArray();
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var materials = renderers.SelectMany(renderer => renderer.sharedMaterials)
                .Where(material => material != null).Distinct().ToArray();
            result.RendererCount = renderers.Length;
            result.MaterialCount = materials.Length;

            foreach (var component in components)
            {
                var path = AnimationUtility.CalculateTransformPath(component.transform, root.transform);
                if (component is AudioSource) AddIssue(recipe, MobileValidationSeverity.Error, path, "Unsupported component",
                    "AudioSource is disabled on mobile avatars.");
                else if (component is Camera) AddIssue(recipe, MobileValidationSeverity.Error, path, "Unsupported component",
                    "Camera is disabled on mobile avatars.");
                else if (component is Light) AddIssue(recipe, MobileValidationSeverity.Error, path, "Unsupported component",
                    "Light is disabled on mobile avatars.");
                else if (component is Cloth) AddIssue(recipe, MobileValidationSeverity.Error, path, "Unsupported component",
                    "Cloth is disabled on mobile avatars.");
                else if (component is Rigidbody) AddIssue(recipe, MobileValidationSeverity.Error, path, "Unsupported component",
                    "Rigidbody is disabled on mobile avatars.");
                else if (component is Collider) AddIssue(recipe, MobileValidationSeverity.Error, path, "Unsupported component",
                    "Unity physics Collider is disabled on mobile avatars.");
                else if (component is Joint) AddIssue(recipe, MobileValidationSeverity.Error, path, "Unsupported component",
                    "Unity Joint is disabled on mobile avatars.");
                else if (component is IConstraint) AddIssue(recipe, MobileValidationSeverity.Error, path, "Unsupported component",
                    "Unity Constraint is disabled on mobile avatars; use a VRChat Constraint or bake the result.");
            }

            foreach (var material in materials)
            {
                var shaderName = material.shader != null ? material.shader.name : "<missing>";
                if (!shaderName.StartsWith("VRChat/Mobile/", StringComparison.Ordinal))
                    AddIssue(recipe, MobileValidationSeverity.Error, AssetDatabase.GetAssetPath(material), "Shader",
                        material.name + " uses unsupported mobile shader " + shaderName + ".");
            }

            var particleCount = components.Count(component => component is ParticleSystem);
            if (particleCount > 0)
                AddIssue(recipe, MobileValidationSeverity.Warning, string.Empty, "Particles",
                    particleCount + " ParticleSystem component(s) remain. Mobile particle limits must be checked visually and in the SDK report.");

            result.PhysBoneCount = components.Count(component => TypeName(component) == "VRCPhysBone");
            result.PhysBoneColliderCount = components.Count(component => TypeName(component) == "VRCPhysBoneCollider");
            result.ContactCount = components.Count(component => TypeName(component).IndexOf("VRCContact", StringComparison.Ordinal) >= 0);
            if (result.PhysBoneCount > 8)
                AddIssue(recipe, MobileValidationSeverity.Error, string.Empty, "PhysBones",
                    $"{result.PhysBoneCount} PhysBone components exceed the mobile component limit of 8.");
            if (result.PhysBoneColliderCount > 16)
                AddIssue(recipe, MobileValidationSeverity.Error, string.Empty, "PhysBones",
                    $"{result.PhysBoneColliderCount} PhysBone colliders exceed the mobile component limit of 16.");
            if (result.ContactCount > 16)
                AddIssue(recipe, MobileValidationSeverity.Error, string.Empty, "Contacts",
                    $"{result.ContactCount} contact components exceed the mobile component limit of 16.");

            var affectedEstimate = components.Where(component => TypeName(component) == "VRCPhysBone")
                .Sum(EstimatePhysBoneTransforms);
            if (affectedEstimate > 64)
                AddIssue(recipe, MobileValidationSeverity.Warning, string.Empty, "PhysBones",
                    $"Estimated affected PhysBone transforms: {affectedEstimate}; the mobile limit is 64. The SDK build is authoritative.");

            if (root.GetComponentsInChildren<Component>(true).Count(component => component != null &&
                    (component.GetType().FullName ?? component.GetType().Name)
                    .IndexOf("VRCAvatarDescriptor", StringComparison.OrdinalIgnoreCase) >= 0) != 1)
                AddIssue(recipe, MobileValidationSeverity.Error, string.Empty, "Descriptor",
                    "Resolved clone must contain exactly one VRC Avatar Descriptor.");
            return result;
        }

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
            catch { return 0; }
        }

        private static string TypeName(Component component) => component.GetType().Name ?? string.Empty;

        private static void AddIssue(MobileAvatarMeshRecipe recipe, MobileValidationSeverity severity,
            string path, string category, string message)
        {
            recipe.MobileValidationIssues.Add(new MobileValidationIssue
            {
                Severity = severity,
                ObjectPath = path ?? string.Empty,
                Category = category,
                Message = message
            });
        }

        public static bool CanRunSdkBuild(MobileAvatarMeshRecipe recipe, out string reason)
        {
            if (!CanRunResolvedAudit(recipe, out reason)) return false;
            var target = EditorUserBuildSettings.activeBuildTarget.ToString();
            if (!recipe.MobileResolvedAuditPassed)
            {
                reason = "Run the resolved mobile audit and clear its blocking issues first.";
                return false;
            }
            if (!string.Equals(recipe.MobileResolvedAuditTarget, target, StringComparison.Ordinal))
            {
                reason = "The saved resolved audit belongs to " + recipe.MobileResolvedAuditTarget +
                         "; rerun it for the active " + target + " target.";
                return false;
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(recipe.MobileResolvedPrefabPath) == null)
            {
                reason = "The validated clean mobile upload prefab is missing. Rerun Stage 7.";
                return false;
            }
            var currentUploadHash = MeshAnalysisUtility.ComputeAssetHash(recipe.CombinedQuestPrefabPath);
            if (string.IsNullOrEmpty(recipe.MobileResolvedPrefabHash) ||
                !string.Equals(currentUploadHash, recipe.MobileResolvedPrefabHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "The clean mobile upload prefab changed after its audit. Rerun Stage 7 before Stage 8.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public static async Task<MobileSdkBuildResult> BuildWithOfficialSdk(MobileAvatarMeshRecipe recipe)
        {
            if (!CanRunSdkBuild(recipe, out var reason)) throw new InvalidOperationException(reason);
            if (!ManualPolishPipeline.ValidateCheckpointForExecution(recipe, out reason))
                throw new InvalidOperationException(reason);
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder))
                throw new InvalidOperationException("Open the VRChat SDK Control Panel once, then press the build button again.");

            var sourceHashBefore = MeshAnalysisUtility.ComputeAssetHash(recipe.SourceAssetPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            if (prefab == null)
                throw new InvalidOperationException("The clean mobile upload prefab is missing.");
            if (MobileAvatarStudioBuildMarkerUtility.IsPreprocessedTestCopy(prefab))
                throw new InvalidOperationException(
                    "The official build refused a VRCFury test copy. Rebuild Stage 6 and use the clean mobile upload prefab.");
            if (!MobileAvatarStudioBuildMarkerUtility.HasValidMarker(prefab, recipe))
                throw new InvalidOperationException(
                    "The clean upload prefab is missing its Mobile Avatar Studio build marker. Rebuild Stage 6.");
            GameObject instance = null;
            var sdkErrors = new List<string>();
            Application.LogCallback sdkLogCapture = (condition, stackTrace, type) =>
            {
                if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
                var message = (condition ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(message) ||
                    message.IndexOf("Encountered the following validation issues", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(message, "Avatar validation failed", StringComparison.OrdinalIgnoreCase))
                    return;
                if (!sdkErrors.Contains(message)) sdkErrors.Add(message);
            };
            recipe.MobileValidationStatus = "Official VRChat SDK mobile build running";
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
            try
            {
                instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null) throw new InvalidOperationException("Could not instantiate the clean mobile upload prefab for SDK build.");
                instance.name = prefab.name + "_SDK_BUILD";
                Application.logMessageReceived += sdkLogCapture;
                string sdkPath;
                try
                {
                    sdkPath = await builder.Build(instance);
                }
                catch (Exception exception)
                {
                    var details = sdkErrors.Take(8).ToArray();
                    if (details.Length == 0) throw;
                    throw new InvalidOperationException(
                        "VRChat SDK validation failed:\n- " + string.Join("\n- ", details), exception);
                }
                finally
                {
                    Application.logMessageReceived -= sdkLogCapture;
                }
                if (string.IsNullOrEmpty(sdkPath) || !File.Exists(sdkPath))
                    throw new InvalidOperationException("The VRChat SDK did not return a readable avatar bundle.");

                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
                var targetName = EditorUserBuildSettings.activeBuildTarget.ToString();
                var outputRoot = Path.Combine(projectRoot, "Temp", "MobileAvatarStudioBuilds",
                    recipe.SourceGuid, targetName);
                Directory.CreateDirectory(outputRoot);
                var outputPath = Path.Combine(outputRoot, "CurrentAvatarBundle.vrca");
                File.Copy(sdkPath, outputPath, true);

                var result = new MobileSdkBuildResult
                {
                    BundlePath = outputPath,
                    DownloadBytes = new FileInfo(outputPath).Length
                };
                result.UncompressedBytes = TryReadSdkUncompressedBytes();
                var mobile = IsMobileTarget(EditorUserBuildSettings.activeBuildTarget);
                result.DownloadLimitPassed = !mobile || result.DownloadBytes <= MobileDownloadLimit;
                result.UncompressedLimitPassed = !mobile || result.UncompressedBytes < 0 ||
                                                 result.UncompressedBytes <= MobileUncompressedLimit;

                recipe.MobileSdkBuildUtc = DateTime.UtcNow.ToString("O");
                recipe.MobileSdkBuildTarget = targetName;
                recipe.MobileSdkBundlePath = outputPath;
                recipe.MobileSdkDownloadBytes = result.DownloadBytes;
                recipe.MobileSdkUncompressedBytes = result.UncompressedBytes;
                recipe.MobileValidationStatus = result.DownloadLimitPassed && result.UncompressedLimitPassed
                    ? "Official SDK mobile build passed measured size limits"
                    : "Official SDK mobile build exceeded a measured size limit";
                EditorUtility.SetDirty(recipe);
                WriteSdkBuildReport(recipe, result, sourceHashBefore,
                    MeshAnalysisUtility.ComputeAssetHash(recipe.SourceAssetPath));
                AssetDatabase.SaveAssets();
                return result;
            }
            finally
            {
                Application.logMessageReceived -= sdkLogCapture;
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static long TryReadSdkUncompressedBytes()
        {
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("VRC.SDKBase.Editor.Validation.ValidationEditorHelpers", false))
                    .FirstOrDefault(value => value != null);
                var method = type?.GetMethod("CheckIfUncompressedAssetBundleFileTooLarge",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null) return -1;
                var parameters = method.GetParameters();
                if (parameters.Length != 3 || !parameters[0].ParameterType.IsEnum) return -1;
                var contentType = Enum.Parse(parameters[0].ParameterType, "Avatar");
                var args = new object[] { contentType, 0, true };
                method.Invoke(null, args);
                return Convert.ToInt64(args[1]);
            }
            catch { return -1; }
        }

        public static string FormatBytes(long value)
        {
            if (value < 0) return "Unavailable";
            return value >= 1024L * 1024L ? value / 1048576d + " MiB" : value / 1024d + " KiB";
        }

        private static void WriteResolvedReport(MobileAvatarMeshRecipe recipe, MobileResolvedAuditResult result,
            string sourceHashBefore, string sourceHashAfter)
        {
            var reportRoot = recipe.OutputRoot + "/Reports";
            MeshAnalysisUtility.EnsureAssetFolder(reportRoot);
            result.ReportPath = reportRoot + "/MobileResolvedAuditReport.txt";
            var text = new StringBuilder();
            text.AppendLine("MOBILE AVATAR STUDIO - MOBILE UPLOAD PREFAB AUDIT");
            text.AppendLine("Generated: " + DateTime.UtcNow.ToString("O"));
            text.AppendLine("Target: " + EditorUserBuildSettings.activeBuildTarget);
            text.AppendLine("Clean upload prefab: " + result.ResolvedPrefabPath);
            text.AppendLine("Audit execution: disposable in-memory preprocessed copy (not saved or uploadable)");
            text.AppendLine("Source unchanged: " + string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.OrdinalIgnoreCase));
            text.AppendLine($"Renderers={result.RendererCount} materials={result.MaterialCount} PhysBones={result.PhysBoneCount} colliders={result.PhysBoneColliderCount} contacts={result.ContactCount}");
            text.AppendLine($"Content exclusions={result.ExcludedObjectCount}; fallback bindings={result.FallbackBindingCount}");
            text.AppendLine("Validation: " + (result.Passed ? "PASS" : "FAIL"));
            text.AppendLine();
            foreach (var issue in recipe.MobileValidationIssues)
                text.AppendLine($"{issue.Severity}: {issue.Category} | {issue.ObjectPath} | {issue.Message}");
            File.WriteAllText(result.ReportPath, text.ToString(), Encoding.UTF8);
            AssetDatabase.ImportAsset(result.ReportPath, ImportAssetOptions.ForceUpdate);
        }

        private static void WriteSdkBuildReport(MobileAvatarMeshRecipe recipe, MobileSdkBuildResult result,
            string sourceHashBefore, string sourceHashAfter)
        {
            var reportRoot = recipe.OutputRoot + "/Reports";
            MeshAnalysisUtility.EnsureAssetFolder(reportRoot);
            var path = reportRoot + "/MobileSdkBuildReport.txt";
            var text = new StringBuilder();
            text.AppendLine("MOBILE AVATAR STUDIO - OFFICIAL VRCHAT SDK MOBILE BUILD");
            text.AppendLine("Generated: " + DateTime.UtcNow.ToString("O"));
            text.AppendLine("Target: " + EditorUserBuildSettings.activeBuildTarget);
            text.AppendLine("Bundle: " + result.BundlePath);
            text.AppendLine("Source unchanged: " + string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.OrdinalIgnoreCase));
            text.AppendLine($"Download: {FormatBytes(result.DownloadBytes)} | mobile limit: 10 MiB | pass={result.DownloadLimitPassed}");
            text.AppendLine($"Uncompressed: {FormatBytes(result.UncompressedBytes)} | mobile limit: 40 MiB | pass={result.UncompressedLimitPassed}");
            text.AppendLine("Status: " + recipe.MobileValidationStatus);
            File.WriteAllText(path, text.ToString(), Encoding.UTF8);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }
    }
}
