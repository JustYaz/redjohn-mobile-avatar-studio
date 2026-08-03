using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDKBase.Validation;

namespace MobileAvatarStudio.Editor
{
    internal static class MaterialConversionPipeline
    {
        public const string TargetShaderName = "VRChat/Mobile/Toon Standard";
        public const string TransparentTargetShaderName = "VRChat/Mobile/Particles/Additive";
        public const string MultiplyTargetShaderName = "VRChat/Mobile/Particles/Multiply";
        private const string MaterialPolicyVersion = "mobile-material-policy-v3-whitelist-profile-selection";
        private const string FlatRampResourceName = "VRChat/ShadowRampFlat";
        private const string DiscardedPoiyomiDefaultMainTextureName = "T_MainTex_D";

        private static readonly string[] MainTextureNames =
            { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_MainTexture" };
        private static readonly string[] NormalTextureNames =
            { "_BumpMap", "_NormalMap", "_RgbNormalR", "_DetailNormalMap" };
        private static readonly string[] EmissionTextureNames =
            { "_EmissionMap", "_EmissionMap1", "_EmissionMask", "_EmissionMask1" };
        private static readonly string[] MetallicTextureNames =
            { "_MetallicGlossMap", "_MetallicMap", "_MetallicTex" };
        private static readonly string[] GlossTextureNames =
            { "_GlossMap", "_MetallicGlossMap", "_SmoothnessMap" };
        private static readonly string[] MatcapTextureNames =
            { "_Matcap", "_MatCap", "_Matcap2", "_Matcap3" };
        private static readonly string[] MatcapMaskNames =
            { "_MatcapMask", "_MatCapMask", "_Matcap2Mask", "_Matcap3Mask" };
        private static readonly string[] OcclusionTextureNames =
            { "_OcclusionMap", "_OcclusionTex" };
        private static readonly string[] ColorMaskNames =
            { "_ColorMask", "_RGBMask", "_GlobalMaskTexture1" };

        public static void Analyze(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null || recipe.SourcePrefab == null) throw new ArgumentNullException(nameof(recipe));

            var previousApprovals = recipe.MaterialChoices
                .Where(choice => choice.SourceMaterial != null && choice.IsCurrentMappingApproved)
                .ToDictionary(choice => choice.SourceMaterial, choice => choice.SourceSignature);
            var previousGenerated = recipe.MaterialChoices
                .Where(choice => choice.SourceMaterial != null && choice.GeneratedMaterial != null)
                .ToDictionary(choice => choice.SourceMaterial, choice => choice.GeneratedMaterial);
            var previousTargets = recipe.MaterialChoices
                .Where(choice => choice.SourceMaterial != null && !string.IsNullOrEmpty(choice.TargetShaderName))
                .ToDictionary(choice => choice.SourceMaterial,
                    choice => new KeyValuePair<string, string>(choice.SourceSignature, choice.TargetShaderName));

            recipe.MaterialChoices.Clear();
            var animatedBindingsByPath = FindAnimatedMaterialBindings(recipe.SourcePrefab, out var animatedMaterialUsages);
            var usages = new Dictionary<Material, HashSet<string>>();

            foreach (var renderer in recipe.SourcePrefab.GetComponentsInChildren<Renderer>(true))
            {
                var path = MeshAnalysisUtility.CalculateTransformPath(renderer.transform, recipe.SourcePrefab.transform);
                if (IsExcludedRendererPath(recipe, path)) continue;
                if (UvTileSplitPipeline.FindEnabledSplit(recipe, path) != null) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    if (!usages.TryGetValue(material, out var paths))
                    {
                        paths = new HashSet<string>(StringComparer.Ordinal);
                        usages.Add(material, paths);
                    }
                    paths.Add(path);
                }
            }

            foreach (var animatedUsage in animatedMaterialUsages)
            {
                // Object-reference material curves still need a mobile material when their
                // renderer is UV-split. The source renderer is removed from the mobile prefab,
                // but the alternate materials must remain available for the rewritten curve.
                // Float shader-property curves are handled separately by the behavior pass.
                animatedUsage.Value.RemoveWhere(path => IsExcludedRendererPath(recipe, path));
                if (animatedUsage.Value.Count == 0) continue;
                if (!usages.TryGetValue(animatedUsage.Key, out var paths))
                {
                    paths = new HashSet<string>(StringComparer.Ordinal);
                    usages.Add(animatedUsage.Key, paths);
                }
                paths.UnionWith(animatedUsage.Value);
            }

            // UV-tile pieces are generated, isolated source domains. They are deliberately
            // analyzed as ordinary one-to-one materials so Stage 3 creates an independent
            // mobile material for every retained piece without modifying the PC material.
            foreach (var split in recipe.UvTileSplitRenderers.Where(item => item.Compatible && item.SplitEnabled))
            foreach (var piece in split.Pieces.Where(item => item.KeepOnMobile && item.IsolatedSourceMaterial != null))
            {
                if (!usages.TryGetValue(piece.IsolatedSourceMaterial, out var paths))
                {
                    paths = new HashSet<string>(StringComparer.Ordinal);
                    usages.Add(piece.IsolatedSourceMaterial, paths);
                }
                paths.Add(UvTileSplitPipeline.BuildChildPath(split, piece));
            }

