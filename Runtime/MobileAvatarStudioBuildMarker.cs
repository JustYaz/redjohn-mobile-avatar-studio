using UnityEngine;

namespace MobileAvatarStudio
{
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class MobileAvatarStudioBuildMarker : MonoBehaviour
    {
        [SerializeField] private string recipeAssetGuid;

        public string RecipeAssetGuid
        {
            get => recipeAssetGuid;
            set => recipeAssetGuid = value;
        }
    }
}
