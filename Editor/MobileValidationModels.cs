using System;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal enum MobileValidationSeverity
    {
        Info,
        Warning,
        Error
    }

    [Serializable]
    internal sealed class MobileValidationIssue
    {
        [SerializeField] private MobileValidationSeverity severity;
        [SerializeField] private string objectPath;
        [SerializeField] private string category;
        [SerializeField, TextArea] private string message;

        public MobileValidationSeverity Severity { get => severity; set => severity = value; }
        public string ObjectPath { get => objectPath; set => objectPath = value; }
        public string Category { get => category; set => category = value; }
        public string Message { get => message; set => message = value; }
    }

    [Serializable]
    internal sealed class MobileComponentRepairChoice
    {
        [SerializeField] private string objectPath;
        [SerializeField] private string componentTypeName;
        [SerializeField] private int componentIndex;
        [SerializeField] private int currentComponentIndex;
        [SerializeField] private string category;
        [SerializeField] private string displayName;
        [SerializeField] private int estimatedAffectedTransforms;
        [SerializeField] private bool removeFromMobile;
        [SerializeField] private bool presentInUploadPrefab = true;
        [SerializeField] private string removedUtc;

        public string ObjectPath { get => objectPath; set => objectPath = value; }
        public string ComponentTypeName { get => componentTypeName; set => componentTypeName = value; }
        public int ComponentIndex { get => componentIndex; set => componentIndex = value; }
        public int CurrentComponentIndex { get => currentComponentIndex; set => currentComponentIndex = value; }
        public string Category { get => category; set => category = value; }
        public string DisplayName { get => displayName; set => displayName = value; }
        public int EstimatedAffectedTransforms
        {
            get => estimatedAffectedTransforms;
            set => estimatedAffectedTransforms = value;
        }
        public bool RemoveFromMobile { get => removeFromMobile; set => removeFromMobile = value; }
        public bool PresentInUploadPrefab
        {
            get => presentInUploadPrefab;
            set => presentInUploadPrefab = value;
        }
        public string RemovedUtc { get => removedUtc; set => removedUtc = value; }
        public string Key => ObjectPath + "|" + ComponentTypeName + "|" + ComponentIndex;
    }
}