            foreach (var pair in usages.OrderBy(item => AssetDatabase.GetAssetPath(item.Key), StringComparer.Ordinal)
                         .ThenBy(item => item.Key.name, StringComparer.OrdinalIgnoreCase))
            {
                var material = pair.Key;
                var path = AssetDatabase.GetAssetPath(material);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long localId);
                var signature = ComputeSourceSignature(material, path, guid, localId);
                var animatedCount = pair.Value.Sum(rendererPath =>
                    animatedBindingsByPath.TryGetValue(rendererPath, out var count) ? count : 0);
                var usedByParticleRenderer = pair.Value.Any(rendererPath =>
                    IsParticleRendererPath(recipe.SourcePrefab, rendererPath));
                var surfaceClassification = ClassifySurface(material, usedByParticleRenderer);
                var transparencyRisk = surfaceClassification != MaterialSurfaceClassification.Opaque;
                var recommendedShader = RecommendShader(surfaceClassification);
                var hasSpecialSurfaceFeatures = HasSpecialSurfaceFeatures(material);
                var recommendationConfidence = GetRecommendationConfidence(surfaceClassification,
                    hasSpecialSurfaceFeatures);
                var recommendationSummary = BuildRecommendationSummary(surfaceClassification,
                    recommendationConfidence);
                var targetShader = recommendedShader;
                if (previousTargets.TryGetValue(material, out var previousTarget) &&
                    string.Equals(previousTarget.Key, signature, StringComparison.Ordinal) &&
                    GetAvailableMobileAvatarShaderNames().Contains(previousTarget.Value, StringComparer.Ordinal))
                    targetShader = previousTarget.Value;
                var hasEmissionMap = FirstMeaningfulTexture(material, EmissionTextureNames) != null;
                var dropsColorOnlyEmission = !hasEmissionMap &&
                                             HasNonZeroColor(material, "_EmissionColor", "_EmissionColor1", "_EmissionColor0");
                var hasMatcap = FirstMeaningfulTexture(material, MatcapTextureNames) != null;
                var stripsDefaultMain = ReferencesDiscardedPoiyomiDefaultMainTexture(material);
                var reasons = new List<string>();
                if (surfaceClassification == MaterialSurfaceClassification.Cutout)
                    reasons.Add("The source uses cutout/clipping. Mobile Toon Standard is opaque, so the safe recommendation avoids additive washout but clipped edges require visual review.");
                else if (surfaceClassification == MaterialSurfaceClassification.TransparentMesh)
                    reasons.Add("The source is a transparent mesh material. Toon Standard is recommended as a safe opaque approximation; select a particle shader only when the material is intentionally additive or multiplicative.");
                else if (surfaceClassification == MaterialSurfaceClassification.ParticleAdditive)
                    reasons.Add("This material is used by a ParticleSystemRenderer or an additive particle shader, so Particles/Additive is recommended.");
                else if (surfaceClassification == MaterialSurfaceClassification.ParticleMultiply)
                    reasons.Add("This material uses a multiply particle shader, so Particles/Multiply is recommended.");
                if (animatedCount > 0)
                    reasons.Add($"{animatedCount} animation binding(s) affect this renderer's material domain; animation remapping must be validated later.");
                if (hasEmissionMap)
                    reasons.Add("The emission map is retained for manual polish, but every generated mobile emission strength starts at zero.");
                else if (dropsColorOnlyEmission)
                    reasons.Add("Color-only PC emission has no emission map and will be disabled on mobile.");
                if (stripsDefaultMain)
                    reasons.Add("Poiyomi T_MainTex_D is a placeholder and will not be assigned to the mobile material.");
                if (!transparencyRisk && hasMatcap)
                    reasons.Add("Matcap textures are preserved, but matcap activation starts disabled for manual polish.");
                if (hasSpecialSurfaceFeatures)
                    reasons.Add("Metallic, matcap, emission, rim, or hue features were detected. This material remains a separate adjustable copy.");
                if (reasons.Count == 0)
                    reasons.Add("Common texture and color properties can be mapped one-to-one. Visual approval is still required.");

                var choice = new MaterialConversionChoice
                {
                    SourceMaterial = material,
                    SourceAssetPath = path,
                    SourceGuid = guid,
                    SourceLocalFileId = localId,
                    SourceSignature = signature,
                    SourceShaderName = material.shader != null ? material.shader.name : "Missing shader",
                    TargetShaderName = targetShader,
                    RecommendedShaderName = recommendedShader,
                    SurfaceClassification = surfaceClassification,
                    RecommendationConfidence = recommendationConfidence,
                    RecommendationSummary = recommendationSummary,
                    SourceRenderQueue = material.renderQueue,
                    UsedByParticleRenderer = usedByParticleRenderer,
                    RendererUsageCount = pair.Value.Count,
                    AnimatedBindingCount = animatedCount,
                    TransparencyRisk = transparencyRisk,
                    Risk = transparencyRisk || animatedCount > 0 || hasSpecialSurfaceFeatures
                        ? MaterialConversionRisk.ReviewRequired
                        : MaterialConversionRisk.Low,
                    RiskSummary = string.Join(" ", reasons)
                };
                choice.RendererPaths.AddRange(pair.Value.OrderBy(value => value, StringComparer.Ordinal));
                if (previousGenerated.TryGetValue(material, out var generated)) choice.GeneratedMaterial = generated;
                if (previousApprovals.TryGetValue(material, out var approvedSignature) &&
                    string.Equals(approvedSignature, signature, StringComparison.Ordinal))
                    choice.ApproveCurrentMapping();
                recipe.MaterialChoices.Add(choice);
            }

