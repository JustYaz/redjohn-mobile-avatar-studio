using System;
using System.Collections.Generic;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal enum BehaviorCurveMappingKind
    {
        ExactProperty,
        SuggestedTranslation,
        Unsupported,
        GeometryActivation
    }

    internal enum UnsupportedBehaviorResolution
    {
        Unresolved,
        KeepStaticMobileMaterial
    }

    [Serializable]
    internal sealed class BehaviorCurveMappingChoice
    {
        [SerializeField] private string sourceProperty;
        [SerializeField] private string targetProperties;
        [SerializeField] private float scale = 1f;
        [SerializeField] private float offset;
        [SerializeField] private bool normalizeStrength;
        [SerializeField] private int bindingCount;
        [SerializeField] private BehaviorCurveMappingKind kind;
        [SerializeField, TextArea] private string summary;
        [SerializeField] private List<string> sourceClipPaths = new List<string>();
        [SerializeField] private string mappingSignature;
        [SerializeField] private bool approved;
        [SerializeField] private string approvedSignature;
        [SerializeField] private string approvedUtc;
        [SerializeField] private UnsupportedBehaviorResolution unsupportedResolution;
        [SerializeField] private string resolutionSignature;
        [SerializeField] private string resolvedUtc;

        public string SourceProperty { get => sourceProperty; set => sourceProperty = value; }
        public string TargetProperties { get => targetProperties; set => targetProperties = value; }
        public float Scale { get => scale; set => scale = value; }
        public float Offset { get => offset; set => offset = value; }
        public bool NormalizeStrength { get => normalizeStrength; set => normalizeStrength = value; }
        public int BindingCount { get => bindingCount; set => bindingCount = value; }
        public BehaviorCurveMappingKind Kind { get => kind; set => kind = value; }
        public string Summary { get => summary; set => summary = value; }
        public List<string> SourceClipPaths => sourceClipPaths;
        public string MappingSignature { get => mappingSignature; set => mappingSignature = value; }
        public UnsupportedBehaviorResolution UnsupportedResolution => unsupportedResolution;
        public bool IsCurrentMappingApproved => kind == BehaviorCurveMappingKind.ExactProperty ||
                                                kind == BehaviorCurveMappingKind.GeometryActivation ||
                                                approved && string.Equals(approvedSignature, mappingSignature,
                                                    StringComparison.Ordinal);
        public bool IsCurrentUnsupportedResolution =>
            kind == BehaviorCurveMappingKind.Unsupported &&
            unsupportedResolution == UnsupportedBehaviorResolution.KeepStaticMobileMaterial &&
            string.Equals(resolutionSignature, mappingSignature, StringComparison.Ordinal);
        public bool IsReadyForBuild => IsCurrentMappingApproved || IsCurrentUnsupportedResolution;

        public IEnumerable<string> EnumerateTargets()
        {
            if (string.IsNullOrWhiteSpace(targetProperties)) yield break;
            foreach (var value in targetProperties.Split(','))
            {
                var trimmed = value.Trim();
                if (!string.IsNullOrEmpty(trimmed)) yield return trimmed;
            }
        }

        public void ApproveCurrentMapping()
        {
            approved = true;
            approvedSignature = mappingSignature;
            approvedUtc = DateTime.UtcNow.ToString("O");
        }

        public void RevokeApproval()
        {
            approved = false;
            approvedSignature = string.Empty;
            approvedUtc = string.Empty;
        }

        public void ResolveUnsupportedAsStaticMobileMaterial()
        {
            if (kind != BehaviorCurveMappingKind.Unsupported)
                throw new InvalidOperationException("Only unsupported properties can use a static mobile fallback.");
            unsupportedResolution = UnsupportedBehaviorResolution.KeepStaticMobileMaterial;
            resolutionSignature = mappingSignature;
            resolvedUtc = DateTime.UtcNow.ToString("O");
        }

        public void ClearUnsupportedResolution()
        {
            unsupportedResolution = UnsupportedBehaviorResolution.Unresolved;
            resolutionSignature = string.Empty;
            resolvedUtc = string.Empty;
        }
    }
}
