using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal static class MeshAnalysisUtility
    {
        internal readonly struct ValidationResult
        {
            public ValidationResult(MeshCandidateStatus status, string message, int connectedComponents)
            {
                Status = status;
                Message = message;
                ConnectedComponents = connectedComponents;
            }

            public MeshCandidateStatus Status { get; }
            public string Message { get; }
            public int ConnectedComponents { get; }
        }

        public static int TriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            long total = 0;
            for (var index = 0; index < mesh.subMeshCount; index++)
                total += (long)mesh.GetIndexCount(index) / 3L;
            return (int)Math.Min(int.MaxValue, total);
        }

        public static int ConnectedTriangleComponents(Mesh mesh)
        {
            if (mesh == null || mesh.vertexCount == 0) return 0;
            try
            {
                var parent = new int[mesh.vertexCount];
                var used = new bool[mesh.vertexCount];
                for (var i = 0; i < parent.Length; i++) parent[i] = i;

                for (var subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    var indices = mesh.GetIndices(subMesh);
                    var step = mesh.GetTopology(subMesh) == MeshTopology.Triangles ? 3 : 1;
                    for (var i = 0; i + step - 1 < indices.Length; i += step)
                    {
                        var first = indices[i];
                        used[first] = true;
                        for (var offset = 1; offset < step; offset++)
                        {
                            var next = indices[i + offset];
                            used[next] = true;
                            Union(parent, first, next);
                        }
                    }
                }

                var roots = new HashSet<int>();
                for (var i = 0; i < used.Length; i++)
                    if (used[i]) roots.Add(Find(parent, i));
                return roots.Count;
            }
            catch (UnityException)
            {
                return -1;
            }
        }

        public static ValidationResult ValidateCandidate(Mesh source, Mesh candidate, bool skinned,
            int sourceConnectedComponents)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            if (candidate == null)
                return new ValidationResult(MeshCandidateStatus.Rejected, "Reducer returned no mesh.", 0);

            if (candidate.vertexCount <= 0 || TriangleCount(candidate) <= 0)
                errors.Add("The candidate is empty.");
            if (candidate.subMeshCount != source.subMeshCount)
                errors.Add($"Submesh count changed from {source.subMeshCount} to {candidate.subMeshCount}.");

            if (skinned && source.boneWeights.Length > 0 && candidate.boneWeights.Length != candidate.vertexCount)
                errors.Add("Bone weights are incomplete.");
            if (skinned && source.bindposes.Length != candidate.bindposes.Length)
                errors.Add($"Bind-pose count changed from {source.bindposes.Length} to {candidate.bindposes.Length}.");

            if (source.blendShapeCount != candidate.blendShapeCount)
            {
                errors.Add($"Blendshape count changed from {source.blendShapeCount} to {candidate.blendShapeCount}.");
            }
            else
            {
                for (var index = 0; index < source.blendShapeCount; index++)
                {
                    if (!string.Equals(source.GetBlendShapeName(index), candidate.GetBlendShapeName(index), StringComparison.Ordinal))
                    {
                        errors.Add($"Blendshape name changed at index {index}.");
                        break;
                    }

                    if (source.GetBlendShapeFrameCount(index) != candidate.GetBlendShapeFrameCount(index))
                    {
                        errors.Add($"Blendshape frame count changed for {source.GetBlendShapeName(index)}.");
                        break;
                    }
                }
            }

            CompareBounds(source.bounds, candidate.bounds, warnings);

            var candidateComponents = ConnectedTriangleComponents(candidate);
            if (sourceConnectedComponents > 0 && candidateComponents < sourceConnectedComponents)
            {
                var lost = sourceConnectedComponents - candidateComponents;
                warnings.Add($"Disconnected geometry groups decreased by {lost} ({sourceConnectedComponents} to {candidateComponents}); small mesh pieces may have collapsed.");
            }

            if (errors.Count > 0)
                return new ValidationResult(MeshCandidateStatus.Rejected, string.Join(" ", errors), candidateComponents);
            if (warnings.Count > 0)
                return new ValidationResult(MeshCandidateStatus.ReviewRequired, string.Join(" ", warnings), candidateComponents);
            return new ValidationResult(MeshCandidateStatus.Safe, "Structural checks passed. Visual approval is still required.", candidateComponents);
        }

        public static string CalculateTransformPath(Transform transform, Transform root)
        {
            if (transform == root) return string.Empty;
            var names = new Stack<string>();
            var current = transform;
            while (current != null && current != root)
            {
                names.Push(current.name);
                current = current.parent;
            }
            if (current != root)
                throw new InvalidOperationException($"{transform.name} is not under {root.name}.");
            return string.Join("/", names);
        }

        public static Transform FindByPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var current = root;
            foreach (var segment in path.Split('/'))
            {
                Transform next = null;
                for (var index = 0; index < current.childCount; index++)
                {
                    var child = current.GetChild(index);
                    if (child.name == segment)
                    {
                        next = child;
                        break;
                    }
                }
                if (next == null) return null;
                current = next;
            }
            return current;
        }

        public static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Avatar";
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
                builder.Append(invalid.Contains(character) || character == '/' || character == '\\' ? '_' : character);
            return builder.ToString().Trim();
        }

        public static string ComputeAssetHash(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return string.Empty;
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot)) return string.Empty;
            var absolute = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute)) return string.Empty;
            using (var stream = File.OpenRead(absolute))
            using (var hash = SHA256.Create())
                return string.Concat(hash.ComputeHash(stream).Select(item => item.ToString("x2")));
        }

        public static string ComputeMeshSignature(Mesh mesh)
        {
            if (mesh == null) return string.Empty;
            try
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(mesh.vertexCount);
                    writer.Write(mesh.subMeshCount);
                    writer.Write(mesh.blendShapeCount);
                    WriteVector(writer, mesh.bounds.center);
                    WriteVector(writer, mesh.bounds.size);
                    foreach (var vertex in mesh.vertices) WriteVector(writer, vertex);
                    for (var subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                    {
                        writer.Write((int)mesh.GetTopology(subMesh));
                        var indices = mesh.GetIndices(subMesh);
                        writer.Write(indices.Length);
                        foreach (var index in indices) writer.Write(index);
                    }
                    for (var shape = 0; shape < mesh.blendShapeCount; shape++)
                    {
                        writer.Write(mesh.GetBlendShapeName(shape));
                        writer.Write(mesh.GetBlendShapeFrameCount(shape));
                    }
                    writer.Flush();
                    stream.Position = 0;
                    using (var sha = SHA256.Create())
                        return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
                }
            }
            catch (UnityException)
            {
                string guid;
                long localFileId;
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out guid, out localFileId);
                var path = AssetDatabase.GetAssetPath(mesh);
                return ComputeStringSignature(new[]
                {
                    guid,
                    localFileId.ToString(),
                    AssetDatabase.GetAssetDependencyHash(path).ToString(),
                    mesh.vertexCount.ToString(),
                    mesh.subMeshCount.ToString(),
                    mesh.blendShapeCount.ToString()
                });
            }
        }

        public static string ComputeStringSignature(IEnumerable<string> values)
        {
            var text = string.Join("\n", values ?? Enumerable.Empty<string>());
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(value => value.ToString("x2")));
        }

        public static void EnsureAssetFolder(string assetFolder)
        {
            var normalized = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(normalized)) return;
            var pieces = normalized.Split('/');
            var current = pieces[0];
            for (var index = 1; index < pieces.Length; index++)
            {
                var next = current + "/" + pieces[index];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, pieces[index]);
                current = next;
            }
        }

        private static void CompareBounds(Bounds source, Bounds candidate, ICollection<string> warnings)
        {
            var sourceSize = source.size;
            var candidateSize = candidate.size;
            var axes = new[] { "X", "Y", "Z" };
            for (var index = 0; index < 3; index++)
            {
                if (sourceSize[index] < 0.00001f) continue;
                var ratio = candidateSize[index] / sourceSize[index];
                if (ratio < 0.92f)
                    warnings.Add($"Bounds shrank to {ratio:P0} on {axes[index]}; an extremity may have disappeared.");
                else if (ratio > 1.08f)
                    warnings.Add($"Bounds grew to {ratio:P0} on {axes[index]}; inspect for malformed vertices.");
            }
        }

        private static int Find(int[] parent, int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }
            return value;
        }

        private static void Union(int[] parent, int left, int right)
        {
            var leftRoot = Find(parent, left);
            var rightRoot = Find(parent, right);
            if (leftRoot != rightRoot) parent[rightRoot] = leftRoot;
        }

        private static void WriteVector(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }
    }
}
