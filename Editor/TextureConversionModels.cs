using System;
using System.Collections.Generic;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    [Flags]
    internal enum MobileTextureRole
    {
        None = 0,
        Color = 1 << 0,
        Normal = 1 << 1,
        Emission = 1 << 2,
        Metallic = 1 << 3,
        Gloss = 1 << 4,
        Matcap = 1 << 5,
        Mask = 1 << 6,
        Other = 1 << 7
    }

    internal enum MobileTextureCompression
    {
        ASTC6x6,
        ASTC8x8
    }

    internal enum MobileTextureCategory
    {
        BaseColor,
        Normal,
        Emission,
        PackedMasks,
        Matcap,
        MixedReview,
        Other
    }

    [Serializable]
    internal sealed class TextureMaterialBinding
    {
        [SerializeField] private Material targetMaterial;
        [SerializeField] private string propertyName;

        public Material TargetMaterial { get => targetMaterial; set => targetMaterial = value; }
        public string PropertyName { get => propertyName; set => propertyName = value; }
    }

    [Serializable]
    internal sealed class TextureConversionChoice
    {
        [SerializeField] private Texture2D sourceTexture;
        [SerializeField] private string sourceAssetPath;
        [SerializeField] private string sourceGuid;
        [SerializeField] private long sourceLocalFileId;
        [SerializeField] private string sourceSignature;
        [SerializeField] private int sourceWidth;
        [SerializeField] private int sourceHeight;
        [SerializeField] private MobileTextureRole roles;
        [SerializeField] private int targetMaxSize = 1024;
        [SerializeField] private MobileTextureCompression compression = MobileTextureCompression.ASTC6x6;
        [SerializeField] private bool embeddedSource;
        [SerializeField] private Texture2D generatedTexture;
        [SerializeField] private string generatedAssetPath;
        [SerializeField, TextArea] private string notes;
        [SerializeField] private List<TextureMaterialBinding> bindings = new List<TextureMaterialBinding>();
        [SerializeField] private bool androidSnapshotCaptured;
        [SerializeField] private bool originalAndroidOverridden;
        [SerializeField] private int originalAndroidMaxSize;
        [SerializeField] private int originalAndroidFormat;
        [SerializeField] private int originalAndroidCompression;
        [SerializeField] private int originalAndroidCompressionQuality;
        [SerializeField] private bool originalAndroidCrunchedCompression;
        [SerializeField] private bool originalAndroidAllowsAlphaSplitting;
        [SerializeField] private int originalAndroidEtc2Fallback;
        [SerializeField] private bool androidOverrideApplied;
        [SerializeField] private bool iosSnapshotCaptured;
        [SerializeField] private bool originalIosOverridden;
        [SerializeField] private int originalIosMaxSize;
        [SerializeField] private int originalIosFormat;
        [SerializeField] private int originalIosCompression;
        [SerializeField] private int originalIosCompressionQuality;
        [SerializeField] private bool originalIosCrunchedCompression;
        [SerializeField] private bool originalIosAllowsAlphaSplitting;
        [SerializeField] private int originalIosEtc2Fallback;
        [SerializeField] private bool iosOverrideApplied;
        [SerializeField] private bool approved;
        [SerializeField] private string approvedSettingsId;
        [SerializeField] private string approvedUtc;

        public Texture2D SourceTexture { get => sourceTexture; set => sourceTexture = value; }
        public string SourceAssetPath { get => sourceAssetPath; set => sourceAssetPath = value; }
        public string SourceGuid { get => sourceGuid; set => sourceGuid = value; }
        public long SourceLocalFileId { get => sourceLocalFileId; set => sourceLocalFileId = value; }
        public string SourceSignature { get => sourceSignature; set => sourceSignature = value; }
        public int SourceWidth { get => sourceWidth; set => sourceWidth = value; }
        public int SourceHeight { get => sourceHeight; set => sourceHeight = value; }
        public MobileTextureRole Roles { get => roles; set => roles = value; }
        public int TargetMaxSize { get => targetMaxSize; set => targetMaxSize = value; }
        public MobileTextureCompression Compression { get => compression; set => compression = value; }
        public bool EmbeddedSource { get => embeddedSource; set => embeddedSource = value; }
        public Texture2D GeneratedTexture { get => generatedTexture; set => generatedTexture = value; }
        public string GeneratedAssetPath { get => generatedAssetPath; set => generatedAssetPath = value; }
        public string Notes { get => notes; set => notes = value; }
        public List<TextureMaterialBinding> Bindings => bindings;
        public bool AndroidSnapshotCaptured { get => androidSnapshotCaptured; set => androidSnapshotCaptured = value; }
        public bool OriginalAndroidOverridden { get => originalAndroidOverridden; set => originalAndroidOverridden = value; }
        public int OriginalAndroidMaxSize { get => originalAndroidMaxSize; set => originalAndroidMaxSize = value; }
        public int OriginalAndroidFormat { get => originalAndroidFormat; set => originalAndroidFormat = value; }
        public int OriginalAndroidCompression { get => originalAndroidCompression; set => originalAndroidCompression = value; }
        public int OriginalAndroidCompressionQuality { get => originalAndroidCompressionQuality; set => originalAndroidCompressionQuality = value; }
        public bool OriginalAndroidCrunchedCompression { get => originalAndroidCrunchedCompression; set => originalAndroidCrunchedCompression = value; }
        public bool OriginalAndroidAllowsAlphaSplitting { get => originalAndroidAllowsAlphaSplitting; set => originalAndroidAllowsAlphaSplitting = value; }
        public int OriginalAndroidEtc2Fallback { get => originalAndroidEtc2Fallback; set => originalAndroidEtc2Fallback = value; }
        public bool AndroidOverrideApplied { get => androidOverrideApplied; set => androidOverrideApplied = value; }
        public bool IosSnapshotCaptured { get => iosSnapshotCaptured; set => iosSnapshotCaptured = value; }
        public bool OriginalIosOverridden { get => originalIosOverridden; set => originalIosOverridden = value; }
        public int OriginalIosMaxSize { get => originalIosMaxSize; set => originalIosMaxSize = value; }
        public int OriginalIosFormat { get => originalIosFormat; set => originalIosFormat = value; }
        public int OriginalIosCompression { get => originalIosCompression; set => originalIosCompression = value; }
        public int OriginalIosCompressionQuality { get => originalIosCompressionQuality; set => originalIosCompressionQuality = value; }
        public bool OriginalIosCrunchedCompression { get => originalIosCrunchedCompression; set => originalIosCrunchedCompression = value; }
        public bool OriginalIosAllowsAlphaSplitting { get => originalIosAllowsAlphaSplitting; set => originalIosAllowsAlphaSplitting = value; }
        public int OriginalIosEtc2Fallback { get => originalIosEtc2Fallback; set => originalIosEtc2Fallback = value; }
        public bool IosOverrideApplied { get => iosOverrideApplied; set => iosOverrideApplied = value; }
        public bool MobileOverridesApplied => androidOverrideApplied && iosOverrideApplied;
        public string CurrentSettingsId => sourceSignature + "|" + targetMaxSize + "|" + compression;
        public bool IsCurrentSettingsApproved => approved &&
                                                 string.Equals(approvedSettingsId, CurrentSettingsId,
                                                     StringComparison.Ordinal);

        public void ApproveCurrentSettings()
        {
            approved = true;
            approvedSettingsId = CurrentSettingsId;
            approvedUtc = DateTime.UtcNow.ToString("O");
        }

        public void RevokeApproval()
        {
            approved = false;
            approvedSettingsId = string.Empty;
            approvedUtc = string.Empty;
        }
    }
}
