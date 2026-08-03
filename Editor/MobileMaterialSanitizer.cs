using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MobileAvatarStudio.Editor
{
    internal static class MobileMaterialSanitizer
    {
        public static void ResetToShader(Material target, Shader shader, string objectName)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (shader == null) throw new ArgumentNullException(nameof(shader));
            var clean = new Material(shader) { name = objectName };
            try
            {
                EditorUtility.CopySerialized(clean, target);
                target.name = objectName;
                EditorUtility.SetDirty(target);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clean);
            }
        }

        public static int SanitizeOwnedMaterialAssets(GameObject prefab, string outputRoot)
        {
            if (prefab == null || string.IsNullOrEmpty(outputRoot)) return 0;
            var materials = CollectOwnedMaterialAssets(prefab, outputRoot);

            foreach (var material in materials) SanitizeInPlace(material);
            if (materials.Length > 0) AssetDatabase.SaveAssets();
            return materials.Length;
        }

        /// <summary>
        /// Collects both renderer-default materials and materials reachable only through animation
        /// clips/controllers. Material-swap clips are real build dependencies even when the material
        /// is not assigned to a renderer in the prefab's default state.
        /// </summary>
        public static Material[] CollectOwnedMaterialAssets(GameObject prefab, string outputRoot)
        {
            if (prefab == null || string.IsNullOrEmpty(outputRoot)) return Array.Empty<Material>();
            var ownedRoot = outputRoot.TrimEnd('/') + "/";
            var materials = new HashSet<Material>();

            foreach (var material in prefab.GetComponentsInChildren<Renderer>(true)
                         .SelectMany(renderer => renderer.sharedMaterials)
                         .Where(material => material != null))
                AddIfOwned(materials, material, ownedRoot);

            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            if (!string.IsNullOrEmpty(prefabPath))
            {
                foreach (var dependencyPath in AssetDatabase.GetDependencies(prefabPath, true))
                {
                    if (!dependencyPath.StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase) ||
                        !dependencyPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) continue;
                    AddIfOwned(materials, AssetDatabase.LoadAssetAtPath<Material>(dependencyPath), ownedRoot);
                }
            }

            return materials.OrderBy(AssetDatabase.GetAssetPath, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void AddIfOwned(ISet<Material> materials, Material material, string ownedRoot)
        {
            if (material == null) return;
            var path = AssetDatabase.GetAssetPath(material);
            if (path.StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                materials.Add(material);
        }

        private static void SanitizeInPlace(Material target)
        {
            if (target == null || target.shader == null) return;
            var sourceName = target.name;
            var clean = new Material(target.shader) { name = sourceName };
            try
            {
                CopyActiveProperties(target, clean);
                clean.shaderKeywords = target.shaderKeywords;
                clean.renderQueue = target.renderQueue;
                clean.enableInstancing = target.enableInstancing;
                clean.doubleSidedGI = target.doubleSidedGI;
                clean.globalIlluminationFlags = target.globalIlluminationFlags;
                EditorUtility.CopySerialized(clean, target);
                target.name = sourceName;
                EditorUtility.SetDirty(target);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clean);
            }
        }

        private static void CopyActiveProperties(Material source, Material target)
        {
            var shader = source.shader;
            for (var index = 0; index < shader.GetPropertyCount(); index++)
            {
                var propertyName = shader.GetPropertyName(index);
                switch (shader.GetPropertyType(index))
                {
                    case ShaderPropertyType.Color:
                        target.SetColor(propertyName, source.GetColor(propertyName));
                        break;
                    case ShaderPropertyType.Vector:
                        target.SetVector(propertyName, source.GetVector(propertyName));
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                    case ShaderPropertyType.Int:
                        target.SetFloat(propertyName, source.GetFloat(propertyName));
                        break;
                    case ShaderPropertyType.Texture:
                        target.SetTexture(propertyName, source.GetTexture(propertyName));
                        target.SetTextureScale(propertyName, source.GetTextureScale(propertyName));
                        target.SetTextureOffset(propertyName, source.GetTextureOffset(propertyName));
                        break;
                }
            }
        }
    }
}