            InvalidateDownstream(recipe, "Material recommendations changed; rebuild Stages 3-6");
            EditorUtility.SetDirty(recipe);
            if (AssetDatabase.Contains(recipe)) AssetDatabase.SaveAssets();
        }

        public static void InvalidateDownstream(MobileAvatarMeshRecipe recipe, string status)
        {
            if (recipe == null) return;
            recipe.TexturePassUtc = string.Empty;
            recipe.FinalAssemblyUtc = string.Empty;
            recipe.FinalAssemblyStatus = status;
            recipe.BehaviorPassUtc = string.Empty;
            recipe.BehaviorAppliedToCombined = false;
            recipe.BehaviorStatus = "Needs rebuild after material shader change";
            recipe.MobileResolvedAuditPassed = false;
            recipe.MobileValidationStatus = "Needs rerun after material shader change";
            ManualPolishPipeline.Invalidate(recipe, "Material shaders changed; rebuild Stages 3-6 before manual polish");
            EditorUtility.SetDirty(recipe);
        }

        public static string BuildMaterialDraft(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null || recipe.SourcePrefab == null) throw new ArgumentNullException(nameof(recipe));
            if (recipe.MaterialChoices.Count == 0)
                throw new InvalidOperationException("No material mappings exist. Re-scan materials first.");
            var unapproved = recipe.MaterialChoices.Where(choice => !choice.IsCurrentMappingApproved).ToArray();
            if (unapproved.Length > 0)
                throw new InvalidOperationException(
                    $"Approve every material mapping before conversion. {unapproved.Length} mapping(s) remain unapproved.");

            var allowedShaders = GetAvailableMobileAvatarShaderNames();
            var invalidMappings = recipe.MaterialChoices.Where(choice =>
                    !allowedShaders.Contains(choice.TargetShaderName, StringComparer.Ordinal))
                .Select(choice => choice.SourceMaterial.name + " -> " + choice.TargetShaderName).ToArray();
            if (invalidMappings.Length > 0)
                throw new InvalidOperationException("One or more target shaders are not in the installed VRChat avatar " +
                                                    "mobile whitelist: " + string.Join(", ", invalidMappings));

            foreach (var shaderName in recipe.MaterialChoices.Select(choice => choice.TargetShaderName)
                         .Distinct(StringComparer.Ordinal))
                if (Shader.Find(shaderName) == null)
                    throw new InvalidOperationException(shaderName + " is not installed in this Unity project.");

            ValidateSources(recipe);
            var sourceHashBefore = MeshAnalysisUtility.ComputeAssetHash(recipe.SourceAssetPath);
            var meshDraftPath = MeshCandidatePipeline.BuildSelectedPrefab(recipe);
            var meshDraft = AssetDatabase.LoadAssetAtPath<GameObject>(meshDraftPath);
            if (meshDraft == null) throw new InvalidOperationException("The isolated mesh draft could not be loaded.");

            var materialsRoot = recipe.OutputRoot + "/Materials";
            MeshAnalysisUtility.EnsureAssetFolder(materialsRoot);
            var materialPaths = BuildMaterialPaths(materialsRoot, recipe.MaterialChoices);
            var materialMap = new Dictionary<Material, Material>();
            var report = new StringBuilder();
            report.AppendLine("MOBILE AVATAR STUDIO - MATERIAL CONVERSION DRAFT");
            report.AppendLine("Generated: " + DateTime.UtcNow.ToString("O"));
            report.AppendLine("Policy: one source material maps to one generated material; no automatic merges or atlases.");
            report.AppendLine("Shader policy: per-material selection from the installed VRChat avatar mobile whitelist.");
            report.AppendLine("Safe default for non-particle opaque/cutout/transparent meshes: " + TargetShaderName);
            report.AppendLine("Particle recommendations: " + TransparentTargetShaderName + " or " + MultiplyTargetShaderName);
            report.AppendLine("Policy: emission maps are retained but generated lit materials start with emission disabled; Poiyomi T_MainTex_D is discarded; matcap textures are retained with activation disabled for manual polish.");
            report.AppendLine("Naming policy: generated material filenames and Unity object names match the source name; a stable suffix is used only for real name collisions.");

            foreach (var choice in recipe.MaterialChoices)
            {
                var outputPath = materialPaths[choice];
                var targetShader = Shader.Find(choice.TargetShaderName);
                var generated = LoadOrMigrateGeneratedMaterial(choice, outputPath, materialsRoot);
                if (generated == null)
                {
                    generated = new Material(targetShader)
                    {
                        name = Path.GetFileNameWithoutExtension(outputPath)
                    };
                    AssetDatabase.CreateAsset(generated, outputPath);
                }
                else
                {
                    MobileMaterialSanitizer.ResetToShader(generated, targetShader,
                        Path.GetFileNameWithoutExtension(outputPath));
                }

                // Unity warns when a main asset's object name differs from its filename. Keep them identical.
                generated.name = Path.GetFileNameWithoutExtension(outputPath);
                ConvertOneToOne(choice.SourceMaterial, generated, targetShader);
                EditorUtility.SetDirty(generated);
                choice.GeneratedMaterial = generated;
                materialMap[choice.SourceMaterial] = generated;
                report.AppendLine($"MATERIAL {choice.SourceMaterial.name} | {choice.SourceShaderName} -> {choice.TargetShaderName} | " +
                                  $"recommended={choice.RecommendedShaderName} | surface={choice.SurfaceClassification} | " +
                                  $"particleRenderer={choice.UsedByParticleRenderer} | risk={choice.Risk} | " +
                                  $"animatedBindings={choice.AnimatedBindingCount} | output={outputPath}");
            }

            AssetDatabase.SaveAssets();
            var instance = UnityEngine.Object.Instantiate(meshDraft);
            instance.name = recipe.SourcePrefab.name + "_QuestMaterial_DRAFT";
            try
            {
                foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                {
                    var materials = renderer.sharedMaterials;
                    var changed = false;
                    for (var index = 0; index < materials.Length; index++)
                    {
                        if (materials[index] != null && materialMap.TryGetValue(materials[index], out var converted))
                        {
                            materials[index] = converted;
                            changed = true;
                        }
                    }
                    if (changed) renderer.sharedMaterials = materials;
                }

                var prefabRoot = recipe.OutputRoot + "/Checkpoints/Materials";
                MeshAnalysisUtility.EnsureAssetFolder(prefabRoot);
                var prefabPath = prefabRoot + "/" + MeshAnalysisUtility.SanitizeFileName(instance.name) + ".prefab";
                var saved = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
                if (saved == null) throw new InvalidOperationException("Unity failed to save the Quest material draft prefab.");
                recipe.GeneratedMaterialPrefabPath = prefabPath;
                recipe.MaterialPassUtc = DateTime.UtcNow.ToString("O");
                InvalidateDownstream(recipe, "Material draft rebuilt; rescan textures and rebuild Stages 4-6");
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();

                var reportRoot = recipe.OutputRoot + "/Reports";
                MeshAnalysisUtility.EnsureAssetFolder(reportRoot);
                report.AppendLine("PREFAB " + prefabPath);
                report.AppendLine("STATUS DRAFT - texture isolation, atlasing, animation remapping, dynamics, and Android/iOS validation have not run.");
                WriteAssetText(reportRoot + "/MaterialConversionReport.txt", report.ToString());

                var sourceHashAfter = MeshAnalysisUtility.ComputeAssetHash(recipe.SourceAssetPath);
                if (!string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The source prefab changed during material conversion. Generated output is a failed draft.");

                Selection.activeObject = saved;
                AssetDatabase.Refresh();
                return prefabPath;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static void ValidateSources(MobileAvatarMeshRecipe recipe)
        {
            var currentPath = AssetDatabase.GetAssetPath(recipe.SourcePrefab);
            if (!string.Equals(currentPath, recipe.SourceAssetPath, StringComparison.Ordinal) ||
                !string.Equals(MeshAnalysisUtility.ComputeAssetHash(currentPath), recipe.SourceFileHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The source prefab changed after analysis. Re-analyze before conversion.");

            foreach (var choice in recipe.MaterialChoices)
            {
                if (choice.SourceMaterial == null)
                    throw new InvalidOperationException("A source material referenced by the recipe is missing.");
                var current = ComputeSourceSignature(choice.SourceMaterial, AssetDatabase.GetAssetPath(choice.SourceMaterial),
                    choice.SourceGuid, choice.SourceLocalFileId);
                if (!string.Equals(current, choice.SourceSignature, StringComparison.Ordinal))
                    throw new InvalidOperationException(choice.SourceMaterial.name +
                                                        " changed after approval. Re-scan and approve its mapping again.");
            }
        }

        private static Dictionary<string, int> FindAnimatedMaterialBindings(GameObject sourcePrefab,
            out Dictionary<Material, HashSet<string>> materialUsages)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            materialUsages = new Dictionary<Material, HashSet<string>>();
            var dependencies = AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(sourcePrefab), true);
            var clips = dependencies.SelectMany(AssetDatabase.LoadAllAssetsAtPath).OfType<AnimationClip>().Distinct();
            foreach (var clip in clips)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (!binding.propertyName.StartsWith("material.", StringComparison.Ordinal)) continue;
                    result[binding.path] = result.TryGetValue(binding.path, out var count) ? count + 1 : 1;
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (binding.propertyName.IndexOf("material", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    result[binding.path] = result.TryGetValue(binding.path, out var count) ? count + 1 : 1;
                    foreach (var keyframe in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                    {
                        if (!(keyframe.value is Material material)) continue;
                        if (!materialUsages.TryGetValue(material, out var paths))
                        {
                            paths = new HashSet<string>(StringComparer.Ordinal);
                            materialUsages.Add(material, paths);
                        }
                        paths.Add(binding.path);
                    }
                }
            }
            return result;
        }

        private static string ComputeSourceSignature(Material material, string path, string guid, long localId)
        {
            var dependencyHash = string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.GetAssetDependencyHash(path).ToString();
            return MeshAnalysisUtility.ComputeStringSignature(new[]
            {
                MaterialPolicyVersion,
                guid ?? string.Empty,
                localId.ToString(),
                material.shader != null ? material.shader.name : "<missing>",
                material.renderQueue.ToString(),
                dependencyHash
            });
        }

        public static string[] GetAvailableMobileAvatarShaderNames()
        {
            return AvatarValidation.ShaderWhiteList
                .Where(name => name.StartsWith("VRChat/Mobile/", StringComparison.Ordinal) &&
                               Shader.Find(name) != null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        public static void ApplyRecommendedShader(MaterialConversionChoice choice)
        {
            if (choice == null || string.IsNullOrEmpty(choice.RecommendedShaderName)) return;
            choice.TargetShaderName = choice.RecommendedShaderName;
            choice.RevokeApproval();
        }

        private static bool IsParticleRendererPath(GameObject root, string rendererPath)
        {
            if (root == null) return false;
            var transform = string.IsNullOrEmpty(rendererPath) ? root.transform : root.transform.Find(rendererPath);
            return transform != null && transform.GetComponent<ParticleSystemRenderer>() != null;
        }

        private static MaterialSurfaceClassification ClassifySurface(Material material,
            bool usedByParticleRenderer)
        {
            var shaderName = material.shader != null ? material.shader.name : string.Empty;
            var renderType = material.GetTag("RenderType", false, string.Empty);
            var particleEvidence = usedByParticleRenderer || ContainsTerm(shaderName, "Particle") ||
                                   ContainsTerm(renderType, "Particle");
            if (particleEvidence)
                return ContainsTerm(shaderName, "Multiply")
                    ? MaterialSurfaceClassification.ParticleMultiply
                    : MaterialSurfaceClassification.ParticleAdditive;

            var mode = Mathf.RoundToInt(FirstFloat(material, -1f, "_Mode", "_RenderingMode"));
            var cutout = mode == 1 || material.IsKeywordEnabled("_ALPHATEST_ON") ||
                         ContainsTerm(shaderName, "Cutout") || ContainsTerm(renderType, "Cutout") ||
                         material.renderQueue >= 2450 && material.renderQueue < 3000;
            if (cutout) return MaterialSurfaceClassification.Cutout;

            var transparent = mode >= 2 || material.IsKeywordEnabled("_ALPHABLEND_ON") ||
                              material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") ||
                              ContainsTerm(shaderName, "Transparent") || ContainsTerm(shaderName, "Fade") ||
                              ContainsTerm(renderType, "Transparent") || material.renderQueue >= 3000;
            return transparent
                ? MaterialSurfaceClassification.TransparentMesh
                : MaterialSurfaceClassification.Opaque;
        }

        private static string RecommendShader(MaterialSurfaceClassification classification)
        {
            switch (classification)
            {
                case MaterialSurfaceClassification.ParticleMultiply:
                    return MultiplyTargetShaderName;
                case MaterialSurfaceClassification.ParticleAdditive:
                    return TransparentTargetShaderName;
                default:
                    return TargetShaderName;
            }
        }

        private static MaterialRecommendationConfidence GetRecommendationConfidence(
            MaterialSurfaceClassification classification, bool hasSpecialSurfaceFeatures)
        {
            switch (classification)
            {
                case MaterialSurfaceClassification.ParticleAdditive:
                case MaterialSurfaceClassification.ParticleMultiply:
                    return MaterialRecommendationConfidence.High;
                case MaterialSurfaceClassification.Cutout:
                case MaterialSurfaceClassification.TransparentMesh:
                    return MaterialRecommendationConfidence.NeedsVisualReview;
                default:
                    return hasSpecialSurfaceFeatures
                        ? MaterialRecommendationConfidence.High
                        : MaterialRecommendationConfidence.Medium;
            }
        }

        private static string BuildRecommendationSummary(MaterialSurfaceClassification classification,
            MaterialRecommendationConfidence confidence)
        {
            switch (classification)
            {
                case MaterialSurfaceClassification.ParticleAdditive:
                    return "Actual particle/additive evidence maps directly to the mobile additive particle shader.";
                case MaterialSurfaceClassification.ParticleMultiply:
                    return "Actual multiply-particle evidence maps directly to the mobile multiply particle shader.";
                case MaterialSurfaceClassification.Cutout:
                    return "No whitelisted mobile avatar shader preserves Poiyomi cutout exactly. Toon Standard avoids additive washout, but alpha edges must be inspected.";
                case MaterialSurfaceClassification.TransparentMesh:
                    return "No whitelisted lit mobile avatar shader preserves mesh transparency exactly. Toon Standard is the safest non-glowing approximation; override only after preview.";
                default:
                    return confidence == MaterialRecommendationConfidence.High
                        ? "Toon Standard retains the widest useful subset of the detected Poiyomi material features."
                        : "Toon Standard is the quality-first general mobile choice; simpler whitelist shaders remain available as manual overrides.";
            }
        }

        private static bool ContainsTerm(string value, string term) =>
            !string.IsNullOrEmpty(value) && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsExcludedRendererPath(MobileAvatarMeshRecipe recipe, string path) =>
            recipe.RendererChoices.Any(choice => choice.IsExcludedFromMobile &&
                (string.Equals(path, choice.TransformPath, StringComparison.Ordinal) ||
                 path.StartsWith(choice.TransformPath + "/", StringComparison.Ordinal)));

        private static bool HasSpecialSurfaceFeatures(Material material)
        {
            return FirstMeaningfulTexture(material, MetallicTextureNames) != null ||
                   FirstMeaningfulTexture(material, GlossTextureNames) != null ||
                   FirstMeaningfulTexture(material, MatcapTextureNames) != null ||
                   FirstMeaningfulTexture(material, EmissionTextureNames) != null ||
                   FirstMeaningfulTexture(material, ColorMaskNames) != null ||
                   HasNonZeroColor(material, "_EmissionColor", "_EmissionColor1") ||
                   HasNonZeroFloat(material, "_Metallic", "_MetallicStrength", "_MatcapStrength", "_RimIntensity", "_HueShift");
        }

        private static void ConvertOneToOne(Material source, Material target, Shader targetShader)
        {
            var main = FirstMeaningfulTexture(source, MainTextureNames, out var mainProperty);
            var mainScale = string.IsNullOrEmpty(mainProperty) ? Vector2.one : source.GetTextureScale(mainProperty);
            var mainOffset = string.IsNullOrEmpty(mainProperty) ? Vector2.zero : source.GetTextureOffset(mainProperty);
            var color = FirstColor(source, Color.white, "_Color", "_BaseColor");
            if (string.Equals(targetShader.name, TransparentTargetShaderName, StringComparison.Ordinal) ||
                string.Equals(targetShader.name, MultiplyTargetShaderName, StringComparison.Ordinal))
            {
                target.shader = targetShader;
                target.shaderKeywords = Array.Empty<string>();
                target.renderQueue = -1;
                SetTexture(target, "_MainTex", main);
                SetColor(target, "_Color", color);
                if (HasShaderPropertyType(target, "_MainTex", ShaderPropertyType.Texture))
                {
                    target.SetTextureScale("_MainTex", mainScale);
                    target.SetTextureOffset("_MainTex", mainOffset);
                }
                return;
            }

            var normal = FirstMeaningfulTexture(source, NormalTextureNames);
            var emission = FirstMeaningfulTexture(source, EmissionTextureNames);
            var metallic = FirstMeaningfulTexture(source, MetallicTextureNames);
            var gloss = FirstMeaningfulTexture(source, GlossTextureNames);
            var matcap = FirstMeaningfulTexture(source, MatcapTextureNames);
            var matcapMask = FirstMeaningfulTexture(source, MatcapMaskNames);
            var occlusion = FirstMeaningfulTexture(source, OcclusionTextureNames);
            var colorMask = FirstMeaningfulTexture(source, ColorMaskNames);
            var emissionColor = emission != null
                ? FirstColor(source, Color.white, "_EmissionColor", "_EmissionColor1", "_EmissionColor0")
                : Color.black;
            var culling = Mathf.RoundToInt(FirstFloat(source, 2f, "_Culling", "_Cull", "_CullMode"));
            var bumpScale = FirstFloat(source, 1f, "_BumpScale", "_NormalScale");
            var metallicStrength = Mathf.Clamp01(FirstFloat(source, metallic != null ? 0.5f : 0f,
                "_MetallicStrength", "_Metallic"));
            var glossStrength = Mathf.Clamp01(FirstFloat(source, gloss != null ? 0.5f : 0.35f,
                "_GlossStrength", "_Glossiness", "_Smoothness"));
            var occlusionStrength = Mathf.Clamp01(FirstFloat(source, occlusion != null ? 1f : 0f,
                "_OcclusionStrength"));
            var hueShift = FirstFloat(source, 0f, "_HueShift");

            target.shader = targetShader;
            target.shaderKeywords = Array.Empty<string>();
            target.renderQueue = -1;
            SetColor(target, "_Color", color);
            SetTexture(target, "_MainTex", main);
            if (HasShaderPropertyType(target, "_MainTex", ShaderPropertyType.Texture))
            {
                target.SetTextureScale("_MainTex", mainScale);
                target.SetTextureOffset("_MainTex", mainOffset);
            }
            SetFloat(target, "_Culling", Mathf.Clamp(culling, 0, 2));
            SetFloat(target, "_Cull", Mathf.Clamp(culling, 0, 2));
            SetTexture(target, "_BumpMap", normal);
            SetFloat(target, "_BumpScale", bumpScale);
            SetTexture(target, "_EmissionMap", emission);
            SetColor(target, "_EmissionColor", emissionColor);
            SetTexture(target, "_MetallicMap", metallic);
            SetTexture(target, "_MetallicGlossMap", metallic ?? gloss);
            SetTexture(target, "_SpecGlossMap", gloss);
            SetFloat(target, "_MetallicStrength", metallicStrength);
            SetFloat(target, "_Metallic", metallicStrength);
            SetTexture(target, "_GlossMap", gloss);
            SetFloat(target, "_GlossStrength", glossStrength);
            SetFloat(target, "_Glossiness", glossStrength);
            SetTexture(target, "_Matcap", matcap);
            SetTexture(target, "_MatCap", matcap);
            SetTexture(target, "_MatcapMask", matcapMask);
            SetTexture(target, "_MatCapMask", matcapMask);
            // Keep the source matcap texture available for manual polish, but always
            // start with the feature unticked/disabled so the generated avatar's
            // initial appearance is not washed by a PC-only matcap setup.
            SetFloat(target, "_MatcapEnable", 0f);
            SetFloat(target, "_Matcap2Enable", 0f);
            SetFloat(target, "_Matcap3Enable", 0f);
            SetFloat(target, "_MatcapStrength", 0f);
            SetFloat(target, "_MatCapStrength", 0f);
            SetFloat(target, "_Matcap2Strength", 0f);
            SetFloat(target, "_Matcap3Strength", 0f);
            if (matcap != null)
            {
                var flatRamp = Resources.Load<Texture2D>(FlatRampResourceName);
                if (flatRamp == null)
                    throw new InvalidOperationException("The installed VRChat SDK is missing " + FlatRampResourceName + ".");
                SetTexture(target, "_Ramp", flatRamp);
            }
            SetTexture(target, "_OcclusionMap", occlusion);
            SetFloat(target, "_OcclusionStrength", occlusionStrength);
            SetTexture(target, "_ColorMask", colorMask);
            SetFloat(target, "_HueShift", hueShift);
            NeutralizeGeneratedEmission(target);

            SetKeyword(target, "USE_NORMAL_MAPS", normal != null);
            SetKeyword(target, "USE_SPECULAR", metallic != null || gloss != null || metallicStrength > 0f);
            SetKeyword(target, "USE_MATCAP", false);
            SetKeyword(target, "USE_OCCLUSION_MAP", occlusion != null && occlusionStrength > 0f);
            SetKeyword(target, "USE_HUE_SHIFT", Mathf.Abs(hueShift) > 0.0001f);
            SetKeyword(target, "USE_COLOR_MASK", colorMask != null);
        }

        private static void NeutralizeGeneratedEmission(Material target)
        {
            var shader = target != null ? target.shader : null;
            if (shader == null) return;

            var hasDirectEmissionMagnitude = false;
            for (var index = 0; index < shader.GetPropertyCount(); index++)
            {
                var propertyName = shader.GetPropertyName(index);
                if (propertyName.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var propertyType = shader.GetPropertyType(index);
                if (propertyType != ShaderPropertyType.Float && propertyType != ShaderPropertyType.Range &&
                    propertyType != ShaderPropertyType.Int)
                    continue;
                if (!IsEmissionMagnitudeProperty(propertyName)) continue;

                target.SetFloat(propertyName, 0f);
                if (propertyName.IndexOf("ColorMask", StringComparison.OrdinalIgnoreCase) < 0)
                    hasDirectEmissionMagnitude = true;
            }

            // Standard Lite exposes only an emission color, with no strength control. Black is its neutral value;
            // the emission map remains assigned so the user can restore a color during manual polish.
            if (!hasDirectEmissionMagnitude &&
                HasShaderPropertyType(target, "_EmissionColor", ShaderPropertyType.Color))
                target.SetColor("_EmissionColor", Color.black);
        }

        private static bool IsEmissionMagnitudeProperty(string propertyName)
        {
            return propertyName.IndexOf("Strength", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   propertyName.IndexOf("Intensity", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   propertyName.IndexOf("Power", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   propertyName.IndexOf("Multiplier", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   propertyName.IndexOf("Enable", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Dictionary<MaterialConversionChoice, string> BuildMaterialPaths(string root,
            IReadOnlyCollection<MaterialConversionChoice> choices)
        {
            var baseNames = choices.ToDictionary(choice => choice,
                choice => MeshAnalysisUtility.SanitizeFileName(choice.SourceMaterial.name));
            var duplicateNames = new HashSet<string>(baseNames.Values
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key), StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<MaterialConversionChoice, string>();
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var choice in choices)
            {
                var baseName = baseNames[choice];
                var stem = duplicateNames.Contains(baseName)
                    ? baseName + "_" + StableMaterialIdentity(choice)
                    : baseName;
                var path = root + "/" + stem + ".mat";
                if (!usedPaths.Add(path))
                {
                    stem = baseName + "_" + StableMaterialIdentity(choice);
                    path = root + "/" + stem + ".mat";
                    if (!usedPaths.Add(path))
                        throw new InvalidOperationException("Could not create a unique generated material path for " +
                                                            choice.SourceMaterial.name + ".");
                }
                result.Add(choice, path);
            }
            return result;
        }

        private static string StableMaterialIdentity(MaterialConversionChoice choice)
        {
            if (!string.IsNullOrEmpty(choice.SourceGuid))
                return choice.SourceGuid.Substring(0, Math.Min(8, choice.SourceGuid.Length)) + "_" +
                       choice.SourceLocalFileId;
            if (!string.IsNullOrEmpty(choice.SourceSignature))
                return choice.SourceSignature.Substring(0, Math.Min(10, choice.SourceSignature.Length));
            return Math.Abs(choice.SourceMaterial.GetInstanceID()).ToString();
        }

        private static Material LoadOrMigrateGeneratedMaterial(MaterialConversionChoice choice, string outputPath,
            string materialsRoot)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(outputPath);
            if (existing != null) return existing;

            var previous = choice.GeneratedMaterial;
            var previousPath = previous != null ? AssetDatabase.GetAssetPath(previous) : string.Empty;
            if (string.IsNullOrEmpty(previousPath) ||
                string.Equals(previousPath, outputPath, StringComparison.OrdinalIgnoreCase) ||
                !IsOwnedGeneratedMaterialPath(previousPath, materialsRoot))
                return null;

            var moveError = AssetDatabase.MoveAsset(previousPath, outputPath);
            if (!string.IsNullOrEmpty(moveError))
                throw new InvalidOperationException("Could not migrate generated material '" + previousPath +
                                                    "' to its clean name: " + moveError);
            return AssetDatabase.LoadAssetAtPath<Material>(outputPath);
        }

        private static bool IsOwnedGeneratedMaterialPath(string assetPath, string materialsRoot)
        {
            var normalizedRoot = materialsRoot.TrimEnd('/') + "/";
            return assetPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                   assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);
        }

        private static Texture FirstTexture(Material material, IEnumerable<string> names)
        {
            return FirstTexture(material, names, out _);
        }

        private static Texture FirstMeaningfulTexture(Material material, IEnumerable<string> names)
        {
            return FirstMeaningfulTexture(material, names, out _);
        }

        private static Texture FirstMeaningfulTexture(Material material, IEnumerable<string> names,
            out string propertyName)
        {
            foreach (var name in names)
            {
                if (!HasShaderPropertyType(material, name, ShaderPropertyType.Texture)) continue;
                var texture = material.GetTexture(name);
                if (texture == null || IsDiscardedPoiyomiDefaultMainTexture(texture)) continue;
                propertyName = name;
                return texture;
            }
            propertyName = string.Empty;
            return null;
        }

        private static bool ReferencesDiscardedPoiyomiDefaultMainTexture(Material material)
        {
            var shader = material != null ? material.shader : null;
            if (shader == null) return false;
            for (var index = 0; index < shader.GetPropertyCount(); index++)
            {
                if (shader.GetPropertyType(index) != ShaderPropertyType.Texture) continue;
                var texture = material.GetTexture(shader.GetPropertyName(index));
                if (IsDiscardedPoiyomiDefaultMainTexture(texture)) return true;
            }
            return false;
        }

        private static bool IsDiscardedPoiyomiDefaultMainTexture(Texture texture)
        {
            if (texture == null || !string.Equals(texture.name, DiscardedPoiyomiDefaultMainTextureName,
                    StringComparison.OrdinalIgnoreCase)) return false;
            var path = AssetDatabase.GetAssetPath(texture).Replace('\\', '/');
            return path.IndexOf("PoiyomiShaders/Textures/Defaults/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Texture FirstTexture(Material material, IEnumerable<string> names, out string propertyName)
        {
            foreach (var name in names)
            {
                if (!HasShaderPropertyType(material, name, ShaderPropertyType.Texture)) continue;
                var texture = material.GetTexture(name);
                if (texture == null) continue;
                propertyName = name;
                return texture;
            }
            propertyName = string.Empty;
            return null;
        }

        private static Color FirstColor(Material material, Color fallback, params string[] names)
        {
            foreach (var name in names)
                if (HasShaderPropertyType(material, name, ShaderPropertyType.Color)) return material.GetColor(name);
            return fallback;
        }

        private static float FirstFloat(Material material, float fallback, params string[] names)
        {
            foreach (var name in names)
                if (HasNumericShaderProperty(material, name)) return material.GetFloat(name);
            return fallback;
        }

        private static bool HasNonZeroColor(Material material, params string[] names)
        {
            foreach (var name in names)
                if (HasShaderPropertyType(material, name, ShaderPropertyType.Color) &&
                    material.GetColor(name).maxColorComponent > 0.001f) return true;
            return false;
        }

        private static bool HasNonZeroFloat(Material material, params string[] names)
        {
            foreach (var name in names)
                if (HasNumericShaderProperty(material, name) && Mathf.Abs(material.GetFloat(name)) > 0.001f) return true;
            return false;
        }

        private static void SetTexture(Material material, string property, Texture value)
        {
            if (HasShaderPropertyType(material, property, ShaderPropertyType.Texture)) material.SetTexture(property, value);
        }

        private static void SetColor(Material material, string property, Color value)
        {
            if (HasShaderPropertyType(material, property, ShaderPropertyType.Color)) material.SetColor(property, value);
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (HasNumericShaderProperty(material, property)) material.SetFloat(property, value);
        }

        private static bool HasNumericShaderProperty(Material material, string property)
        {
            return HasShaderPropertyType(material, property, ShaderPropertyType.Float) ||
                   HasShaderPropertyType(material, property, ShaderPropertyType.Range) ||
                   HasShaderPropertyType(material, property, ShaderPropertyType.Int);
        }

        private static bool HasShaderPropertyType(Material material, string property, ShaderPropertyType expectedType)
        {
            var shader = material != null ? material.shader : null;
            if (shader == null) return false;
            var index = shader.FindPropertyIndex(property);
            return index >= 0 && shader.GetPropertyType(index) == expectedType;
        }

        private static void SetKeyword(Material material, string keyword, bool enabled)
        {
            if (enabled) material.EnableKeyword(keyword); else material.DisableKeyword(keyword);
        }

        private static void WriteAssetText(string assetPath, string contents)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            var absolute = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute) ?? projectRoot);
            File.WriteAllText(absolute, contents);
        }
    }
}
