using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal sealed class MobileContentRewriteResult
    {
        public int ExcludedObjects { get; set; }
        public int RewrittenClips { get; set; }
        public int RemovedBindings { get; set; }
        public int FallbackBindings { get; set; }
        public int RemovedMenuControls { get; set; }
        public int RemovedEmptySubmenus { get; set; }
        public HashSet<string> RedirectedRulePaths { get; } = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> PreexistingFallbackRulePaths { get; } = new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> FallbackEvidenceClipPaths { get; } =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        public List<string> Warnings { get; } = new List<string>();
    }

    internal static class MobileContentPipeline
    {
        private sealed class ActivationSelector
        {
            public RendererMeshChoice Rule;
            public string Parameter;
            public float Value;
        }

        private sealed class ParameterMobileUsage
        {
            public bool HasBlendTreeUse;
            public bool HasMobileEffect;
            public bool HasOtherUse;
        }

        private static readonly string[] ActiveProperties = { "m_IsActive", "m_Enabled" };

        public static void Analyze(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null || recipe.SourcePrefab == null) throw new ArgumentNullException(nameof(recipe));
            foreach (var choice in recipe.RendererChoices)
            {
                choice.MobileActivationBindingCount = 0;
                choice.MobileActivationClipPaths.Clear();
            }

            var dependencies = AssetDatabase.GetDependencies(recipe.SourceAssetPath, true);
            var clips = dependencies.SelectMany(AssetDatabase.LoadAllAssetsAtPath)
                .OfType<AnimationClip>().Distinct().ToArray();
            foreach (var clip in clips)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (!ActiveProperties.Contains(binding.propertyName, StringComparer.Ordinal)) continue;
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (!CurveCanEnable(curve)) continue;
                    foreach (var choice in recipe.RendererChoices.Where(choice =>
                                 PathIsSameOrChild(binding.path, choice.TransformPath)))
                    {
                        choice.MobileActivationBindingCount++;
                        var clipPath = AssetDatabase.GetAssetPath(clip);
                        var label = string.IsNullOrEmpty(clipPath) ? clip.name : clipPath + " :: " + clip.name;
                        if (!choice.MobileActivationClipPaths.Contains(label))
                            choice.MobileActivationClipPaths.Add(label);
                    }
                }
            }

            recipe.MobileContentAnalysisUtc = DateTime.UtcNow.ToString("O");
            recipe.MobileContentStatus = recipe.RendererChoices.Any(choice => choice.IsExcludedFromMobile)
                ? "Exclusion choices need mobile rebuild"
                : "Analyzed; no mobile exclusions selected";
            EditorUtility.SetDirty(recipe);
            if (AssetDatabase.Contains(recipe)) AssetDatabase.SaveAssets();
        }

        public static bool ValidateConfiguration(MobileAvatarMeshRecipe recipe, out string reason)
        {
            if (recipe == null || recipe.SourcePrefab == null)
            {
                reason = "Open a saved Mobile Avatar Studio recipe first.";
                return false;
            }

            var choicesByPath = recipe.RendererChoices
                .GroupBy(choice => choice.TransformPath, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var excluded = recipe.RendererChoices.Where(choice => choice.IsExcludedFromMobile).ToArray();
            foreach (var choice in excluded)
            {
                var retainedDescendant = recipe.RendererChoices.FirstOrDefault(other =>
                    !ReferenceEquals(other, choice) && !other.IsExcludedFromMobile &&
                    PathIsSameOrChild(other.TransformPath, choice.TransformPath));
                if (retainedDescendant != null)
                {
                    reason = choice.DisplayName + " contains retained renderer " + retainedDescendant.DisplayName +
                             ". Exclude the child renderers individually or retain the parent.";
                    return false;
                }
                if (!choice.RedirectsToFallback) continue;
                if (string.IsNullOrEmpty(choice.MobileFallbackTransformPath))
                {
                    reason = choice.DisplayName + " needs a retained mobile fallback.";
                    return false;
                }
                if (!choicesByPath.TryGetValue(choice.MobileFallbackTransformPath, out var fallback))
                {
                    reason = choice.DisplayName + " points to a fallback renderer that no longer exists.";
                    return false;
                }
                if (fallback.IsExcludedFromMobile)
                {
                    reason = choice.DisplayName + " points to an excluded fallback. Choose a retained renderer.";
                    return false;
                }
                if (PathIsSameOrChild(fallback.TransformPath, choice.TransformPath))
                {
                    reason = choice.DisplayName + " cannot redirect to an object inside its excluded hierarchy.";
                    return false;
                }
            }

            foreach (var left in excluded)
            foreach (var right in excluded)
            {
                if (ReferenceEquals(left, right)) continue;
                if (!PathIsSameOrChild(right.TransformPath, left.TransformPath)) continue;
                reason = "Nested exclusions overlap: " + left.TransformPath + " and " + right.TransformPath +
                         ". Exclude only their common parent or only the individual children.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public static void InvalidateDownstream(MobileAvatarMeshRecipe recipe)
        {
            recipe.FinalAssemblyStatus = "Needs rebuild after mobile content change";
            recipe.BehaviorAppliedToCombined = false;
            recipe.BehaviorStatus = "Needs rebuild after mobile content change";
            recipe.MobileResolvedAuditPassed = false;
            recipe.MobileValidationStatus = "Needs rerun after mobile content change";
            recipe.MobileContentStatus = "Exclusion choices need mobile rebuild";
            foreach (var choice in recipe.RendererChoices) choice.MobileFallbackBehaviorClipPaths.Clear();
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
        }

        public static void ApplyAuthoringPayloadExclusions(GameObject root, MobileAvatarMeshRecipe recipe)
        {
            if (!ValidateConfiguration(recipe, out var reason)) throw new InvalidOperationException(reason);
            foreach (var choice in recipe.RendererChoices.Where(choice => choice.IsExcludedFromMobile))
            {
                var target = MeshAnalysisUtility.FindByPath(root.transform, choice.TransformPath);
                if (target == null)
                    throw new InvalidOperationException("The mobile exclusion path is missing: " + choice.TransformPath);
                ClearRendererPayload(target);
                if (choice.RedirectsToFallback && target.gameObject.activeSelf)
                {
                    var fallback = MeshAnalysisUtility.FindByPath(root.transform, choice.MobileFallbackTransformPath);
                    if (fallback == null)
                        throw new InvalidOperationException("The mobile fallback path is missing: " +
                                                            choice.MobileFallbackTransformPath);
                    fallback.gameObject.SetActive(true);
                }
            }
        }

        public static void ApplyPreviewPayloadExclusions(GameObject root, MobileAvatarMeshRecipe recipe)
        {
            foreach (var choice in recipe.RendererChoices.Where(choice => choice.IsExcludedFromMobile))
            {
                var target = MeshAnalysisUtility.FindByPath(root.transform, choice.TransformPath);
                if (target != null) ClearRendererPayload(target);
            }
        }

        public static MobileContentRewriteResult RewriteCopiedBehavior(
            IReadOnlyDictionary<UnityEngine.Object, UnityEngine.Object> objectMap,
            MobileAvatarMeshRecipe recipe)
        {
            var result = new MobileContentRewriteResult();
            var controllers = objectMap.Values.OfType<RuntimeAnimatorController>().Distinct().ToArray();
            CollectPreexistingFallbackEvidence(controllers, recipe, result);
            var selectors = DiscoverActivationSelectors(controllers, recipe);
            var ineffectiveParameters = DiscoverProvenMobileNoEffectParameters(controllers, recipe);
            result.RemovedMenuControls += PruneGeneratedMenus(objectMap.Values, selectors,
                ineffectiveParameters, recipe, out var emptySubmenus);
            result.RemovedEmptySubmenus += emptySubmenus;
            foreach (var clip in objectMap.Values.OfType<AnimationClip>().Distinct())
                RewriteClip(clip, recipe, result);
            PersistBehaviorFallbackEvidence(recipe, result);
            AssetDatabase.SaveAssets();
            return result;
        }

        public static MobileContentRewriteResult ApplyResolvedExclusions(GameObject root,
            MobileAvatarMeshRecipe recipe)
        {
            if (!ValidateConfiguration(recipe, out var reason)) throw new InvalidOperationException(reason);
            var result = new MobileContentRewriteResult();
            var controllers = EnumerateControllerReferences(root).Distinct().ToArray();
            foreach (var path in DiscoverPreexistingFallbackCoverage(controllers, recipe))
                result.PreexistingFallbackRulePaths.Add(path);
            CollectPreexistingFallbackEvidence(controllers, recipe, result);
            var selectors = DiscoverActivationSelectors(controllers, recipe);
            var ineffectiveParameters = DiscoverProvenMobileNoEffectParameters(controllers, recipe);
            result.RemovedMenuControls += PruneGeneratedMenus(EnumerateObjectReferences(root), selectors,
                ineffectiveParameters, recipe, out var emptySubmenus);
            result.RemovedEmptySubmenus += emptySubmenus;
            var clips = controllers.SelectMany(controller => controller.animationClips)
                .Where(clip => clip != null).Distinct().ToArray();
            foreach (var clip in clips)
            {
                if (!ClipRequiresMobileContentRewrite(clip, recipe)) continue;
                var path = AssetDatabase.GetAssetPath(clip).Replace('\\', '/');
                if (!CanModifyGeneratedBehaviorPath(path, recipe))
                    throw new InvalidOperationException("Mobile exclusion rewrite refused to modify source animation: " + path);
                RewriteClip(clip, recipe, result);
            }
            ApplyStoredBehaviorFallbackEvidence(clips, recipe, result);
            AssetDatabase.SaveAssets();

            var excluded = recipe.RendererChoices.Where(choice => choice.IsExcludedFromMobile)
                .OrderByDescending(choice => PathDepth(choice.TransformPath)).ToArray();
            foreach (var choice in excluded)
            {
                var target = MeshAnalysisUtility.FindByPath(root.transform, choice.TransformPath);
                if (target == null) continue;
                EnsureNoKeptRendererUsesExcludedBones(root, target, recipe);
                UnityEngine.Object.DestroyImmediate(target.gameObject);
                result.ExcludedObjects++;
            }

            foreach (var choice in excluded.Where(choice => choice.RedirectsToFallback))
            {
                var fallback = MeshAnalysisUtility.FindByPath(root.transform, choice.MobileFallbackTransformPath);
                if (fallback == null)
                    throw new InvalidOperationException("Resolved mobile fallback was removed: " +
                                                        choice.MobileFallbackTransformPath);
                if (!result.RedirectedRulePaths.Contains(choice.TransformPath) &&
                    !result.PreexistingFallbackRulePaths.Contains(choice.TransformPath))
                    throw new InvalidOperationException("The resolved mobile controller did not redirect " +
                                                        choice.TransformPath + " to its selected fallback. " +
                                                        "The build was stopped to prevent a bald/empty mobile state.");
            }
            return result;
        }

        public static void WriteReport(MobileAvatarMeshRecipe recipe, MobileContentRewriteResult result,
            string phase)
        {
            var reportRoot = recipe.OutputRoot + "/Reports";
            MeshAnalysisUtility.EnsureAssetFolder(reportRoot);
            var path = reportRoot + "/MobileContentReport.txt";
            var text = new StringBuilder();
            text.AppendLine("MOBILE AVATAR STUDIO - MOBILE CONTENT EXCLUSIONS");
            text.AppendLine("Generated: " + DateTime.UtcNow.ToString("O"));
            text.AppendLine("Phase: " + phase);
            text.AppendLine("Expression parameter policy: preserve ordering, types, defaults, and sync layout.");
            text.AppendLine($"Excluded objects: {recipe.RendererChoices.Count(choice => choice.IsExcludedFromMobile)}");
            text.AppendLine($"Rewritten clips: {result.RewrittenClips}; removed bindings: {result.RemovedBindings}; fallback bindings: {result.FallbackBindings}");
            text.AppendLine($"Fallback rules already safe in existing states: {result.PreexistingFallbackRulePaths.Count}");
            text.AppendLine($"Removed proven PC-only/no-effect mobile menu controls: {result.RemovedMenuControls}");
            text.AppendLine($"Removed empty mobile submenus: {result.RemovedEmptySubmenus}");
            foreach (var choice in recipe.RendererChoices.Where(choice => choice.IsExcludedFromMobile))
                text.AppendLine($"- {choice.TransformPath} | {choice.MobileContentMode} | fallback={choice.MobileFallbackTransformPath} | detected activations={choice.MobileActivationBindingCount}");
            foreach (var warning in result.Warnings) text.AppendLine("WARNING: " + warning);
            File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty,
                path.Replace('/', Path.DirectorySeparatorChar)), text.ToString(), Encoding.UTF8);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            recipe.MobileContentReportPath = path;
            recipe.MobileContentStatus = phase;
            EditorUtility.SetDirty(recipe);
        }

        private static void RewriteClip(AnimationClip clip, MobileAvatarMeshRecipe recipe,
            MobileContentRewriteResult result)
        {
            var changed = false;
            var bindings = AnimationUtility.GetCurveBindings(clip);
            foreach (var binding in bindings)
            {
                if (!ActiveProperties.Contains(binding.propertyName, StringComparer.Ordinal)) continue;
                var rule = recipe.RendererChoices.FirstOrDefault(choice => choice.IsExcludedFromMobile &&
                    PathIsSameOrChild(binding.path, choice.TransformPath));
                if (rule == null) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                AnimationUtility.SetEditorCurve(clip, binding, null);
                result.RemovedBindings++;
                changed = true;
                if (!rule.RedirectsToFallback || !CurveCanEnable(curve)) continue;

                var fallbackBinding = EditorCurveBinding.FloatCurve(rule.MobileFallbackTransformPath,
                    typeof(GameObject), "m_IsActive");
                var existing = AnimationUtility.GetEditorCurve(clip, fallbackBinding);
                AnimationUtility.SetEditorCurve(clip, fallbackBinding, MaxCurve(existing, curve));
                result.FallbackBindings++;
                result.RedirectedRulePaths.Add(rule.TransformPath);
                RecordFallbackEvidence(result, rule.TransformPath, clip);
            }
            if (!changed) return;
            result.RewrittenClips++;
            EditorUtility.SetDirty(clip);
        }

        private static bool ClipRequiresMobileContentRewrite(AnimationClip clip, MobileAvatarMeshRecipe recipe)
        {
            if (clip == null) return false;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (!ActiveProperties.Contains(binding.propertyName, StringComparer.Ordinal)) continue;
                if (recipe.RendererChoices.Any(choice => choice.IsExcludedFromMobile &&
                        PathIsSameOrChild(binding.path, choice.TransformPath)))
                    return true;
            }
            return false;
        }

        private static AnimationCurve MaxCurve(AnimationCurve left, AnimationCurve right)
        {
            if (left == null || left.length == 0) return new AnimationCurve(right.keys);
            if (right == null || right.length == 0) return new AnimationCurve(left.keys);
            var times = left.keys.Select(key => key.time).Concat(right.keys.Select(key => key.time))
                .Distinct().OrderBy(value => value).ToArray();
            var keys = times.Select(time => new Keyframe(time,
                Mathf.Max(left.Evaluate(time), right.Evaluate(time)), 0f, 0f)).ToArray();
            var curve = new AnimationCurve(keys) { preWrapMode = left.preWrapMode, postWrapMode = left.postWrapMode };
            return curve;
        }

        private static bool CurveCanEnable(AnimationCurve curve) =>
            curve != null && curve.keys.Any(key => key.value > 0.5f);

        private static IEnumerable<RuntimeAnimatorController> EnumerateControllerReferences(GameObject root)
        {
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                if (animator.runtimeAnimatorController != null) yield return animator.runtimeAnimatorController;
            foreach (var component in root.GetComponentsInChildren<Component>(true).Where(component => component != null))
            {
                SerializedObject serialized;
                try { serialized = new SerializedObject(component); }
                catch { continue; }
                var iterator = serialized.GetIterator();
                if (!iterator.Next(true)) continue;
                do
                {
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference &&
                        iterator.objectReferenceValue is RuntimeAnimatorController controller)
                        yield return controller;
                } while (iterator.Next(true));
            }
        }

        private static IEnumerable<UnityEngine.Object> EnumerateObjectReferences(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true).Where(component => component != null))
            {
                SerializedObject serialized;
                try { serialized = new SerializedObject(component); }
                catch { continue; }
                var iterator = serialized.GetIterator();
                if (!iterator.Next(true)) continue;
                do
                {
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference &&
                        iterator.objectReferenceValue != null)
                        yield return iterator.objectReferenceValue;
                } while (iterator.Next(true));
            }
        }

        private static List<ActivationSelector> DiscoverActivationSelectors(
            IEnumerable<RuntimeAnimatorController> runtimeControllers, MobileAvatarMeshRecipe recipe)
        {
            var selectors = new List<ActivationSelector>();
            foreach (var runtime in runtimeControllers)
            {
                var controller = runtime as AnimatorController;
                if (controller == null && runtime is AnimatorOverrideController over)
                    controller = over.runtimeAnimatorController as AnimatorController;
                if (controller == null) continue;
                foreach (var layer in controller.layers)
                    DiscoverStateMachineSelectors(layer.stateMachine, recipe, selectors);
            }
            return selectors.GroupBy(selector => selector.Rule.TransformPath + "|" + selector.Parameter + "|" +
                                                 selector.Value.ToString("R"), StringComparer.Ordinal)
                .Select(group => group.First()).ToList();
        }

        private static void DiscoverStateMachineSelectors(AnimatorStateMachine machine,
            MobileAvatarMeshRecipe recipe, ICollection<ActivationSelector> selectors)
        {
            var states = machine.states.Select(child => child.state).Where(state => state != null).ToArray();
            foreach (var state in states)
            {
                var rules = recipe.RendererChoices.Where(choice => choice.IsExcludedFromMobile &&
                    (MotionActivatesPath(state.motion, choice.TransformPath) ||
                     StateAlreadyActivatesFallback(state, choice))).ToArray();
                if (rules.Length == 0) continue;
                var incomingConditions = machine.anyStateTransitions
                    .Where(transition => transition.destinationState == state)
                    .Select(transition => transition.conditions)
                    .Concat(states.SelectMany(source => source.transitions)
                        .Where(transition => transition.destinationState == state)
                        .Select(transition => transition.conditions))
                    .Concat(machine.entryTransitions
                        .Where(transition => transition.destinationState == state)
                        .Select(transition => transition.conditions));
                foreach (var conditions in incomingConditions)
                {
                    if (conditions.Length != 1) continue;
                    var condition = conditions[0];
                    if (!TryGetExactSelectorValue(condition, out var value)) continue;
                    foreach (var rule in rules)
                        selectors.Add(new ActivationSelector
                        {
                            Rule = rule,
                            Parameter = condition.parameter,
                            Value = value
                        });
                }
            }
            foreach (var child in machine.stateMachines)
                if (child.stateMachine != null) DiscoverStateMachineSelectors(child.stateMachine, recipe, selectors);
        }

        private static HashSet<string> DiscoverPreexistingFallbackCoverage(
            IEnumerable<RuntimeAnimatorController> runtimeControllers, MobileAvatarMeshRecipe recipe)
        {
            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var runtime in runtimeControllers)
            {
                var controller = runtime as AnimatorController;
                if (controller == null && runtime is AnimatorOverrideController over)
                    controller = over.runtimeAnimatorController as AnimatorController;
                if (controller == null) continue;
                foreach (var layer in controller.layers)
                    DiscoverPreexistingFallbackCoverage(layer.stateMachine, recipe, covered);
            }
            return covered;
        }

        private static void CollectPreexistingFallbackEvidence(
            IEnumerable<RuntimeAnimatorController> runtimeControllers, MobileAvatarMeshRecipe recipe,
            MobileContentRewriteResult result)
        {
            foreach (var runtime in runtimeControllers)
            {
                var controller = runtime as AnimatorController;
                if (controller == null && runtime is AnimatorOverrideController over)
                    controller = over.runtimeAnimatorController as AnimatorController;
                if (controller == null) continue;
                foreach (var layer in controller.layers)
                    CollectPreexistingFallbackEvidence(layer.stateMachine, recipe, result);
            }
        }

        private static void CollectPreexistingFallbackEvidence(AnimatorStateMachine machine,
            MobileAvatarMeshRecipe recipe, MobileContentRewriteResult result)
        {
            foreach (var childState in machine.states)
            {
                var state = childState.state;
                if (state == null) continue;
                foreach (var choice in recipe.RendererChoices.Where(choice => choice.RedirectsToFallback &&
                             StateAlreadyActivatesFallback(state, choice)))
                {
                    result.PreexistingFallbackRulePaths.Add(choice.TransformPath);
                    foreach (var clip in EnumerateMotionClips(state.motion).Where(clip =>
                                 MotionActivatesPath(clip, choice.MobileFallbackTransformPath)))
                        RecordFallbackEvidence(result, choice.TransformPath, clip);
                }
            }
            foreach (var child in machine.stateMachines)
                if (child.stateMachine != null)
                    CollectPreexistingFallbackEvidence(child.stateMachine, recipe, result);
        }

        private static IEnumerable<AnimationClip> EnumerateMotionClips(Motion motion)
        {
            if (motion is AnimationClip clip)
            {
                yield return clip;
                yield break;
            }
            if (!(motion is BlendTree tree)) yield break;
            foreach (var child in tree.children)
            foreach (var nested in EnumerateMotionClips(child.motion))
                yield return nested;
        }

        private static void RecordFallbackEvidence(MobileContentRewriteResult result, string rulePath,
            AnimationClip clip)
        {
            var path = AssetDatabase.GetAssetPath(clip).Replace('\\', '/');
            if (string.IsNullOrEmpty(path)) return;
            if (!result.FallbackEvidenceClipPaths.TryGetValue(rulePath, out var paths))
            {
                paths = new HashSet<string>(StringComparer.Ordinal);
                result.FallbackEvidenceClipPaths.Add(rulePath, paths);
            }
            paths.Add(path);
        }

        private static void PersistBehaviorFallbackEvidence(MobileAvatarMeshRecipe recipe,
            MobileContentRewriteResult result)
        {
            foreach (var choice in recipe.RendererChoices)
            {
                choice.MobileFallbackBehaviorClipPaths.Clear();
                if (!choice.RedirectsToFallback ||
                    !result.FallbackEvidenceClipPaths.TryGetValue(choice.TransformPath, out var paths)) continue;
                choice.MobileFallbackBehaviorClipPaths.AddRange(paths.OrderBy(path => path, StringComparer.Ordinal));
            }
            EditorUtility.SetDirty(recipe);
        }

        private static void ApplyStoredBehaviorFallbackEvidence(IEnumerable<AnimationClip> resolvedClips,
            MobileAvatarMeshRecipe recipe, MobileContentRewriteResult result)
        {
            var clipsByPath = resolvedClips.Where(clip => clip != null)
                .GroupBy(clip => AssetDatabase.GetAssetPath(clip).Replace('\\', '/'), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var choice in recipe.RendererChoices.Where(choice => choice.RedirectsToFallback &&
                         !result.RedirectedRulePaths.Contains(choice.TransformPath) &&
                         !result.PreexistingFallbackRulePaths.Contains(choice.TransformPath)))
            {
                foreach (var evidencePath in choice.MobileFallbackBehaviorClipPaths)
                {
                    if (!clipsByPath.TryGetValue(evidencePath, out var clips) ||
                        !clips.Any(clip => MotionActivatesPath(clip, choice.MobileFallbackTransformPath))) continue;
                    result.PreexistingFallbackRulePaths.Add(choice.TransformPath);
                    RecordFallbackEvidence(result, choice.TransformPath, clips.First(clip =>
                        MotionActivatesPath(clip, choice.MobileFallbackTransformPath)));
                    break;
                }
            }
        }

        private static void DiscoverPreexistingFallbackCoverage(AnimatorStateMachine machine,
            MobileAvatarMeshRecipe recipe, ISet<string> covered)
        {
            foreach (var childState in machine.states)
            {
                var state = childState.state;
                if (state == null) continue;
                foreach (var choice in recipe.RendererChoices.Where(choice => choice.RedirectsToFallback))
                    if (StateAlreadyActivatesFallback(state, choice)) covered.Add(choice.TransformPath);
            }
            foreach (var child in machine.stateMachines)
                if (child.stateMachine != null)
                    DiscoverPreexistingFallbackCoverage(child.stateMachine, recipe, covered);
        }

        private static bool StateAlreadyActivatesFallback(AnimatorState state, RendererMeshChoice choice)
        {
            return state != null && choice.RedirectsToFallback &&
                   StateNameMatchesRule(state, choice) &&
                   MotionActivatesPath(state.motion, choice.MobileFallbackTransformPath);
        }

        private static bool StateNameMatchesRule(AnimatorState state, RendererMeshChoice choice)
        {
            var ruleName = NormalizeContentName(Path.GetFileName(choice.TransformPath));
            var stateName = NormalizeContentName(state.name);
            var motionName = NormalizeContentName(state.motion == null ? string.Empty : state.motion.name);
            return NamesOverlap(ruleName, stateName) || NamesOverlap(ruleName, motionName);
        }

        private static bool NamesOverlap(string left, string right)
        {
            if (left.Length < 3 || right.Length < 3) return false;
            return left.IndexOf(right, StringComparison.Ordinal) >= 0 ||
                   right.IndexOf(left, StringComparison.Ordinal) >= 0;
        }

        private static string NormalizeContentName(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var normalized = new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            foreach (var token in new[] { "hairstyle", "hair", "saikura" })
                normalized = normalized.Replace(token, string.Empty);
            return normalized;
        }

        private static bool TryGetExactSelectorValue(AnimatorCondition condition, out float value)
        {
            switch (condition.mode)
            {
                case AnimatorConditionMode.If:
                    value = 1f;
                    return true;
                case AnimatorConditionMode.Equals:
                    value = condition.threshold;
                    return true;
                default:
                    value = 0f;
                    return false;
            }
        }

        private static bool MotionActivatesPath(Motion motion, string excludedPath)
        {
            if (motion is AnimationClip clip)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (!ActiveProperties.Contains(binding.propertyName, StringComparer.Ordinal) ||
                        !PathIsSameOrChild(binding.path, excludedPath)) continue;
                    if (CurveCanEnable(AnimationUtility.GetEditorCurve(clip, binding))) return true;
                }
                return false;
            }
            if (motion is BlendTree tree)
                return tree.children.Any(child => MotionActivatesPath(child.motion, excludedPath));
            return false;
        }

        private static HashSet<string> DiscoverProvenMobileNoEffectParameters(
            IEnumerable<RuntimeAnimatorController> runtimeControllers, MobileAvatarMeshRecipe recipe)
        {
            var usage = new Dictionary<string, ParameterMobileUsage>(StringComparer.Ordinal);
            foreach (var runtime in runtimeControllers)
            {
                var controller = runtime as AnimatorController;
                if (controller == null && runtime is AnimatorOverrideController over)
                    controller = over.runtimeAnimatorController as AnimatorController;
                if (controller == null) continue;
                foreach (var layer in controller.layers)
                    CollectParameterMobileUsage(layer.stateMachine, recipe, usage);
            }
            return new HashSet<string>(usage.Where(pair => pair.Value.HasBlendTreeUse &&
                                                           !pair.Value.HasMobileEffect &&
                                                           !pair.Value.HasOtherUse)
                .Select(pair => pair.Key), StringComparer.Ordinal);
        }

        private static void CollectParameterMobileUsage(AnimatorStateMachine machine,
            MobileAvatarMeshRecipe recipe, IDictionary<string, ParameterMobileUsage> usage)
        {
            var states = machine.states.Select(child => child.state).Where(state => state != null).ToArray();
            foreach (var state in states)
            {
                CollectBlendTreeParameterUsage(state.motion, recipe, usage);
                if (state.speedParameterActive) MarkOtherParameterUsage(usage, state.speedParameter);
                if (state.timeParameterActive) MarkOtherParameterUsage(usage, state.timeParameter);
                if (state.mirrorParameterActive) MarkOtherParameterUsage(usage, state.mirrorParameter);
                if (state.cycleOffsetParameterActive) MarkOtherParameterUsage(usage, state.cycleOffsetParameter);
                foreach (var transition in state.transitions)
                foreach (var condition in transition.conditions)
                    MarkOtherParameterUsage(usage, condition.parameter);
            }
            foreach (var transition in machine.anyStateTransitions)
            foreach (var condition in transition.conditions)
                MarkOtherParameterUsage(usage, condition.parameter);
            foreach (var transition in machine.entryTransitions)
            foreach (var condition in transition.conditions)
                MarkOtherParameterUsage(usage, condition.parameter);
            foreach (var child in machine.stateMachines)
                if (child.stateMachine != null) CollectParameterMobileUsage(child.stateMachine, recipe, usage);
        }

        private static void CollectBlendTreeParameterUsage(Motion motion, MobileAvatarMeshRecipe recipe,
            IDictionary<string, ParameterMobileUsage> usage)
        {
            if (!(motion is BlendTree tree)) return;
            var hasMobileEffect = MotionHasRetainedMobileEffect(tree, recipe);
            if (tree.blendType == BlendTreeType.Direct)
            {
                foreach (var child in tree.children)
                    MarkBlendTreeParameterUsage(usage, child.directBlendParameter,
                        MotionHasRetainedMobileEffect(child.motion, recipe));
            }
            else
            {
                MarkBlendTreeParameterUsage(usage, tree.blendParameter, hasMobileEffect);
                if (tree.blendType != BlendTreeType.Simple1D)
                    MarkBlendTreeParameterUsage(usage, tree.blendParameterY, hasMobileEffect);
            }
            foreach (var child in tree.children)
                CollectBlendTreeParameterUsage(child.motion, recipe, usage);
        }

        private static void MarkBlendTreeParameterUsage(IDictionary<string, ParameterMobileUsage> usage,
            string parameter, bool hasMobileEffect)
        {
            if (string.IsNullOrEmpty(parameter)) return;
            if (!usage.TryGetValue(parameter, out var entry))
                usage[parameter] = entry = new ParameterMobileUsage();
            entry.HasBlendTreeUse = true;
            entry.HasMobileEffect |= hasMobileEffect;
        }

        private static void MarkOtherParameterUsage(IDictionary<string, ParameterMobileUsage> usage,
            string parameter)
        {
            if (string.IsNullOrEmpty(parameter)) return;
            if (!usage.TryGetValue(parameter, out var entry))
                usage[parameter] = entry = new ParameterMobileUsage();
            entry.HasOtherUse = true;
        }

        private static bool MotionHasRetainedMobileEffect(Motion motion, MobileAvatarMeshRecipe recipe)
        {
            if (motion is AnimationClip clip)
            {
                if (clip.events != null && clip.events.Length > 0) return true;
                if (AnimationUtility.GetCurveBindings(clip).Any(binding =>
                        BindingTargetsRetainedMobileContent(binding.path, recipe))) return true;
                return AnimationUtility.GetObjectReferenceCurveBindings(clip).Any(binding =>
                    BindingTargetsRetainedMobileContent(binding.path, recipe));
            }
            if (motion is BlendTree tree)
                return tree.children.Any(child => MotionHasRetainedMobileEffect(child.motion, recipe));
            return false;
        }

        private static bool BindingTargetsRetainedMobileContent(string bindingPath,
            MobileAvatarMeshRecipe recipe) =>
            !recipe.RendererChoices.Any(choice => choice.IsExcludedFromMobile &&
                PathIsSameOrChild(bindingPath, choice.TransformPath));

        private static int PruneGeneratedMenus(IEnumerable<UnityEngine.Object> candidates,
            IReadOnlyCollection<ActivationSelector> selectors, IReadOnlyCollection<string> ineffectiveParameters,
            MobileAvatarMeshRecipe recipe, out int removedEmptySubmenus)
        {
            var pending = new Queue<UnityEngine.Object>(candidates.Where(IsExpressionsMenu).Distinct());
            var visited = new HashSet<UnityEngine.Object>();
            var removed = 0;
            while (pending.Count > 0)
            {
                var menu = pending.Dequeue();
                if (menu == null || !visited.Add(menu)) continue;
                var path = AssetDatabase.GetAssetPath(menu).Replace('\\', '/');
                var serialized = new SerializedObject(menu);
                var controls = serialized.FindProperty("controls");
                if (controls == null || !controls.isArray) continue;
                var indexesToRemove = new List<int>();
                for (var index = controls.arraySize - 1; index >= 0; index--)
                {
                    var control = controls.GetArrayElementAtIndex(index);
                    var submenu = control.FindPropertyRelative("subMenu")?.objectReferenceValue;
                    if (submenu != null && IsExpressionsMenu(submenu)) pending.Enqueue(submenu);
                    var parameter = control.FindPropertyRelative("parameter")?.FindPropertyRelative("name")?.stringValue;
                    var valueProperty = control.FindPropertyRelative("value");
                    var value = valueProperty?.floatValue ?? 0f;
                    var exactExcludedSelector = !string.IsNullOrEmpty(parameter) && selectors.Any(selector =>
                        string.Equals(selector.Parameter, parameter, StringComparison.Ordinal) &&
                        Mathf.Abs(selector.Value - value) < 0.001f);
                    var drivenParameters = EnumerateControlParameters(control).ToArray();
                    var provenNoEffect = drivenParameters.Length > 0 &&
                                         drivenParameters.All(ineffectiveParameters.Contains);
                    if (!exactExcludedSelector && !provenNoEffect) continue;
                    indexesToRemove.Add(index);
                }
                if (indexesToRemove.Count == 0) continue;
                if (!CanModifyGeneratedBehaviorPath(path, recipe))
                    throw new InvalidOperationException("Mobile exclusion rewrite refused to modify source menu: " + path);
                foreach (var index in indexesToRemove)
                {
                    controls.DeleteArrayElementAtIndex(index);
                    removed++;
                }
                if (serialized.ApplyModifiedPropertiesWithoutUndo()) EditorUtility.SetDirty(menu);
            }
            removedEmptySubmenus = PruneEmptyGeneratedSubmenus(visited, recipe);
            return removed;
        }

        private static IEnumerable<string> EnumerateControlParameters(SerializedProperty control)
        {
            var main = control.FindPropertyRelative("parameter")?.FindPropertyRelative("name")?.stringValue;
            if (!string.IsNullOrEmpty(main)) yield return main;
            var subParameters = control.FindPropertyRelative("subParameters");
            if (subParameters == null || !subParameters.isArray) yield break;
            for (var index = 0; index < subParameters.arraySize; index++)
            {
                var name = subParameters.GetArrayElementAtIndex(index)
                    .FindPropertyRelative("name")?.stringValue;
                if (!string.IsNullOrEmpty(name)) yield return name;
            }
        }

        private static int PruneEmptyGeneratedSubmenus(IEnumerable<UnityEngine.Object> menus,
            MobileAvatarMeshRecipe recipe)
        {
            var generatedMenus = menus.Where(menu => menu != null).Distinct().ToArray();
            var removed = 0;
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var menu in generatedMenus)
                {
                    var serialized = new SerializedObject(menu);
                    var controls = serialized.FindProperty("controls");
                    if (controls == null || !controls.isArray) continue;
                    var remove = new List<int>();
                    for (var index = controls.arraySize - 1; index >= 0; index--)
                    {
                        var submenu = controls.GetArrayElementAtIndex(index)
                            .FindPropertyRelative("subMenu")?.objectReferenceValue;
                        if (submenu == null || !IsExpressionsMenu(submenu)) continue;
                        var submenuSerialized = new SerializedObject(submenu);
                        var submenuControls = submenuSerialized.FindProperty("controls");
                        if (submenuControls != null && submenuControls.isArray && submenuControls.arraySize == 0)
                            remove.Add(index);
                    }
                    if (remove.Count == 0) continue;
                    var path = AssetDatabase.GetAssetPath(menu).Replace('\\', '/');
                    if (!CanModifyGeneratedBehaviorPath(path, recipe))
                        throw new InvalidOperationException(
                            "Mobile exclusion rewrite refused to modify source menu: " + path);
                    foreach (var index in remove)
                    {
                        controls.DeleteArrayElementAtIndex(index);
                        removed++;
                    }
                    if (serialized.ApplyModifiedPropertiesWithoutUndo()) EditorUtility.SetDirty(menu);
                    changed = true;
                }
            }
            return removed;
        }

        private static bool IsExpressionsMenu(UnityEngine.Object value)
        {
            if (value == null) return false;
            var name = value.GetType().FullName ?? value.GetType().Name;
            return name.IndexOf("VRCExpressionsMenu", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool CanModifyGeneratedBehaviorPath(string path, MobileAvatarMeshRecipe recipe) =>
            !string.IsNullOrEmpty(path) &&
            (path.StartsWith(recipe.OutputRoot + "/", StringComparison.Ordinal) ||
             path.StartsWith("Packages/com.vrcfury.temp/Builds/", StringComparison.OrdinalIgnoreCase));

        private static void ClearRendererPayload(Transform root)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = false;
                renderer.sharedMaterials = Array.Empty<Material>();
                if (renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = null;
                if (renderer is ParticleSystemRenderer particle) particle.mesh = null;
            }
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true)) filter.sharedMesh = null;
        }

        private static void EnsureNoKeptRendererUsesExcludedBones(GameObject avatar, Transform excluded,
            MobileAvatarMeshRecipe recipe)
        {
            var excludedTransforms = new HashSet<Transform>(excluded.GetComponentsInChildren<Transform>(true));
            var excludedPath = AnimationUtility.CalculateTransformPath(excluded, avatar.transform);
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform);
                if (PathIsSameOrChild(rendererPath, excludedPath) ||
                    recipe.RendererChoices.Any(choice => choice.IsExcludedFromMobile &&
                        PathIsSameOrChild(rendererPath, choice.TransformPath))) continue;
                if ((renderer.rootBone != null && excludedTransforms.Contains(renderer.rootBone)) ||
                    renderer.bones.Any(bone => bone != null && excludedTransforms.Contains(bone)))
                    throw new InvalidOperationException("Cannot delete " + excludedPath +
                                                        " because retained renderer " + rendererPath + " uses its bones.");
            }
        }

        private static bool PathIsSameOrChild(string path, string rootPath)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(rootPath)) return false;
            return string.Equals(path, rootPath, StringComparison.Ordinal) ||
                   path.StartsWith(rootPath + "/", StringComparison.Ordinal);
        }

        private static int PathDepth(string path) => string.IsNullOrEmpty(path) ? 0 : path.Count(character => character == '/');
    }
}
