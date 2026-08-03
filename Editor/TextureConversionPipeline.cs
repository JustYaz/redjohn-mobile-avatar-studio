using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MobileAvatarStudio.Editor
{
    internal static class TextureConversionPipeline
    {
        private const string AndroidPlatform = "Android";
        private const string IosPlatform = "iPhone";

        public static void Analyze(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            var generatedMaterials = recipe.MaterialChoices
                .Where(choice => choice.RendererPaths.Any(path => IsRetainedMobileMaterialPath(recipe, path)))
                .Select(choice => choice.GeneratedMaterial)
                .Where(material => material != null)
                .Distinct()
                .ToArray();
            if (generatedMaterials.Length == 0)
                throw new InvalidOperationException("Build the isolated Quest material draft with at least one retained mobile renderer before scanning textures.");
            AnalyzeMaterials(recipe, generatedMaterials);
        }

        public static void AnalyzeFinalPrefab(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            if (prefab == null)
                throw new InvalidOperationException("Build the combined mobile prefab before scanning final textures.");
            var finalMaterials = MobileMaterialSanitizer.CollectOwnedMaterialAssets(prefab, recipe.OutputRoot);
            if (finalMaterials.Length == 0)
                throw new InvalidOperationException("The combined mobile prefab has no renderer materials to scan.");
            AnalyzeMaterials(recipe, finalMaterials);
        }

        /// <summary>
        /// Counts duplicate isolated texture copies created by the UV-tile splitter.
        /// A duplicate is counted by isolated asset, not by material slot.  This is
        /// deliberately limited to retained UV pieces so excluded mobile content
        /// cannot affect the result.
        /// </summary>
        public static int CountDuplicateUvSplitTextureCopies(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null) return 0;
            return GetRetainedUvTextureGroups(recipe)
                .Sum(group => group.Select(record => record.IsolatedTexture)
                    .Where(texture => texture != null)
                    .Distinct()
                    .Skip(1)
                    .Count());
        }

        /// <summary>
        /// Reuses one owned isolated texture asset for UV pieces that originated
        /// from the same source texture.  Materials remain separate because they
        /// can have different shader values, animated bindings, and toggle
        /// domains.  Only texture assets/references are deduplicated here.
        /// </summary>
        public static int ShareDuplicateUvSplitTextures(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));

            var redirected = 0;
            var deleted = 0;
            foreach (var group in GetRetainedUvTextureGroups(recipe))
            {
                var records = group.Where(record => record.IsolatedTexture != null).ToArray();
                if (records.Length < 2) continue;
                var canonical = records[0].IsolatedTexture;
                var duplicates = records.Select(record => record.IsolatedTexture)
                    .Where(texture => texture != null && texture != canonical)
                    .Distinct()
                    .ToArray();
                if (duplicates.Length == 0) continue;

                foreach (var duplicate in duplicates)
                {
                    ReplaceOwnedMaterialTextureReferences(recipe, duplicate, canonical);
                    foreach (var record in records.Where(record => record.IsolatedTexture == duplicate))
                    {
                        record.IsolatedTexture = canonical;
                        redirected++;
                    }

                    var duplicatePath = AssetDatabase.GetAssetPath(duplicate);
                    if (IsOwnedUvSplitTexturePath(recipe, duplicatePath) &&
                        AssetDatabase.DeleteAsset(duplicatePath))
                        deleted++;
                }
            }

            if (redirected == 0) return 0;
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Rebuild the Stage 4 list so approvals, roles, bindings, and memory
            // estimates describe the shared assets rather than deleted copies.
            Analyze(recipe);
            Debug.Log($"Mobile Avatar Studio shared {redirected} UV texture references and removed {deleted} owned duplicate asset(s).");
            return redirected;
        }

        private static IEnumerable<IGrouping<Texture2D, UvTileTextureAsset>> GetRetainedUvTextureGroups(
            MobileAvatarMeshRecipe recipe)
        {
            return UvTileSplitPipeline.GetRetainedPieces(recipe)
                .SelectMany(piece => piece.Textures)
                .Where(record => record != null && record.SourceTexture != null && record.IsolatedTexture != null)
                .GroupBy(record => record.SourceTexture);
        }

        private static bool IsOwnedUvSplitTexturePath(MobileAvatarMeshRecipe recipe, string path)
        {
            if (recipe == null || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(recipe.OutputRoot)) return false;
            var root = recipe.OutputRoot.TrimEnd('/') + "/UVTileSplit/Textures/";
            return path.StartsWith(root, StringComparison.Ordinal);
        }

        private static bool IsOwnedGeneratedAsset(MobileAvatarMeshRecipe recipe, UnityEngine.Object asset)
        {
            if (recipe == null || asset == null || string.IsNullOrEmpty(recipe.OutputRoot)) return false;
            var path = AssetDatabase.GetAssetPath(asset);
            return !string.IsNullOrEmpty(path) &&
                   path.StartsWith(recipe.OutputRoot.TrimEnd('/') + "/", StringComparison.Ordinal);
        }

        private static void ReplaceOwnedMaterialTextureReferences(
            MobileAvatarMeshRecipe recipe, Texture2D duplicate, Texture2D canonical)
        {
            if (duplicate == null || canonical == null) return;
            var materials = new HashSet<Material>();
            foreach (var choice in recipe.MaterialChoices)
            {
                if (IsOwnedGeneratedAsset(recipe, choice.SourceMaterial)) materials.Add(choice.SourceMaterial);
                if (IsOwnedGeneratedAsset(recipe, choice.GeneratedMaterial)) materials.Add(choice.GeneratedMaterial);
            }
            foreach (var piece in UvTileSplitPipeline.GetRetainedPieces(recipe))
                if (IsOwnedGeneratedAsset(recipe, piece.IsolatedSourceMaterial))
                    materials.Add(piece.IsolatedSourceMaterial);

            foreach (var material in materials)
            foreach (var property in material.GetTexturePropertyNames())
                if (material.GetTexture(property) == duplicate)
                    material.SetTexture(property, canonical);

            foreach (var choice in recipe.TextureChoices)
            foreach (var binding in choice.Bindings)
            {
                var material = binding.TargetMaterial;
                if (!IsOwnedGeneratedAsset(recipe, material) || !material.HasProperty(binding.PropertyName)) continue;
                if (material.GetTexture(binding.PropertyName) == duplicate)
                    material.SetTexture(binding.PropertyName, canonical);
            }
        }

        private static void AnalyzeMaterials(MobileAvatarMeshRecipe recipe, IEnumerable<Material> materials)
        {
            var scannedMaterials = materials.Where(material => material != null).Distinct().ToArray();

            var previous = recipe.TextureChoices
                .Where(choice => choice.SourceTexture != null)
                .GroupBy(choice => choice.SourceTexture)
                .ToDictionary(group => group.Key, group => group.First());
            var previousByAssignedTexture = new Dictionary<Texture2D, TextureConversionChoice>();
            foreach (var old in previous.Values)
            {
                previousByAssignedTexture[old.SourceTexture] = old;
                if (old.GeneratedTexture != null) previousByAssignedTexture[old.GeneratedTexture] = old;
            }
            var scanned = new Dictionary<Texture2D, TextureConversionChoice>();

            foreach (var material in scannedMaterials)
            {
                var shader = material.shader;
                if (shader == null) continue;
                for (var index = 0; index < shader.GetPropertyCount(); index++)
                {
                    if (shader.GetPropertyType(index) != ShaderPropertyType.Texture) continue;
                    var property = shader.GetPropertyName(index);
                    var assignedTexture = material.GetTexture(property) as Texture2D;
                    if (assignedTexture == null) continue;
                    previousByAssignedTexture.TryGetValue(assignedTexture, out var previousChoice);
                    var texture = previousChoice?.SourceTexture ?? assignedTexture;
                    var path = AssetDatabase.GetAssetPath(texture);
                    if (string.IsNullOrEmpty(path)) continue; // Built-in white/black/normal defaults need no override.

                    if (!scanned.TryGetValue(texture, out var choice))
                    {
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out string guid, out long localId);
                        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                        var direct = importer != null && path.StartsWith("Assets/", StringComparison.Ordinal);
                        choice = new TextureConversionChoice
                        {
                            SourceTexture = texture,
                            SourceAssetPath = path,
                            SourceGuid = guid,
                            SourceLocalFileId = localId,
                            SourceSignature = ComputeSourceSignature(texture, path, guid, localId),
                            SourceWidth = texture.width,
                            SourceHeight = texture.height,
                            EmbeddedSource = !direct,
                            Notes = direct
                                ? "The source asset will be reused. Only its Android/iOS platform overrides will change; PC settings remain untouched."
                                : "This package-owned or embedded texture will receive a deterministic generated copy used only by the isolated Quest materials."
                        };

                        var old = previousChoice;
                        if (old == null) previous.TryGetValue(texture, out old);
                        if (old != null)
                        {
                            choice.TargetMaxSize = old.TargetMaxSize;
                            choice.Compression = old.Compression;
                            CopySnapshot(old, choice);
                            if (old.IsCurrentSettingsApproved &&
                                string.Equals(old.SourceSignature, choice.SourceSignature, StringComparison.Ordinal))
                                choice.ApproveCurrentSettings();
                        }
                        else
                        {
                            choice.TargetMaxSize = RecommendedMaxSize(MobileTextureCategory.Other,
                                texture.width, texture.height);
                        }
                        scanned.Add(texture, choice);
                    }

                    choice.Roles |= ClassifyRole(property);
                    if (!choice.Bindings.Any(binding => binding.TargetMaterial == material &&
                                                       string.Equals(binding.PropertyName, property, StringComparison.Ordinal)))
                        choice.Bindings.Add(new TextureMaterialBinding
                        {
                            TargetMaterial = material,
                            PropertyName = property
                        });
                }
            }

            recipe.TextureChoices.Clear();
            foreach (var choice in scanned.Values.OrderBy(item => item.SourceAssetPath, StringComparer.Ordinal)
                         .ThenBy(item => item.SourceTexture.name, StringComparer.OrdinalIgnoreCase))
            {
                if (!previous.ContainsKey(choice.SourceTexture))
                {
                    var category = GetCategory(choice);
                    choice.TargetMaxSize = RecommendedMaxSize(category, choice.SourceWidth, choice.SourceHeight);
                    choice.Compression = RecommendedCompression(category);
                }
                if (!choice.EmbeddedSource)
                {
                    if (!choice.AndroidSnapshotCaptured) CaptureAndroidSnapshot(choice);
                    if (!choice.IosSnapshotCaptured) CaptureIosSnapshot(choice);
                }
                recipe.TextureChoices.Add(choice);
            }

            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
        }

        private static bool IsRetainedMobileMaterialPath(MobileAvatarMeshRecipe recipe, string path)
        {
            if (recipe == null || string.IsNullOrEmpty(path)) return false;

            // Exclusions win even when the path is an ancestor of a generated UV piece.
            if (recipe.RendererChoices.Any(choice => choice.IsExcludedFromMobile &&
                (string.Equals(choice.TransformPath, path, StringComparison.Ordinal) ||
                 path.StartsWith(choice.TransformPath + "/", StringComparison.Ordinal))))
                return false;

            var split = UvTileSplitPipeline.FindEnabledSplit(recipe, path);
            if (split != null) return split.Pieces.Any(piece => piece.KeepOnMobile);
            if (UvTileSplitPipeline.IsGeneratedPiecePath(recipe, path)) return true;

            // Ordinary material usage must belong to a renderer still carried to mobile.
            return recipe.RendererChoices.Any(choice => !choice.IsExcludedFromMobile &&
                string.Equals(choice.TransformPath, path, StringComparison.Ordinal));
        }

        public static void ApplyMobileOverrides(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            if (recipe.TextureChoices.Count == 0) throw new InvalidOperationException("Scan textures first.");
            var unapproved = recipe.TextureChoices.Where(choice => !choice.IsCurrentSettingsApproved).ToArray();
            if (unapproved.Length > 0)
                throw new InvalidOperationException($"Approve every texture setting before applying. {unapproved.Length} remain unapproved.");

            var report = new StringBuilder();
            report.AppendLine("MOBILE AVATAR STUDIO - SHARED TEXTURE ANDROID/IOS OVERRIDES");
            report.AppendLine("Generated: " + DateTime.UtcNow.ToString("O"));
            report.AppendLine("Policy: reuse project texture assets with Android/iOS-only overrides; copy package-owned or embedded textures into the isolated workspace; preserve PC settings and source package assets.");
            long estimatedBytes = 0;

            try
            {
                foreach (var choice in recipe.TextureChoices) ValidateSource(choice);
                foreach (var choice in recipe.TextureChoices.Where(item => item.EmbeddedSource))
                {
                    var generated = CreateOrUpdateGeneratedCopy(recipe, choice);
                    RemapBindings(choice, generated);
                }
                AssetDatabase.SaveAssets();

                for (var index = 0; index < recipe.TextureChoices.Count; index++)
                {
                    var choice = recipe.TextureChoices[index];
                    EditorUtility.DisplayProgressBar("Mobile Avatar Studio",
                        "Applying Android/iOS overrides: " + choice.SourceTexture.name,
                        index / (float)Math.Max(1, recipe.TextureChoices.Count));
                    var appliedPath = choice.EmbeddedSource ? choice.GeneratedAssetPath : choice.SourceAssetPath;
                    if (!choice.EmbeddedSource)
                    {
                        if (!choice.AndroidSnapshotCaptured) CaptureAndroidSnapshot(choice);
                        if (!choice.IosSnapshotCaptured) CaptureIosSnapshot(choice);
                    }
                    var importer = AssetImporter.GetAtPath(appliedPath) as TextureImporter;
                    if (importer == null) throw new InvalidOperationException("No TextureImporter for " + appliedPath);
                    ApplyMobileSettings(importer, choice);
                    ValidatePlatformSettings(importer, choice, AndroidPlatform);
                    ValidatePlatformSettings(importer, choice, IosPlatform);
                    choice.AndroidOverrideApplied = true;
                    choice.IosOverrideApplied = true;
                    var bytes = EstimateMobileBytes(choice);
                    estimatedBytes += bytes;
                    report.AppendLine($"TEXTURE {choice.SourceTexture.name} | {choice.SourceWidth}x{choice.SourceHeight} -> max {choice.TargetMaxSize} | " +
                                      $"{choice.Compression} | roles={choice.Roles} | estimatedMemory={FormatBytes(bytes)} | " +
                                      $"source={choice.SourceAssetPath} | applied={appliedPath} | generatedCopy={choice.EmbeddedSource}");
                }

                recipe.TexturePassUtc = DateTime.UtcNow.ToString("O");
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();
                report.AppendLine("ESTIMATED TEXTURE MEMORY PER MOBILE PLATFORM " + FormatBytes(estimatedBytes));
                report.AppendLine("STATUS ESTIMATE ONLY - actual Android and iOS builds are required for upload and uncompressed size validation.");
                WriteReport(recipe, report.ToString());
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        public static bool ValidateAppliedMobileOverrides(MobileAvatarMeshRecipe recipe, out string reason)
        {
            if (recipe == null || recipe.TextureChoices.Count == 0)
            {
                reason = "Run the final material texture scan first.";
                return false;
            }

            foreach (var choice in recipe.TextureChoices)
            {
                if (!choice.IsCurrentSettingsApproved)
                {
                    reason = $"Approve the mobile texture setting for {choice.SourceTexture?.name ?? choice.SourceAssetPath}.";
                    return false;
                }
                if (!choice.MobileOverridesApplied)
                {
                    reason = $"Apply the Android/iOS texture override for {choice.SourceTexture?.name ?? choice.SourceAssetPath}.";
                    return false;
                }

                try
                {
                    ValidateSource(choice);
                    var appliedPath = choice.EmbeddedSource ? choice.GeneratedAssetPath : choice.SourceAssetPath;
                    var importer = AssetImporter.GetAtPath(appliedPath) as TextureImporter;
                    if (importer == null)
                    {
                        reason = "No TextureImporter exists for " + appliedPath + ".";
                        return false;
                    }
                    ValidatePlatformSettings(importer, choice, AndroidPlatform);
                    ValidatePlatformSettings(importer, choice, IosPlatform);
                }
                catch (Exception exception)
                {
                    reason = exception.Message;
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        public static void RestoreMobileOverrides(MobileAvatarMeshRecipe recipe, bool writeReport = true)
        {
            if (recipe == null) return;
            foreach (var choice in recipe.TextureChoices.Where(item => !item.EmbeddedSource &&
                                                                        (item.AndroidSnapshotCaptured || item.IosSnapshotCaptured)))
            {
                var importer = AssetImporter.GetAtPath(choice.SourceAssetPath) as TextureImporter;
                if (importer == null) continue;
                RestoreAndroidSnapshot(importer, choice);
                RestoreIosSnapshot(importer, choice);
                importer.SaveAndReimport();
                choice.AndroidOverrideApplied = false;
                choice.IosOverrideApplied = false;
            }
            foreach (var choice in recipe.TextureChoices.Where(item => item.EmbeddedSource))
            {
                RemapBindings(choice, choice.SourceTexture);
                var importer = AssetImporter.GetAtPath(choice.GeneratedAssetPath) as TextureImporter;
                if (importer != null)
                {
                    importer.ClearPlatformTextureSettings(AndroidPlatform);
                    importer.ClearPlatformTextureSettings(IosPlatform);
                    importer.SaveAndReimport();
                }
                choice.AndroidOverrideApplied = false;
                choice.IosOverrideApplied = false;
            }
            recipe.TexturePassUtc = string.Empty;
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
            if (writeReport) WriteReport(recipe, "Previous Android/iOS texture platform settings restored at " + DateTime.UtcNow.ToString("O"));
        }

        public static long EstimateMobileBytes(TextureConversionChoice choice)
        {
            var scale = Math.Min(1f, choice.TargetMaxSize / (float)Math.Max(choice.SourceWidth, choice.SourceHeight));
            var width = Math.Max(1, Mathf.RoundToInt(choice.SourceWidth * scale));
            var height = Math.Max(1, Mathf.RoundToInt(choice.SourceHeight * scale));
            var block = choice.Compression == MobileTextureCompression.ASTC6x6 ? 6 : 8;
            var blocksX = (width + block - 1) / block;
            var blocksY = (height + block - 1) / block;
            return (long)Math.Ceiling(blocksX * blocksY * 16d * 4d / 3d); // Includes an estimated full mip chain.
        }

        public static string FormatBytes(long bytes)
        {
            return bytes >= 1024L * 1024L
                ? (bytes / (1024d * 1024d)).ToString("0.00") + " MiB"
                : (bytes / 1024d).ToString("0.0") + " KiB";
        }

        private static void CaptureAndroidSnapshot(TextureConversionChoice choice)
        {
            var importer = AssetImporter.GetAtPath(choice.SourceAssetPath) as TextureImporter;
            if (importer == null) return;
            var settings = importer.GetPlatformTextureSettings(AndroidPlatform);
            choice.AndroidSnapshotCaptured = true;
            choice.OriginalAndroidOverridden = settings.overridden;
            choice.OriginalAndroidMaxSize = settings.maxTextureSize;
            choice.OriginalAndroidFormat = (int)settings.format;
            choice.OriginalAndroidCompression = (int)settings.textureCompression;
            choice.OriginalAndroidCompressionQuality = settings.compressionQuality;
            choice.OriginalAndroidCrunchedCompression = settings.crunchedCompression;
            choice.OriginalAndroidAllowsAlphaSplitting = settings.allowsAlphaSplitting;
            choice.OriginalAndroidEtc2Fallback = (int)settings.androidETC2FallbackOverride;
        }

        private static void CaptureIosSnapshot(TextureConversionChoice choice)
        {
            var importer = AssetImporter.GetAtPath(choice.SourceAssetPath) as TextureImporter;
            if (importer == null) return;
            var settings = importer.GetPlatformTextureSettings(IosPlatform);
            choice.IosSnapshotCaptured = true;
            choice.OriginalIosOverridden = settings.overridden;
            choice.OriginalIosMaxSize = settings.maxTextureSize;
            choice.OriginalIosFormat = (int)settings.format;
            choice.OriginalIosCompression = (int)settings.textureCompression;
            choice.OriginalIosCompressionQuality = settings.compressionQuality;
            choice.OriginalIosCrunchedCompression = settings.crunchedCompression;
            choice.OriginalIosAllowsAlphaSplitting = settings.allowsAlphaSplitting;
            choice.OriginalIosEtc2Fallback = (int)settings.androidETC2FallbackOverride;
        }

        private static void CopySnapshot(TextureConversionChoice source, TextureConversionChoice destination)
        {
            destination.AndroidSnapshotCaptured = source.AndroidSnapshotCaptured;
            destination.OriginalAndroidOverridden = source.OriginalAndroidOverridden;
            destination.OriginalAndroidMaxSize = source.OriginalAndroidMaxSize;
            destination.OriginalAndroidFormat = source.OriginalAndroidFormat;
            destination.OriginalAndroidCompression = source.OriginalAndroidCompression;
            destination.OriginalAndroidCompressionQuality = source.OriginalAndroidCompressionQuality;
            destination.OriginalAndroidCrunchedCompression = source.OriginalAndroidCrunchedCompression;
            destination.OriginalAndroidAllowsAlphaSplitting = source.OriginalAndroidAllowsAlphaSplitting;
            destination.OriginalAndroidEtc2Fallback = source.OriginalAndroidEtc2Fallback;
            destination.AndroidOverrideApplied = source.AndroidOverrideApplied;
            destination.IosSnapshotCaptured = source.IosSnapshotCaptured;
            destination.OriginalIosOverridden = source.OriginalIosOverridden;
            destination.OriginalIosMaxSize = source.OriginalIosMaxSize;
            destination.OriginalIosFormat = source.OriginalIosFormat;
            destination.OriginalIosCompression = source.OriginalIosCompression;
            destination.OriginalIosCompressionQuality = source.OriginalIosCompressionQuality;
            destination.OriginalIosCrunchedCompression = source.OriginalIosCrunchedCompression;
            destination.OriginalIosAllowsAlphaSplitting = source.OriginalIosAllowsAlphaSplitting;
            destination.OriginalIosEtc2Fallback = source.OriginalIosEtc2Fallback;
            destination.IosOverrideApplied = source.IosOverrideApplied;
            destination.GeneratedTexture = source.GeneratedTexture;
            destination.GeneratedAssetPath = source.GeneratedAssetPath;
        }

        private static Texture2D CreateOrUpdateGeneratedCopy(MobileAvatarMeshRecipe recipe, TextureConversionChoice choice)
        {
            var folder = recipe.OutputRoot + "/Textures/GeneratedCopies";
            MeshAnalysisUtility.EnsureAssetFolder(folder);
            var sourceExtension = Path.GetExtension(choice.SourceAssetPath);
            var canCopyFile = AssetImporter.GetAtPath(choice.SourceAssetPath) is TextureImporter &&
                              IsImageExtension(sourceExtension) && SourceFileExists(choice.SourceAssetPath);
            var extension = canCopyFile ? sourceExtension.ToLowerInvariant() : ".png";
            var identity = !string.IsNullOrEmpty(choice.SourceGuid) ? choice.SourceGuid : choice.SourceSignature;
            var suffix = string.IsNullOrEmpty(identity) ? "texture" : identity.Substring(0, Math.Min(8, identity.Length));
            var fileName = MeshAnalysisUtility.SanitizeFileName(choice.SourceTexture.name) + "_" + suffix + extension;
            var targetPath = folder + "/" + fileName;

            if (canCopyFile)
                CopySourceFilePreservingGeneratedMeta(choice.SourceAssetPath, targetPath);
            else
                BakeTextureToPng(choice.SourceTexture, targetPath);

            var generated = AssetDatabase.LoadAssetAtPath<Texture2D>(targetPath);
            if (generated == null) throw new InvalidOperationException("Generated texture copy could not be loaded: " + targetPath);
            var importer = AssetImporter.GetAtPath(targetPath) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Generated texture copy has no TextureImporter: " + targetPath);
            ConfigureGeneratedImporter(choice, importer, canCopyFile);
            choice.GeneratedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(targetPath);
            choice.GeneratedAssetPath = targetPath;
            choice.Notes = "A deterministic generated copy is used by the isolated Quest materials. The source package or embedded texture is not modified.";
            return choice.GeneratedTexture;
        }

        private static void CopySourceFilePreservingGeneratedMeta(string sourcePath, string targetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                              ?? throw new InvalidOperationException("Unity project root could not be resolved.");
            var sourceAbsolute = Path.GetFullPath(Path.Combine(projectRoot, sourcePath.Replace('/', Path.DirectorySeparatorChar)));
            var targetAbsolute = Path.GetFullPath(Path.Combine(projectRoot, targetPath.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(targetAbsolute) ?? projectRoot);
            File.Copy(sourceAbsolute, targetAbsolute, true);
            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        private static void BakeTextureToPng(Texture2D source, string targetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                              ?? throw new InvalidOperationException("Unity project root could not be resolved.");
            var targetAbsolute = Path.GetFullPath(Path.Combine(projectRoot, targetPath.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(targetAbsolute) ?? projectRoot);
            var temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            var previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                readable.Apply(false, false);
                File.WriteAllBytes(targetAbsolute, readable.EncodeToPNG());
            }
            finally
            {
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
            }
            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        private static void ConfigureGeneratedImporter(TextureConversionChoice choice, TextureImporter importer, bool copiedOriginalFile)
        {
            var sourceImporter = AssetImporter.GetAtPath(choice.SourceAssetPath) as TextureImporter;
            if (copiedOriginalFile && sourceImporter != null)
            {
                importer.textureType = sourceImporter.textureType;
                importer.textureShape = sourceImporter.textureShape;
                importer.sRGBTexture = sourceImporter.sRGBTexture;
                importer.alphaSource = sourceImporter.alphaSource;
                importer.alphaIsTransparency = sourceImporter.alphaIsTransparency;
                importer.mipmapEnabled = sourceImporter.mipmapEnabled;
                importer.wrapModeU = sourceImporter.wrapModeU;
                importer.wrapModeV = sourceImporter.wrapModeV;
                importer.wrapModeW = sourceImporter.wrapModeW;
                importer.filterMode = sourceImporter.filterMode;
                importer.anisoLevel = sourceImporter.anisoLevel;
                importer.npotScale = sourceImporter.npotScale;
                importer.maxTextureSize = sourceImporter.maxTextureSize;
                importer.textureCompression = sourceImporter.textureCompression;
                importer.compressionQuality = sourceImporter.compressionQuality;
                importer.crunchedCompression = sourceImporter.crunchedCompression;
            }
            else
            {
                importer.textureType = GetCategory(choice) == MobileTextureCategory.Normal
                    ? TextureImporterType.NormalMap
                    : TextureImporterType.Default;
                importer.sRGBTexture = GetCategory(choice) == MobileTextureCategory.BaseColor ||
                                       GetCategory(choice) == MobileTextureCategory.Emission ||
                                       GetCategory(choice) == MobileTextureCategory.Matcap;
                importer.mipmapEnabled = true;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
            }
            importer.SaveAndReimport();
        }

        private static void ApplyMobileSettings(TextureImporter importer, TextureConversionChoice choice)
        {
            ApplyPlatformSettings(importer, choice, AndroidPlatform);
            ApplyPlatformSettings(importer, choice, IosPlatform);
            importer.SaveAndReimport();
        }

        private static void ApplyPlatformSettings(TextureImporter importer, TextureConversionChoice choice, string platform)
        {
            var settings = importer.GetPlatformTextureSettings(platform);
            settings.name = platform;
            settings.overridden = true;
            settings.maxTextureSize = choice.TargetMaxSize;
            settings.format = choice.Compression == MobileTextureCompression.ASTC6x6
                ? TextureImporterFormat.ASTC_6x6
                : TextureImporterFormat.ASTC_8x8;
            settings.textureCompression = TextureImporterCompression.CompressedHQ;
            settings.compressionQuality = 50;
            settings.crunchedCompression = false;
            importer.SetPlatformTextureSettings(settings);
        }

        private static void ValidatePlatformSettings(TextureImporter importer, TextureConversionChoice choice, string platform)
        {
            var settings = importer.GetPlatformTextureSettings(platform);
            var expectedFormat = choice.Compression == MobileTextureCompression.ASTC6x6
                ? TextureImporterFormat.ASTC_6x6
                : TextureImporterFormat.ASTC_8x8;
            if (!settings.overridden || settings.maxTextureSize != choice.TargetMaxSize || settings.format != expectedFormat)
                throw new InvalidOperationException($"{platform} override verification failed for {choice.SourceTexture.name}: " +
                                                    $"override={settings.overridden}, max={settings.maxTextureSize}, format={settings.format}.");
        }

        private static void RestoreAndroidSnapshot(TextureImporter importer, TextureConversionChoice choice)
        {
            if (!choice.AndroidSnapshotCaptured) return;
            if (!choice.OriginalAndroidOverridden)
            {
                importer.ClearPlatformTextureSettings(AndroidPlatform);
                return;
            }
            RestorePlatformSettings(importer, AndroidPlatform, choice.OriginalAndroidMaxSize,
                choice.OriginalAndroidFormat, choice.OriginalAndroidCompression,
                choice.OriginalAndroidCompressionQuality, choice.OriginalAndroidCrunchedCompression,
                choice.OriginalAndroidAllowsAlphaSplitting, choice.OriginalAndroidEtc2Fallback);
        }

        private static void RestoreIosSnapshot(TextureImporter importer, TextureConversionChoice choice)
        {
            if (!choice.IosSnapshotCaptured) return;
            if (!choice.OriginalIosOverridden)
            {
                importer.ClearPlatformTextureSettings(IosPlatform);
                return;
            }
            RestorePlatformSettings(importer, IosPlatform, choice.OriginalIosMaxSize,
                choice.OriginalIosFormat, choice.OriginalIosCompression,
                choice.OriginalIosCompressionQuality, choice.OriginalIosCrunchedCompression,
                choice.OriginalIosAllowsAlphaSplitting, choice.OriginalIosEtc2Fallback);
        }

        private static void RestorePlatformSettings(TextureImporter importer, string platform, int maxSize,
            int format, int compression, int quality, bool crunched, bool alphaSplitting, int etc2Fallback)
        {
            var settings = importer.GetPlatformTextureSettings(platform);
            settings.name = platform;
            settings.overridden = true;
            settings.maxTextureSize = maxSize;
            settings.format = (TextureImporterFormat)format;
            settings.textureCompression = (TextureImporterCompression)compression;
            settings.compressionQuality = quality;
            settings.crunchedCompression = crunched;
            settings.allowsAlphaSplitting = alphaSplitting;
            settings.androidETC2FallbackOverride = (AndroidETC2FallbackOverride)etc2Fallback;
            importer.SetPlatformTextureSettings(settings);
        }

        private static void RemapBindings(TextureConversionChoice choice, Texture2D texture)
        {
            foreach (var binding in choice.Bindings.Where(item => item.TargetMaterial != null &&
                                                                  !string.IsNullOrEmpty(item.PropertyName)))
            {
                if (!binding.TargetMaterial.HasProperty(binding.PropertyName))
                    throw new InvalidOperationException(binding.TargetMaterial.name + " no longer has texture property " + binding.PropertyName + ". Re-scan textures first.");
                binding.TargetMaterial.SetTexture(binding.PropertyName, texture);
                EditorUtility.SetDirty(binding.TargetMaterial);
                if (binding.TargetMaterial.GetTexture(binding.PropertyName) != texture)
                    throw new InvalidOperationException("Failed to remap " + binding.TargetMaterial.name + "." + binding.PropertyName + ".");
            }
        }

        private static bool SourceFileExists(string assetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            return File.Exists(Path.GetFullPath(Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar))));
        }

        private static bool IsImageExtension(string extension)
        {
            return new[] { ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".bmp", ".psd", ".exr", ".hdr" }
                .Contains((extension ?? string.Empty).ToLowerInvariant());
        }

        private static void ValidateSource(TextureConversionChoice choice)
        {
            if (choice.SourceTexture == null) throw new InvalidOperationException("A source texture is missing.");
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(choice.SourceTexture, out string guid, out long localId);
            var signature = ComputeSourceSignature(choice.SourceTexture, AssetDatabase.GetAssetPath(choice.SourceTexture), guid, localId);
            if (!string.Equals(signature, choice.SourceSignature, StringComparison.Ordinal))
                throw new InvalidOperationException(choice.SourceTexture.name + " changed after approval. Re-scan textures first.");
        }

        private static string ComputeSourceSignature(Texture2D texture, string path, string guid, long localId)
        {
            return MeshAnalysisUtility.ComputeStringSignature(new[]
            {
                guid ?? string.Empty,
                localId.ToString(),
                texture.width.ToString(),
                texture.height.ToString(),
                MeshAnalysisUtility.ComputeAssetHash(path)
            });
        }

        public static MobileTextureCategory GetCategory(TextureConversionChoice choice)
        {
            var roles = choice.Roles;
            var color = (roles & MobileTextureRole.Color) != 0;
            var normal = (roles & MobileTextureRole.Normal) != 0;
            var emission = (roles & MobileTextureRole.Emission) != 0;
            var packed = (roles & (MobileTextureRole.Metallic | MobileTextureRole.Gloss | MobileTextureRole.Mask)) != 0;
            var matcap = (roles & MobileTextureRole.Matcap) != 0;
            var other = (roles & MobileTextureRole.Other) != 0;
            var groups = (color ? 1 : 0) + (normal ? 1 : 0) + (emission ? 1 : 0) +
                         (packed ? 1 : 0) + (matcap ? 1 : 0) + (other ? 1 : 0);
            if (groups > 1) return MobileTextureCategory.MixedReview;
            if (color) return MobileTextureCategory.BaseColor;
            if (normal) return MobileTextureCategory.Normal;
            if (emission) return MobileTextureCategory.Emission;
            if (packed) return MobileTextureCategory.PackedMasks;
            if (matcap) return MobileTextureCategory.Matcap;
            return MobileTextureCategory.Other;
        }

        public static int RecommendedMaxSize(MobileTextureCategory category, int width, int height)
        {
            var largest = Math.Max(width, height);
            var limit = category == MobileTextureCategory.BaseColor ||
                        category == MobileTextureCategory.Normal ||
                        category == MobileTextureCategory.MixedReview
                ? 1024
                : 512;
            var desired = Math.Min(largest, limit);
            if (desired <= 256) return 256;
            if (desired <= 512) return 512;
            return 1024;
        }

        public static MobileTextureCompression RecommendedCompression(MobileTextureCategory category)
        {
            return category == MobileTextureCategory.PackedMasks || category == MobileTextureCategory.Other
                ? MobileTextureCompression.ASTC8x8
                : MobileTextureCompression.ASTC6x6;
        }

        public static string CategoryDisplayName(MobileTextureCategory category)
        {
            switch (category)
            {
                case MobileTextureCategory.BaseColor: return "Base Color / Albedo";
                case MobileTextureCategory.Normal: return "Normal Maps";
                case MobileTextureCategory.Emission: return "Emission Maps";
                case MobileTextureCategory.PackedMasks: return "Packed Masks / Metallic / Gloss / Occlusion";
                case MobileTextureCategory.Matcap: return "Matcaps";
                case MobileTextureCategory.MixedReview: return "Mixed Use - Review Required";
                default: return "Other / Ramps / Detail";
            }
        }

        private static MobileTextureRole ClassifyRole(string property)
        {
            var lower = property.ToLowerInvariant();
            if (lower.Contains("normal") || lower.Contains("bump")) return MobileTextureRole.Normal;
            if (lower.Contains("emission")) return MobileTextureRole.Emission;
            if (lower.Contains("metal")) return MobileTextureRole.Metallic;
            if (lower.Contains("gloss") || lower.Contains("smooth")) return MobileTextureRole.Gloss;
            if (lower.Contains("matcap")) return lower.Contains("mask") ? MobileTextureRole.Mask : MobileTextureRole.Matcap;
            if (lower.Contains("mask") || lower.Contains("occlusion")) return MobileTextureRole.Mask;
            if (lower.Contains("main") || lower.Contains("base") || lower.Contains("albedo")) return MobileTextureRole.Color;
            return MobileTextureRole.Other;
        }

        private static void WriteReport(MobileAvatarMeshRecipe recipe, string text)
        {
            var reportRoot = recipe.OutputRoot + "/Reports";
            MeshAnalysisUtility.EnsureAssetFolder(reportRoot);
            var legacyReport = reportRoot + "/TextureAndroidOverrides.txt";
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(legacyReport) != null)
                AssetDatabase.DeleteAsset(legacyReport);
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            var assetPath = reportRoot + "/TextureMobileOverrides.txt";
            var absolute = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute) ?? projectRoot);
            File.WriteAllText(absolute, text);
            AssetDatabase.Refresh();
        }
    }
}
