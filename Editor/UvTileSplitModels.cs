using System;
using System.Collections.Generic;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal enum UvTilePieceBehaviorMode
    {
        FollowSourceToggle,
        AlwaysVisibleOnMobile
    }

    [Serializable]
    internal sealed class UvTileTextureAsset
    {
        [SerializeField] private string propertyName;
        [SerializeField] private Texture2D sourceTexture;
        [SerializeField] private Texture2D isolatedTexture;

        public string PropertyName { get => propertyName; set => propertyName = value; }
        public Texture2D SourceTexture { get => sourceTexture; set => sourceTexture = value; }
        public Texture2D IsolatedTexture { get => isolatedTexture; set => isolatedTexture = value; }
    }

    [Serializable]
    internal sealed class UvTilePieceChoice
    {
        [SerializeField] private string id;
        [SerializeField] private string displayName;
        [SerializeField] private string generatedChildName;
        [SerializeField] private int materialSlot;
        [SerializeField] private int uvChannel;
        [SerializeField] private int tileColumn = -1;
        [SerializeField] private int tileRow = -1;
        [SerializeField] private bool alwaysVisible;
        [SerializeField] private bool keepOnMobile = true;
        [SerializeField] private bool sourceVisibleByDefault = true;
        [SerializeField] private UvTilePieceBehaviorMode behaviorMode;
        [SerializeField] private Material sourceMaterial;
        [SerializeField] private Material isolatedSourceMaterial;
        [SerializeField] private List<UvTileTextureAsset> textures = new List<UvTileTextureAsset>();
        [SerializeField] private List<string> controllingClips = new List<string>();
        [SerializeField] private RendererMeshChoice meshChoice = new RendererMeshChoice();

        public string Id { get => id; set => id = value; }
        public string DisplayName { get => displayName; set => displayName = value; }
        public string GeneratedChildName { get => generatedChildName; set => generatedChildName = value; }
        public int MaterialSlot { get => materialSlot; set => materialSlot = value; }
        public int UvChannel { get => uvChannel; set => uvChannel = value; }
        public int TileColumn { get => tileColumn; set => tileColumn = value; }
        public int TileRow { get => tileRow; set => tileRow = value; }
        public bool AlwaysVisible { get => alwaysVisible; set => alwaysVisible = value; }
        public bool KeepOnMobile { get => keepOnMobile; set => keepOnMobile = value; }
        public bool SourceVisibleByDefault { get => sourceVisibleByDefault; set => sourceVisibleByDefault = value; }
        public UvTilePieceBehaviorMode BehaviorMode { get => behaviorMode; set => behaviorMode = value; }
        public Material SourceMaterial { get => sourceMaterial; set => sourceMaterial = value; }
        public Material IsolatedSourceMaterial { get => isolatedSourceMaterial; set => isolatedSourceMaterial = value; }
        public List<UvTileTextureAsset> Textures => textures;
        public List<string> ControllingClips => controllingClips;
        public RendererMeshChoice MeshChoice => meshChoice;
        public string TileProperty => AlwaysVisible
            ? string.Empty
            : $"_UVTileDissolveAlpha_Row{TileRow}_{TileColumn}";
    }

    [Serializable]
    internal sealed class UvTileSplitRenderer
    {
        [SerializeField] private string transformPath;
        [SerializeField] private string displayName;
        [SerializeField] private bool skinned;
        [SerializeField] private bool compatible;
        [SerializeField] private bool splitEnabled;
        [SerializeField, TextArea] private string status;
        [SerializeField] private string sourceMeshSignature;
        [SerializeField] private List<UvTilePieceChoice> pieces = new List<UvTilePieceChoice>();

        public string TransformPath { get => transformPath; set => transformPath = value; }
        public string DisplayName { get => displayName; set => displayName = value; }
        public bool Skinned { get => skinned; set => skinned = value; }
        public bool Compatible { get => compatible; set => compatible = value; }
        public bool SplitEnabled { get => splitEnabled; set => splitEnabled = value; }
        public string Status { get => status; set => status = value; }
        public string SourceMeshSignature { get => sourceMeshSignature; set => sourceMeshSignature = value; }
        public List<UvTilePieceChoice> Pieces => pieces;
    }
}
