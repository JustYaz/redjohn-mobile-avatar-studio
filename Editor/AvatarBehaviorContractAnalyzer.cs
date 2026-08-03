using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal static class AvatarBehaviorContractAnalyzer
    {
        public static AvatarBehaviorContract Capture(GameObject sourcePrefab)
        {
            var contract = new AvatarBehaviorContract { CapturedUtc = DateTime.UtcNow.ToString("O") };
            var sourcePath = AssetDatabase.GetAssetPath(sourcePrefab);
            var dependencyPaths = AssetDatabase.GetDependencies(sourcePath, true)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var dependencyAssets = dependencyPaths
                .SelectMany(AssetDatabase.LoadAllAssetsAtPath)
                .Where(asset => asset != null)
                .Distinct()
                .ToArray();

            var clips = dependencyAssets.OfType<AnimationClip>().Distinct().ToArray();
            var bindings = clips.SelectMany(AnimationUtility.GetCurveBindings).ToArray();
            var objectBindings = clips.SelectMany(AnimationUtility.GetObjectReferenceCurveBindings).ToArray();
            var components = sourcePrefab.GetComponentsInChildren<Component>(true).Where(component => component != null).ToArray();
            var componentTypes = components.Select(component => component.GetType().FullName ?? component.GetType().Name).ToArray();

            Add(contract, "Renderer paths",
                sourcePrefab.GetComponentsInChildren<Renderer>(true).Length,
                "Every renderer path is part of the source contract and must remain resolvable after conversion.");
            Add(contract, "Animation clips", clips.Length, $"{bindings.Length + objectBindings.Length} total curve bindings.");
            Add(contract, "Animation bindings", bindings.Length + objectBindings.Length,
                $"Float curves: {bindings.Length}; object-reference curves: {objectBindings.Length}.");
            Add(contract, "Object toggles",
                bindings.Count(binding => binding.propertyName == "m_IsActive" || binding.propertyName == "m_Enabled"),
                "Animated active-state and enabled-state bindings.");
            Add(contract, "Material properties",
                bindings.Count(binding => binding.propertyName.StartsWith("material.", StringComparison.Ordinal)),
                "Animated material property bindings; these constrain shader conversion and atlas grouping.");
            Add(contract, "Blendshapes",
                sourcePrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .Where(renderer => renderer.sharedMesh != null)
                    .Sum(renderer => renderer.sharedMesh.blendShapeCount),
                $"Animated blendshape bindings: {bindings.Count(binding => binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))}.");
            Add(contract, "Particles", sourcePrefab.GetComponentsInChildren<ParticleSystem>(true).Length,
                "Particle components present in the authoring prefab.");
            Add(contract, "Audio sources", sourcePrefab.GetComponentsInChildren<AudioSource>(true).Length,
                "Audio sources are unsupported on mobile avatars and require an explicit contract decision.");
            Add(contract, "Constraints",
                components.Count(component => IsConstraintType(component.GetType())),
                "Unity and VRChat constraint components detected by type.");
            Add(contract, "PhysBones", componentTypes.Count(name => name.IndexOf("VRCPhysBone", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("Collider", StringComparison.OrdinalIgnoreCase) < 0),
                "PhysBone components detected without assuming a specific SDK assembly version.");
            Add(contract, "PhysBone colliders", componentTypes.Count(name => name.IndexOf("VRCPhysBoneCollider", StringComparison.OrdinalIgnoreCase) >= 0),
                "PhysBone collider components.");
            Add(contract, "Contacts", componentTypes.Count(name => name.IndexOf("VRCContact", StringComparison.OrdinalIgnoreCase) >= 0),
                "Contact sender and receiver components.");

            var menuAssets = dependencyAssets.Where(asset =>
                (asset.GetType().FullName ?? asset.GetType().Name).IndexOf("VRCExpressionsMenu", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            var parameterAssets = dependencyAssets.Where(asset =>
                (asset.GetType().FullName ?? asset.GetType().Name).IndexOf("VRCExpressionParameters", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            Add(contract, "Menu assets", menuAssets.Length, $"Controls discovered: {CountSerializedArrayEntries(menuAssets, "controls")}.");
            Add(contract, "Menu controls", CountSerializedArrayEntries(menuAssets, "controls"),
                "Reachable-control analysis is a later contract pass; this is the serialized source count.");
            Add(contract, "Parameters", CountSerializedArrayEntries(parameterAssets, "parameters"),
                "Expression parameter definitions found in source dependencies.");

            DetectBuildSystems(componentTypes, contract);
            if (contract.DetectedBuildSystems.Count > 0)
                contract.Warnings.Add("Build-time avatar systems were detected. The mature analyzer must inspect their resolved output before final conversion.");

            contract.ContractHash = ComputeContractHash(contract, dependencyPaths);
            return contract;
        }

        private static void Add(AvatarBehaviorContract contract, string name, int count, string summary)
        {
            contract.Categories.Add(new BehaviorContractCategory
            {
                Name = name,
                EntryCount = count,
                Summary = summary
            });
        }

        private static int CountSerializedArrayEntries(IEnumerable<UnityEngine.Object> assets, string propertyName)
        {
            var count = 0;
            foreach (var asset in assets)
            {
                try
                {
                    var property = new SerializedObject(asset).FindProperty(propertyName);
                    if (property != null && property.isArray) count += property.arraySize;
                }
                catch
                {
                    // Unknown third-party asset serialization must not make source analysis destructive or fail closed.
                }
            }
            return count;
        }

        private static bool IsConstraintType(Type type)
        {
            var name = type.FullName ?? type.Name;
            return name.IndexOf("Constraint", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   name.IndexOf("ConstraintSource", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static void DetectBuildSystems(IEnumerable<string> componentTypes, AvatarBehaviorContract contract)
        {
            foreach (var name in componentTypes)
            {
                if (name.IndexOf("VRCFury", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddUnique(contract.DetectedBuildSystems, "VRCFury");
                if (name.IndexOf("ModularAvatar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("modular_avatar", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddUnique(contract.DetectedBuildSystems, "Modular Avatar");
                if (name.IndexOf("NDMF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("nadena.dev.ndmf", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddUnique(contract.DetectedBuildSystems, "NDMF");
            }
        }

        private static void AddUnique(ICollection<string> values, string value)
        {
            if (!values.Contains(value)) values.Add(value);
        }

        private static string ComputeContractHash(AvatarBehaviorContract contract, IEnumerable<string> dependencies)
        {
            var text = new StringBuilder();
            foreach (var category in contract.Categories.OrderBy(category => category.Name, StringComparer.Ordinal))
                text.Append(category.Name).Append('=').Append(category.EntryCount).Append('|').Append(category.Summary).AppendLine();
            foreach (var system in contract.DetectedBuildSystems.OrderBy(value => value, StringComparer.Ordinal))
                text.Append("build=").AppendLine(system);
            foreach (var dependency in dependencies) text.Append("dep=").AppendLine(dependency);
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())).Select(value => value.ToString("x2")));
        }
    }
}
