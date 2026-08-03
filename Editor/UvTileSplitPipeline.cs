using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MobileAvatarStudio.Editor
{
    /// <summary>
    /// Converts Poiyomi-style 4x4 UV Tile Dissolve domains into ordinary renderer children.
    /// The PC prefab is never modified. Selector UVs only classify geometry; ordinary texture
    /// UVs are retained exactly because a selector channel does not prove a texture's UV source.
    /// </summary>
    internal static class UvTileSplitPipeline
    {
        private const string EnabledProperty = "_UVTileDissolveEnabled";
        private const string DiscardProperty = "_UVTileDissolveDiscardAtMax";
        private const string ChannelProperty = "_UVTileDissolveUV";
        private const float TileEpsilon = 0.0002f;

        private readonly struct PieceKey : IEquatable<PieceKey>
        {
            public PieceKey(int slot, int channel, int column, int row, bool always)
            {
                Slot = slot;
                Channel = channel;
                Column = column;
                Row = row;
                Always = always;
            }

            public int Slot { get; }
            public int Channel { get; }
            public int Column { get; }
            public int Row { get; }
            public bool Always { get; }
            public bool Equals(PieceKey other) => Slot == other.Slot && Channel == other.Channel &&
                                                  Column == other.Column && Row == other.Row && Always == other.Always;
            public override bool Equals(object obj) => obj is PieceKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = Slot;
                    hash = hash * 397 ^ Channel;
                    hash = hash * 397 ^ Column;
                    hash = hash * 397 ^ Row;
                    return hash * 397 ^ (Always ? 1 : 0);
                }
            }
        }

        private sealed class PieceGeometry
        {
            public PieceKey Key;
            public Material Material;
            public readonly List<int> Triangles = new List<int>();
        }

        public static void ScanAndGenerate(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null || recipe.SourcePrefab == null) throw new ArgumentNullException(nameof(recipe));
            MeshCandidatePipeline.EnsureWorkspace(recipe);

            var generatedRoot = recipe.OutputRoot + "/UVTileSplit";
            if (AssetDatabase.IsValidFolder(generatedRoot) && !AssetDatabase.DeleteAsset(generatedRoot))
                throw new InvalidOperationException("Could not replace the owned UV Tile Split workspace: " + generatedRoot);
            MeshAnalysisUtility.EnsureAssetFolder(generatedRoot);
            MeshAnalysisUtility.EnsureAssetFolder(generatedRoot + "/Meshes");
            MeshAnalysisUtility.EnsureAssetFolder(generatedRoot + "/Materials");
            MeshAnalysisUtility.EnsureAssetFolder(generatedRoot + "/Textures");

            var oldKeep = recipe.UvTileSplitRenderers
                .SelectMany(renderer => renderer.Pieces.Select(piece => new
                {
                    Key = renderer.TransformPath + "|" + piece.Id,
                    piece.KeepOnMobile,
                    piece.BehaviorMode
                }))
                .ToDictionary(item => item.Key, item => new KeyValuePair<bool, UvTilePieceBehaviorMode>(
                    item.KeepOnMobile, item.BehaviorMode), StringComparer.Ordinal);
            recipe.UvTileSplitRenderers.Clear();

            var clipLabels = CollectClipLabels(recipe.SourcePrefab);
            var isolatedTextures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            var compatibleCount = 0;
            var pieceCount = 0;

            foreach (var sourceRenderer in recipe.SourcePrefab.GetComponentsInChildren<Renderer>(true)
                         .OrderBy(renderer => AnimationUtility.CalculateTransformPath(
                             renderer.transform, recipe.SourcePrefab.transform), StringComparer.Ordinal))
            {
                if (!TryGetRendererMesh(sourceRenderer, out var sourceMesh, out var skinned) || sourceMesh == null)
                    continue;
                var materials = sourceRenderer.sharedMaterials;
                if (!materials.Any(IsSupportedTileMaterial)) continue;

                var path = AnimationUtility.CalculateTransformPath(sourceRenderer.transform,
                    recipe.SourcePrefab.transform);
                var split = new UvTileSplitRenderer
                {
                    TransformPath = path,
                    DisplayName = string.IsNullOrEmpty(path) ? sourceRenderer.name : path,
                    Skinned = skinned,
                    SourceMeshSignature = MeshAnalysisUtility.ComputeMeshSignature(sourceMesh),
                    Compatible = false,
                    SplitEnabled = false
                };
                recipe.UvTileSplitRenderers.Add(split);

                if (!sourceMesh.isReadable)
                {
                    split.Status = "Blocked: source mesh Read/Write is disabled. The source is untouched; enable Read/Write or provide a readable generated copy.";
                    continue;
                }

                try
                {
                    var geometry = ClassifyGeometry(sourceMesh, materials, out var classificationError);
                    if (!string.IsNullOrEmpty(classificationError))
                    {
                        split.Status = "Blocked: " + classificationError;
                        continue;
                    }
                    if (geometry.Count == 0)
                    {
                        split.Status = "No triangle groups were found.";
                        continue;
                    }

                    var rendererToken = StableToken(path);
                    foreach (var item in geometry.OrderBy(item => item.Key.Slot)
                                 .ThenBy(item => item.Key.Always ? 1 : 0)
                                 .ThenBy(item => item.Key.Row).ThenBy(item => item.Key.Column))
                    {
                        var pieceId = item.Key.Always
                            ? $"slot{item.Key.Slot}_always"
                            : $"slot{item.Key.Slot}_r{item.Key.Row}_c{item.Key.Column}";
                        var mesh = BuildCompactMesh(sourceMesh, item.Triangles,
                            sourceMesh.name + "_" + pieceId);
                        var meshPath = generatedRoot + "/Meshes/" + rendererToken + "_" + pieceId + ".asset";
                        AssetDatabase.CreateAsset(mesh, meshPath);

                        var material = CreateIsolatedMaterial(item.Material, generatedRoot, rendererToken, pieceId,
                            isolatedTextures, out var textureRecords);
                        var property = item.Key.Always
                            ? string.Empty
                            : $"_UVTileDissolveAlpha_Row{item.Key.Row}_{item.Key.Column}";
                        var labels = string.IsNullOrEmpty(property) || !clipLabels.TryGetValue(path + "|" + property,
                            out var names) ? new List<string>() : names;
                        var label = item.Key.Always
                            ? $"{item.Material.name} - always visible geometry"
                            : labels.Count > 0
                                ? $"{item.Material.name} - {string.Join(", ", labels.Take(3))}"
                                : $"{item.Material.name} - Row {item.Key.Row}, Column {item.Key.Column}";
                        var childName = "__MAS_UV_" + rendererToken + "_" + pieceId;
                        var piece = new UvTilePieceChoice
                        {
                            Id = pieceId,
                            DisplayName = label,
                            GeneratedChildName = childName,
                            MaterialSlot = item.Key.Slot,
                            UvChannel = item.Key.Channel,
                            TileColumn = item.Key.Column,
                            TileRow = item.Key.Row,
                            AlwaysVisible = item.Key.Always,
                            KeepOnMobile = !oldKeep.TryGetValue(path + "|" + pieceId, out var oldDecision) || oldDecision.Key,
                            BehaviorMode = oldKeep.TryGetValue(path + "|" + pieceId, out oldDecision)
                                ? oldDecision.Value
                                : UvTilePieceBehaviorMode.FollowSourceToggle,
                            SourceVisibleByDefault = item.Key.Always || !IsTileDiscarded(item.Material, property),
                            SourceMaterial = item.Material,
                            IsolatedSourceMaterial = material
                        };
                        piece.Textures.AddRange(textureRecords);
                        piece.ControllingClips.AddRange(labels);
                        PopulateMeshChoice(piece.MeshChoice, split, piece, mesh, sourceRenderer);
                        split.Pieces.Add(piece);
                        pieceCount++;
                    }

                    split.Compatible = true;
                    split.SplitEnabled = true;
                    split.Status = $"Ready: {split.Pieces.Count} isolated geometry/material piece(s).";
                    compatibleCount++;
                }
                catch (Exception exception)
                {
                    split.Compatible = false;
                    split.SplitEnabled = false;
                    split.Status = "Blocked without changing the source: " + exception.Message;
                }
            }

            recipe.UvTileSplitScanUtc = DateTime.UtcNow.ToString("O");
            recipe.UvTileSplitStatus = recipe.UvTileSplitRenderers.Count == 0
                ? "No compatible UV Tile Dissolve materials were detected."
                : $"Detected {recipe.UvTileSplitRenderers.Count} renderer(s); {compatibleCount} compatible, {pieceCount} pieces generated.";
            recipe.MeshGenerationState = "UV tile split changed; regenerate retained mesh candidates";
            MaterialConversionPipeline.Analyze(recipe);
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        public static IReadOnlyList<RendererMeshChoice> GetEffectiveMeshChoices(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null) return Array.Empty<RendererMeshChoice>();
            var result = new List<RendererMeshChoice>();
            foreach (var choice in recipe.RendererChoices)
            {
                if (choice.IsExcludedFromMobile) continue;
                var split = FindEnabledSplit(recipe, choice.TransformPath);
                if (split == null)
                {
                    result.Add(choice);
                    continue;
                }
                result.AddRange(split.Pieces.Where(piece => piece.KeepOnMobile).Select(piece => piece.MeshChoice));
            }
            return result;
        }

        public static UvTileSplitRenderer FindEnabledSplit(MobileAvatarMeshRecipe recipe, string transformPath)
        {
            return recipe?.UvTileSplitRenderers.FirstOrDefault(split => split.Compatible && split.SplitEnabled &&
                string.Equals(split.TransformPath, transformPath, StringComparison.Ordinal));
        }

        public static bool IsGeneratedPiecePath(MobileAvatarMeshRecipe recipe, string path)
        {
            return recipe != null && recipe.UvTileSplitRenderers.Any(split => split.Compatible && split.SplitEnabled &&
                split.Pieces.Any(piece => piece.KeepOnMobile &&
                    string.Equals(path, BuildChildPath(split, piece), StringComparison.Ordinal)));
        }

        public static string BuildChildPath(UvTileSplitRenderer split, UvTilePieceChoice piece)
        {
            return string.IsNullOrEmpty(split.TransformPath)
                ? piece.GeneratedChildName
                : split.TransformPath + "/" + piece.GeneratedChildName;
        }

        public static IEnumerable<UvTilePieceChoice> GetRetainedPieces(MobileAvatarMeshRecipe recipe)
        {
            if (recipe == null) yield break;
            foreach (var split in recipe.UvTileSplitRenderers.Where(item => item.Compatible && item.SplitEnabled))
            foreach (var piece in split.Pieces.Where(item => item.KeepOnMobile))
                yield return piece;
        }

        public static void ApplyToInstance(GameObject instance, MobileAvatarMeshRecipe recipe, bool selectedMeshes)
        {
            if (instance == null || recipe == null) return;
            foreach (var split in recipe.UvTileSplitRenderers.Where(item => item.Compatible && item.SplitEnabled))
            {
                var sourceChoice = recipe.RendererChoices.FirstOrDefault(choice =>
                    string.Equals(choice.TransformPath, split.TransformPath, StringComparison.Ordinal));
                if (sourceChoice == null || sourceChoice.IsExcludedFromMobile) continue;
                var sourceTransform = MeshAnalysisUtility.FindByPath(instance.transform, split.TransformPath);
                if (sourceTransform == null)
                    throw new InvalidOperationException("UV split renderer path is missing: " + split.TransformPath);
                var sourceRenderer = sourceTransform.GetComponent<Renderer>();
                if (sourceRenderer == null)
                    throw new InvalidOperationException("UV split renderer component is missing: " + split.TransformPath);

                foreach (var piece in split.Pieces.Where(item => item.KeepOnMobile))
                {
                    var oldChild = sourceTransform.Find(piece.GeneratedChildName);
                    if (oldChild != null) UnityEngine.Object.DestroyImmediate(oldChild.gameObject);
                    var child = new GameObject(piece.GeneratedChildName)
                    {
                        layer = sourceTransform.gameObject.layer,
                        tag = sourceTransform.gameObject.tag
                    };
                    child.transform.SetParent(sourceTransform, false);
                    var mesh = selectedMeshes ? piece.MeshChoice.SelectedMesh : piece.MeshChoice.SourceMesh;
                    if (split.Skinned)
                    {
                        var sourceSkinned = sourceRenderer as SkinnedMeshRenderer;
                        var target = child.AddComponent<SkinnedMeshRenderer>();
                        EditorUtility.CopySerialized(sourceSkinned, target);
                        target.sharedMesh = mesh;
                        target.sharedMaterials = new[] { piece.IsolatedSourceMaterial };
                    }
                    else
                    {
                        var sourceFilter = sourceTransform.GetComponent<MeshFilter>();
                        if (sourceFilter == null)
                            throw new InvalidOperationException("UV split MeshRenderer has no MeshFilter: " +
                                                                split.TransformPath);
                        var filter = child.AddComponent<MeshFilter>();
                        EditorUtility.CopySerialized(sourceFilter, filter);
                        filter.sharedMesh = mesh;
                        var target = child.AddComponent<MeshRenderer>();
                        EditorUtility.CopySerialized(sourceRenderer as MeshRenderer, target);
                        target.sharedMaterials = new[] { piece.IsolatedSourceMaterial };
                    }
                    child.SetActive(piece.BehaviorMode == UvTilePieceBehaviorMode.AlwaysVisibleOnMobile ||
                                    piece.SourceVisibleByDefault);
                }

                sourceRenderer.enabled = false;
                sourceRenderer.sharedMaterials = Array.Empty<Material>();
                if (sourceRenderer is SkinnedMeshRenderer skinnedRenderer) skinnedRenderer.sharedMesh = null;
                else
                {
                    var filter = sourceTransform.GetComponent<MeshFilter>();
                    if (filter != null) filter.sharedMesh = null;
                }
            }
        }

        private static List<PieceGeometry> ClassifyGeometry(Mesh mesh, Material[] materials, out string error)
        {
            error = string.Empty;
            var groups = new Dictionary<PieceKey, PieceGeometry>();
            for (var slot = 0; slot < mesh.subMeshCount; slot++)
            {
                if (mesh.GetTopology(slot) != MeshTopology.Triangles)
                {
                    error = $"submesh {slot} is {mesh.GetTopology(slot)}, not triangles.";
                    return new List<PieceGeometry>();
                }
                var material = slot < materials.Length ? materials[slot] : null;
                if (material == null)
                {
                    error = $"submesh {slot} has no matching material.";
                    return new List<PieceGeometry>();
                }
                var tiled = IsSupportedTileMaterial(material);
                var channel = tiled ? Mathf.Clamp(Mathf.RoundToInt(material.GetFloat(ChannelProperty)), 0, 3) : 0;
                var uvs = new List<Vector4>();
                if (tiled)
                {
                    mesh.GetUVs(channel, uvs);
                    if (uvs.Count != mesh.vertexCount)
                    {
                        error = $"{material.name} selects UV{channel}, but that channel is absent or incomplete.";
                        return new List<PieceGeometry>();
                    }
                }
                var triangles = mesh.GetTriangles(slot);
                for (var index = 0; index < triangles.Length; index += 3)
                {
                    PieceKey key;
                    if (!tiled)
                    {
                        key = new PieceKey(slot, channel, -1, -1, true);
                    }
                    else if (!TryClassifyTriangle(uvs[triangles[index]], uvs[triangles[index + 1]],
                                 uvs[triangles[index + 2]], slot, channel, out key))
                    {
                        error = $"{material.name}, submesh {slot} contains a triangle crossing UV tile boundaries. " +
                                "The split was refused rather than damaging topology.";
                        return new List<PieceGeometry>();
                    }
                    if (!groups.TryGetValue(key, out var geometry))
                    {
                        geometry = new PieceGeometry { Key = key, Material = material };
                        groups.Add(key, geometry);
                    }
                    geometry.Triangles.Add(triangles[index]);
                    geometry.Triangles.Add(triangles[index + 1]);
                    geometry.Triangles.Add(triangles[index + 2]);
                }
            }
            return groups.Values.ToList();
        }

        private static bool TryClassifyTriangle(Vector4 a4, Vector4 b4, Vector4 c4, int slot, int channel,
            out PieceKey key)
        {
            var a = new Vector2(a4.x, a4.y);
            var b = new Vector2(b4.x, b4.y);
            var c = new Vector2(c4.x, c4.y);
            var center = (a + b + c) / 3f;
            var column = Mathf.FloorToInt(center.x);
            var row = Mathf.FloorToInt(center.y);
            if (column < 0 || column > 3 || row < 0 || row > 3)
            {
                key = new PieceKey(slot, channel, -1, -1, true);
                return SameOutsideDomain(a, b, c);
            }
            var minX = column - TileEpsilon;
            var maxX = column + 1f + TileEpsilon;
            var minY = row - TileEpsilon;
            var maxY = row + 1f + TileEpsilon;
            var inside = new[] { a, b, c }.All(uv => uv.x >= minX && uv.x <= maxX && uv.y >= minY && uv.y <= maxY);
            key = new PieceKey(slot, channel, column, row, false);
            return inside;
        }

        private static bool SameOutsideDomain(Vector2 a, Vector2 b, Vector2 c)
        {
            int Region(Vector2 uv)
            {
                if (uv.x < 0f) return 0;
                if (uv.x > 4f) return 1;
                if (uv.y < 0f) return 2;
                if (uv.y > 4f) return 3;
                return 4;
            }
            return Region(a) == Region(b) && Region(b) == Region(c);
        }

        private static Mesh BuildCompactMesh(Mesh source, IReadOnlyList<int> triangles, string name)
        {
            var used = triangles.Distinct().OrderBy(index => index).ToArray();
            var remap = new Dictionary<int, int>();
            for (var index = 0; index < used.Length; index++) remap[used[index]] = index;
            var mesh = new Mesh { name = name, indexFormat = used.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            CopyVertexArray(source.vertices, used, values => mesh.vertices = values);
            if (source.normals.Length == source.vertexCount) CopyVertexArray(source.normals, used, values => mesh.normals = values);
            if (source.tangents.Length == source.vertexCount) CopyVertexArray(source.tangents, used, values => mesh.tangents = values);
            if (source.colors32.Length == source.vertexCount) CopyVertexArray(source.colors32, used, values => mesh.colors32 = values);
            for (var channel = 0; channel < 8; channel++)
            {
                var values = new List<Vector4>();
                source.GetUVs(channel, values);
                if (values.Count != source.vertexCount) continue;
                var compact = used.Select(vertex => values[vertex]).ToList();
                mesh.SetUVs(channel, compact);
            }

            CopyBoneWeights(source, mesh, used);
            mesh.bindposes = source.bindposes;
            mesh.subMeshCount = 1;
            mesh.SetTriangles(triangles.Select(index => remap[index]).ToArray(), 0, false);
            CopyBlendShapes(source, mesh, used);
            mesh.bounds = source.bounds;
            return mesh;
        }

        private static void CopyVertexArray<T>(T[] source, IReadOnlyList<int> used, Action<T[]> assign)
        {
            var result = new T[used.Count];
            for (var index = 0; index < used.Count; index++) result[index] = source[used[index]];
            assign(result);
        }

        private static void CopyBoneWeights(Mesh source, Mesh target, IReadOnlyList<int> used)
        {
            var bonesPerVertex = source.GetBonesPerVertex();
            var allWeights = source.GetAllBoneWeights();
            try
            {
                if (bonesPerVertex.Length != source.vertexCount) return;
                var offsets = new int[source.vertexCount + 1];
                for (var index = 0; index < source.vertexCount; index++)
                    offsets[index + 1] = offsets[index] + bonesPerVertex[index];
                var counts = new NativeArray<byte>(used.Count, Allocator.Temp);
                var total = used.Sum(vertex => (int)bonesPerVertex[vertex]);
                var weights = new NativeArray<BoneWeight1>(total, Allocator.Temp);
                try
                {
                    var cursor = 0;
                    for (var index = 0; index < used.Count; index++)
                    {
                        var vertex = used[index];
                        counts[index] = bonesPerVertex[vertex];
                        for (var weight = offsets[vertex]; weight < offsets[vertex + 1]; weight++)
                            weights[cursor++] = allWeights[weight];
                    }
                    target.SetBoneWeights(counts, weights);
                }
                finally
                {
                    counts.Dispose();
                    weights.Dispose();
                }
            }
            finally
            {
                bonesPerVertex.Dispose();
                allWeights.Dispose();
            }
        }

        private static void CopyBlendShapes(Mesh source, Mesh target, IReadOnlyList<int> used)
        {
            var sourceVertices = source.vertexCount;
            var deltaVertices = new Vector3[sourceVertices];
            var deltaNormals = new Vector3[sourceVertices];
            var deltaTangents = new Vector3[sourceVertices];
            foreach (var shapeIndex in Enumerable.Range(0, source.blendShapeCount))
            foreach (var frameIndex in Enumerable.Range(0, source.GetBlendShapeFrameCount(shapeIndex)))
            {
                source.GetBlendShapeFrameVertices(shapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);
                var dv = used.Select(index => deltaVertices[index]).ToArray();
                var dn = used.Select(index => deltaNormals[index]).ToArray();
                var dt = used.Select(index => deltaTangents[index]).ToArray();
                target.AddBlendShapeFrame(source.GetBlendShapeName(shapeIndex),
                    source.GetBlendShapeFrameWeight(shapeIndex, frameIndex), dv, dn, dt);
            }
        }

        private static Material CreateIsolatedMaterial(Material source, string root, string rendererToken,
            string pieceId, IDictionary<string, Texture2D> isolatedTextures,
            out List<UvTileTextureAsset> textureRecords)
        {
            textureRecords = new List<UvTileTextureAsset>();
            var material = new Material(source)
            {
                name = MeshAnalysisUtility.SanitizeFileName(source.name + "_" + rendererToken + "_" + pieceId)
            };
            foreach (var property in source.GetTexturePropertyNames())
            {
                if (!(source.GetTexture(property) is Texture2D texture)) continue;
                // Keep texture ownership per generated piece. A material is isolated
                // per tile already; using the source-only key here would silently make
                // all tiles share one mutable texture asset.
                var key = rendererToken + "|" + pieceId + "|" + GetStableAssetKey(texture);
                if (!isolatedTextures.TryGetValue(key, out var isolated))
                {
                    isolated = CopyTextureAsset(texture, root + "/Textures", key);
                    isolatedTextures.Add(key, isolated);
                }
                material.SetTexture(property, isolated);
                textureRecords.Add(new UvTileTextureAsset
                {
                    PropertyName = property,
                    SourceTexture = texture,
                    IsolatedTexture = isolated
                });
            }
            if (material.HasProperty(EnabledProperty)) material.SetFloat(EnabledProperty, 0f);
            for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
            {
                var property = $"_UVTileDissolveAlpha_Row{row}_{column}";
                if (material.HasProperty(property)) material.SetFloat(property, 0f);
            }
            var path = root + "/Materials/" + material.name + ".mat";
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static Texture2D CopyTextureAsset(Texture2D source, string folder, string stableKey)
        {
            var sourcePath = AssetDatabase.GetAssetPath(source);
            var stem = MeshAnalysisUtility.SanitizeFileName(source.name) + "_UVSplit_" + stableKey.Substring(0, 8);
            if (!string.IsNullOrEmpty(sourcePath) && AssetDatabase.IsMainAsset(source))
            {
                var extension = Path.GetExtension(sourcePath);
                var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + stem + extension);
                if (AssetDatabase.CopyAsset(sourcePath, path))
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    var copy = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (copy != null) return copy;
                }
            }
            var clone = UnityEngine.Object.Instantiate(source);
            clone.name = stem;
            var assetPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + stem + ".asset");
            AssetDatabase.CreateAsset(clone, assetPath);
            return clone;
        }

        private static void PopulateMeshChoice(RendererMeshChoice choice, UvTileSplitRenderer split,
            UvTilePieceChoice piece, Mesh mesh, Renderer sourceRenderer)
        {
            choice.TransformPath = BuildChildPath(split, piece);
            choice.DisplayName = split.DisplayName + " / " + piece.DisplayName;
            choice.Skinned = split.Skinned;
            choice.SourceMesh = mesh;
            choice.SourceTriangleCount = MeshAnalysisUtility.TriangleCount(mesh);
            choice.SourceVertexCount = mesh.vertexCount;
            choice.SourceBlendShapeCount = mesh.blendShapeCount;
            choice.SourceConnectedComponents = MeshAnalysisUtility.ConnectedTriangleComponents(mesh);
            choice.SourceBoneCount = sourceRenderer is SkinnedMeshRenderer skinned ? skinned.bones.Length : 0;
            choice.SourceReadable = mesh.isReadable;
            choice.ReductionRisk = mesh.blendShapeCount > 0 ? 55 : 15;
            choice.ReductionRiskReason = mesh.blendShapeCount > 0
                ? "Generated UV tile piece retains blendshapes and requires deformation review."
                : "Generated UV tile piece uses source topology and isolated material data.";
            choice.GenerateCandidates = true;
            choice.SelectedCandidateIndex = 0;
            choice.Identity.HierarchyPath = choice.TransformPath;
            choice.Identity.RendererName = piece.GeneratedChildName;
            choice.Identity.MeshSignature = MeshAnalysisUtility.ComputeMeshSignature(mesh);
            choice.Identity.MaterialSignature = GetStableAssetKey(piece.IsolatedSourceMaterial);
            choice.Identity.BlendShapeSignature = MeshAnalysisUtility.ComputeStringSignature(
                Enumerable.Range(0, mesh.blendShapeCount).Select(index => mesh.GetBlendShapeName(index)));
        }

        private static Dictionary<string, List<string>> CollectClipLabels(GameObject sourcePrefab)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var sourcePath = AssetDatabase.GetAssetPath(sourcePrefab);
            foreach (var clip in AssetDatabase.GetDependencies(sourcePath, true)
                         .SelectMany(AssetDatabase.LoadAllAssetsAtPath).OfType<AnimationClip>().Distinct())
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var property = binding.propertyName.StartsWith("material.", StringComparison.Ordinal)
                    ? binding.propertyName.Substring("material.".Length)
                    : binding.propertyName;
                if (!TryParseTileProperty(property, out _, out _)) continue;
                var key = binding.path + "|" + property;
                if (!result.TryGetValue(key, out var labels)) result.Add(key, labels = new List<string>());
                if (!labels.Contains(clip.name)) labels.Add(clip.name);
            }
            foreach (var labels in result.Values) labels.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        public static bool TryParseTileProperty(string property, out int row, out int column)
        {
            row = -1;
            column = -1;
            const string prefix = "_UVTileDissolveAlpha_Row";
            if (string.IsNullOrEmpty(property) || !property.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var parts = property.Substring(prefix.Length).Split('_');
            return parts.Length == 2 && int.TryParse(parts[0], out row) && int.TryParse(parts[1], out column) &&
                   row >= 0 && row < 4 && column >= 0 && column < 4;
        }

        private static bool IsSupportedTileMaterial(Material material)
        {
            return material != null && material.HasProperty(EnabledProperty) &&
                   material.GetFloat(EnabledProperty) > 0.5f && material.HasProperty(DiscardProperty) &&
                   material.GetFloat(DiscardProperty) > 0.5f && material.HasProperty(ChannelProperty);
        }

        private static bool IsTileDiscarded(Material material, string property)
        {
            return material != null && !string.IsNullOrEmpty(property) && material.HasProperty(property) &&
                   material.GetFloat(property) > 0.999f;
        }

        private static bool TryGetRendererMesh(Renderer renderer, out Mesh mesh, out bool skinned)
        {
            mesh = null;
            skinned = false;
            if (renderer == null) return false;
            if (renderer is SkinnedMeshRenderer skinnedRenderer)
            {
                mesh = skinnedRenderer.sharedMesh;
                skinned = true;
                return mesh != null;
            }
            // ParticleSystemRenderer, TrailRenderer, LineRenderer, and other Renderer subclasses
            // do not own MeshFilters. Check the renderer kind before reading the component; using
            // ?. on UnityEngine.Object is unsafe because it bypasses Unity's overloaded null test.
            if (!(renderer is MeshRenderer)) return false;
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null) return false;
            mesh = filter.sharedMesh;
            return mesh != null;
        }

        private static string GetStableAssetKey(UnityEngine.Object asset)
        {
            if (asset == null) return Hash128.Compute("<null>").ToString();
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId);
            return Hash128.Compute((guid ?? string.Empty) + "|" + localId + "|" + asset.name).ToString();
        }

        private static string StableToken(string value)
        {
            return Hash128.Compute(value ?? string.Empty).ToString().Substring(0, 8);
        }
    }
}
