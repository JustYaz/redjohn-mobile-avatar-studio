using System;
using System.Collections.Generic;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    [Serializable]
    internal sealed class RendererMaterialSlotCacheEntry
    {
        [SerializeField] private string transformPath;
        [SerializeField] private string rendererType;
        [SerializeField] private int rendererTypeIndex;
        [SerializeField] private List<Material> materials = new List<Material>();

        public string TransformPath { get => transformPath; set => transformPath = value; }
        public string RendererType { get => rendererType; set => rendererType = value; }
        public int RendererTypeIndex { get => rendererTypeIndex; set => rendererTypeIndex = value; }
        public List<Material> Materials => materials;
    }
}
