using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal sealed class MobileAvatarStudioWindow : EditorWindow
    {
        private enum StudioStage
        {
            Overview,
            Meshes,
            Materials,
            Textures,
            Assemble,
            Behavior,
            Validate,
            Build
        }

        private enum PreviewMode
        {
            SelectedCandidate,
            Original,
            SideBySide
        }

        private static readonly Color SafeColor = new Color(0.35f, 0.85f, 0.45f);
        private static readonly Color ReviewColor = new Color(1f, 0.72f, 0.2f);
        private static readonly Color RejectedColor = new Color(1f, 0.35f, 0.35f);
        private static readonly int[] TextureSizeOptions = { 256, 512, 1024, 2048 };
        private static readonly string[] TextureSizeLabels = { "256", "512", "1024", "2048" };

        [SerializeField] private GameObject sourcePrefab;
        [SerializeField] private MobileAvatarMeshRecipe recipe;
        private IMeshReductionBackend backend;
        private readonly float[] ratios = { 0.90f, 0.75f, 0.60f, 0.40f };
        private readonly string[] ratioLabels = { "Very Light", "Light", "Balanced", "Aggressive" };

        private Vector2 rendererScroll;
        private Vector2 uvTileScroll;
        private Vector2 meshControlScroll;
        private Vector2 candidateScroll;
        private Vector2 contractScroll;
        private Vector2 materialScroll;
        private Vector2 textureScroll;
        private Vector2 behaviorScroll;
        private Vector2 validationScroll;
        private Vector2 repairScroll;
        private int repairTab;
        private string search = string.Empty;
        private string materialSearch = string.Empty;
        private string previewUvPieceKey = string.Empty;
        private int materialSurfaceFilter;
        private int selectedRendererIndex = -1;
        private bool isolateSelected = true;
        private bool wireframe;
        private bool adaptiveCandidates = true;
        private bool showBehaviorContract;
        private bool showMobileContentRules = true;
        private bool showUvTileSplit = true;
        private bool deferredOperationQueued;
        private StudioStage stage;
        private readonly bool[] textureCategoryExpanded = { true, true, true, true, true, true, true };
        private readonly int[] textureCategoryMaxSizes = { 1024, 1024, 512, 512, 512, 1024, 512 };
        private readonly MobileTextureCompression[] textureCategoryCompressions =
        {
            MobileTextureCompression.ASTC6x6,
            MobileTextureCompression.ASTC6x6,
            MobileTextureCompression.ASTC6x6,
            MobileTextureCompression.ASTC8x8,
            MobileTextureCompression.ASTC6x6,
            MobileTextureCompression.ASTC6x6,
            MobileTextureCompression.ASTC8x8
        };

        [SerializeField] private float meshControlPaneWidth;
        [SerializeField] private float uvTileListHeight = 245f;
        [SerializeField] private float rendererListHeight = 280f;
        [SerializeField] private float validationIssueListHeight = 230f;
        [SerializeField] private float repairListHeight = 310f;
        [SerializeField] private float behaviorContractListHeight = 150f;

        private const string LayoutPreferencePrefix = "MobileAvatarStudio.Layout.";

        private PreviewRenderUtility preview;
        private GameObject previewInstance;
        private GameObject originalPreviewInstance;
        private Bounds previewBounds;
        private PreviewMode previewMode = PreviewMode.SideBySide;
        private float previewYaw = 155f;
        private float previewPitch = 8f;
        private float previewZoom = 1f;
        private Vector2 previewPan;

        [MenuItem("Tools/Mobile Avatar Studio/Open")]
        [MenuItem("Tools/Mobile Avatar Studio/Open Mesh Candidate Lab")]
        private static void Open()
        {
            var window = GetWindow<MobileAvatarStudioWindow>();
            window.titleContent = new GUIContent("Mobile Avatar Studio");
            window.minSize = new Vector2(920f, 600f);
            window.Show();
        }

        private void OnEnable()
        {
            backend = new AutoLodReflectionBackend();
            LoadPaneLayout();
            RestoreWorkspaceState();
            MigrateManualPolishCheckpoint();
            CreatePreviewUtility();
        }

        private void OnDisable()
        {
            SavePaneLayout();
            SaveRecipe();
            RememberWorkspaceState();
            DestroyPreviewInstance();
            preview?.Cleanup();
            preview = null;
        }

        private void OnGUI()
        {
            DrawHeader();

            if (recipe == null)
            {
                EditorGUILayout.HelpBox(
                    "Select any prefab asset and analyze it. No source assets are modified during analysis or candidate generation.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.Space(4f);
            var nextStage = (StudioStage)GUILayout.Toolbar((int)stage,
                new[] { "1 Overview", "2 Meshes", "3 Materials", "4 Textures", "5 Assemble", "6 Behavior", "7 Fix & Validate", "8 Build" });
            if (nextStage != stage)
            {
                if ((nextStage == StudioStage.Validate || nextStage == StudioStage.Build) &&
                    !ManualPolishPipeline.ValidateCheckpoint(recipe, out var checkpointReason))
                {
                    stage = StudioStage.Behavior;
                    EditorUtility.DisplayDialog("Manual polish checkpoint required", checkpointReason, "Go to Stage 6");
                }
                else
                {
                    stage = nextStage;
                    RememberWorkspaceState();
                }
            }
            EditorGUILayout.Space(4f);

            switch (stage)
            {
                case StudioStage.Overview:
                    DrawSummary();
                    DrawBehaviorContractSummary();
                    DrawOverview();
                    break;
                case StudioStage.Meshes:
                    DrawSummary();
                    DrawMeshWorkspace();
                    break;
                case StudioStage.Materials:
                    DrawMaterialWorkspace();
                    break;
                case StudioStage.Textures:
                    DrawTextureWorkspace();
                    break;
                case StudioStage.Assemble:
                    DrawFinalWorkspace();
                    break;
                case StudioStage.Behavior:
                    DrawBehaviorWorkspace();
                    break;
                case StudioStage.Validate:
                    DrawMobileValidationWorkspace();
                    break;
                case StudioStage.Build:
                    DrawMobileBuildWorkspace();
                    break;
            }
        }

        private void DrawMeshWorkspace()
        {
            var defaultWidth = Mathf.Clamp(position.width * 0.46f, 410f, 620f);
            if (meshControlPaneWidth <= 0f) meshControlPaneWidth = defaultWidth;
            var maxLeftWidth = Mathf.Max(360f, position.width - 285f);
            meshControlPaneWidth = Mathf.Clamp(meshControlPaneWidth, 360f, maxLeftWidth);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical(EditorStyles.helpBox,
                GUILayout.Width(meshControlPaneWidth), GUILayout.MaxWidth(meshControlPaneWidth),
                GUILayout.ExpandHeight(true));
            meshControlScroll = EditorGUILayout.BeginScrollView(meshControlScroll,
                GUILayout.ExpandHeight(true));
            DrawMeshListAndCandidates();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            DrawVerticalSplitter("MeshControlsPreview", ref meshControlPaneWidth, 360f, maxLeftWidth,
                defaultWidth);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            DrawPreviewPanel();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Mobile Avatar Studio", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Build a reviewable mobile counterpart in isolated stages without modifying the PC source prefab.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            sourcePrefab = (GameObject)EditorGUILayout.ObjectField("Source prefab", sourcePrefab, typeof(GameObject), false);
            if (EditorGUI.EndChangeCheck() && sourcePrefab != recipe?.SourcePrefab)
            {
                recipe = null;
                selectedRendererIndex = -1;
                DestroyPreviewInstance();
                RememberWorkspaceState();
            }

            var existingRecipe = MeshCandidatePipeline.FindExistingRecipe(sourcePrefab);
            if (GUILayout.Button(existingRecipe != null ? "Open / Resume" : "Analyze", GUILayout.Width(105f)))
                RunDeferredEditorOperation(() => AnalyzeSource(false));
            EditorGUI.BeginDisabledGroup(existingRecipe == null);
            if (GUILayout.Button("Start Fresh...", GUILayout.Width(100f)))
                RunDeferredEditorOperation(StartFresh);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            var loaded = (MobileAvatarMeshRecipe)EditorGUILayout.ObjectField("Open recipe", recipe, typeof(MobileAvatarMeshRecipe), false);
            if (loaded != recipe && loaded != null)
            {
                recipe = loaded;
                sourcePrefab = recipe.SourcePrefab;
                selectedRendererIndex = recipe.RendererChoices.Count > 0 ? 0 : -1;
                stage = StudioStage.Overview;
                RebuildPreview();
                RememberWorkspaceState();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawOverview()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Conversion workflow", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Analysis records the source contract. Meshes and materials are separate approval stages; later stages will add textures/atlases, dynamics, behavior remapping, and Android/iOS validation.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.HelpBox(
                "Progress is saved in this recipe after each decision and generation checkpoint. Open / Resume continues it later. Only Start Fresh replaces saved generated work.",
                MessageType.Info);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Generated workspace", EditorStyles.miniBoldLabel);
            EditorGUILayout.SelectableLabel(recipe.OutputRoot, EditorStyles.textField, GUILayout.Height(18f));
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Continue to Mesh Candidates", GUILayout.Height(28f))) SetStage(StudioStage.Meshes);
            if (GUILayout.Button("Review Material Mappings", GUILayout.Height(28f))) SetStage(StudioStage.Materials);
            if (GUILayout.Button("Configure Android/iOS Textures", GUILayout.Height(28f))) SetStage(StudioStage.Textures);
            EditorGUILayout.EndHorizontal();
            if (GUILayout.Button("Assemble Combined Quest Prefab", GUILayout.Height(28f))) SetStage(StudioStage.Assemble);
            if (GUILayout.Button("Review and Isolate Behavior", GUILayout.Height(28f))) SetStage(StudioStage.Behavior);
            if (GUILayout.Button("Validate Android/iOS Build", GUILayout.Height(28f))) SetStage(StudioStage.Validate);
            EditorGUILayout.HelpBox(
                "The combined prefab remains a working output until resolved behavior simulation and an actual VRChat SDK mobile build pass. Its validation state is recorded in the Studio instead of its filename.",
                MessageType.Warning);
            EditorGUILayout.EndVertical();
        }

        private void DrawMaterialWorkspace()
        {
            var choices = recipe.MaterialChoices;
            var approved = choices.Count(choice => choice.IsCurrentMappingApproved);
            var review = choices.Count(choice => choice.Risk == MaterialConversionRisk.ReviewRequired);
            var availableShaders = MaterialConversionPipeline.GetAvailableMobileAvatarShaderNames();

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Materials: {choices.Count}", GUILayout.Width(120f));
            EditorGUILayout.LabelField($"Approved: {approved}/{choices.Count}", GUILayout.Width(140f));
            EditorGUILayout.LabelField($"Need review: {review}", GUILayout.Width(140f));
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Per-material mobile shader policy", EditorStyles.miniBoldLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "Every source material stays separate and has its own mobile shader choice. Recommendations use the source render mode, actual ParticleSystemRenderer usage, textures, and special features. Additive and Multiply are recommended only for particle evidence; cutout and transparent mesh materials default to a safe opaque approximation instead of being washed out.",
                MessageType.Info);

            var opaqueCount = choices.Count(choice =>
                choice.SurfaceClassification == MaterialSurfaceClassification.Opaque);
            var cutoutCount = choices.Count(choice =>
                choice.SurfaceClassification == MaterialSurfaceClassification.Cutout);
            var transparentCount = choices.Count(choice =>
                choice.SurfaceClassification == MaterialSurfaceClassification.TransparentMesh);
            var particleMaterialCount = choices.Count(choice =>
                choice.SurfaceClassification == MaterialSurfaceClassification.ParticleAdditive ||
                choice.SurfaceClassification == MaterialSurfaceClassification.ParticleMultiply);
            EditorGUILayout.LabelField($"Detected surfaces — Opaque: {opaqueCount} | Cutout: {cutoutCount} | " +
                                       $"Transparent mesh: {transparentCount} | Particle: {particleMaterialCount}",
                EditorStyles.miniBoldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Re-scan Source Materials", GUILayout.Height(25f)))
                RunDeferredEditorOperation(ReanalyzeMaterials);
            EditorGUI.BeginDisabledGroup(choices.Count == 0);
            if (GUILayout.Button("Apply Safe Shader Recommendations", GUILayout.Height(25f)))
            {
                var changed = choices.Count(choice => !string.Equals(choice.TargetShaderName,
                    choice.RecommendedShaderName, StringComparison.Ordinal));
                if (changed == 0 || EditorUtility.DisplayDialog(
                        "Apply safe mobile shader recommendations?",
                        $"Reset {changed} custom shader selection(s) to the current evidence-based recommendations. " +
                        "This changes only recipe choices and revokes those material approvals; generated materials are not rebuilt until you press Build.",
                        "Apply Recommendations", "Cancel"))
                {
                    Undo.RecordObject(recipe, "Apply mobile shader recommendations");
                    foreach (var choice in choices) MaterialConversionPipeline.ApplyRecommendedShader(choice);
                    MaterialConversionPipeline.InvalidateDownstream(recipe,
                        "Material shader recommendations changed; rebuild Stages 3-6");
                    SaveRecipe();
                }
            }
            if (GUILayout.Button("Approve All Low-Risk Mappings", GUILayout.Height(25f)))
            {
                Undo.RecordObject(recipe, "Approve low-risk material mappings");
                foreach (var choice in choices.Where(item => item.Risk == MaterialConversionRisk.Low))
                    choice.ApproveCurrentMapping();
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();
            }
            if (GUILayout.Button("Approve All Mappings - No Merge", GUILayout.Height(25f)))
            {
                var particleCount = choices.Count(item =>
                    item.TargetShaderName == MaterialConversionPipeline.TransparentTargetShaderName ||
                    item.TargetShaderName == MaterialConversionPipeline.MultiplyTargetShaderName);
                var customCount = choices.Count(item => !string.Equals(item.TargetShaderName,
                    item.RecommendedShaderName, StringComparison.Ordinal));
                var confirmed = EditorUtility.DisplayDialog(
                    "Approve all material mappings?",
                    $"This approves {choices.Count} separate one-to-one Quest material copies. Nothing will be merged or atlased.\n\n" +
                    $"{particleCount} material(s) currently use mobile particle shaders. " +
                    $"{customCount} mapping(s) differ from the Studio recommendation. Every displayed choice will be used exactly as shown.",
                    "Approve All - No Merge",
                    "Cancel");
                if (confirmed)
                {
                    Undo.RecordObject(recipe, "Approve all material mappings");
                    foreach (var choice in choices) choice.ApproveCurrentMapping();
                    EditorUtility.SetDirty(recipe);
                    AssetDatabase.SaveAssets();
                }
            }
            if (GUILayout.Button("Revoke All Approvals", GUILayout.Height(25f)))
            {
                Undo.RecordObject(recipe, "Revoke material approvals");
                foreach (var choice in choices) choice.RevokeApproval();
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (choices.Count == 0)
            {
                EditorGUILayout.HelpBox("This older recipe has no material scan. Press Re-scan Source Materials.", MessageType.Warning);
                return;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Find material", GUILayout.Width(82f));
            materialSearch = EditorGUILayout.TextField(materialSearch, EditorStyles.toolbarSearchField,
                GUILayout.MinWidth(180f));
            EditorGUILayout.LabelField("Surface", GUILayout.Width(52f));
            var filterLabels = new[] { "All", "Opaque", "Cutout", "Transparent Mesh", "Particle Additive", "Particle Multiply" };
            materialSurfaceFilter = EditorGUILayout.Popup(materialSurfaceFilter, filterLabels, GUILayout.Width(145f));
            EditorGUILayout.EndHorizontal();

            var visibleChoices = choices.Where(choice =>
                    string.IsNullOrWhiteSpace(materialSearch) ||
                    choice.SourceMaterial != null && choice.SourceMaterial.name.IndexOf(materialSearch,
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    choice.SourceShaderName.IndexOf(materialSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(choice => materialSurfaceFilter == 0 ||
                                 (int)choice.SurfaceClassification == materialSurfaceFilter - 1)
                .ToArray();
            materialScroll = EditorGUILayout.BeginScrollView(materialScroll);
            foreach (var choice in visibleChoices)
            {
                var currentApproved = choice.IsCurrentMappingApproved;
                EditorGUILayout.BeginVertical(currentApproved ? "SelectionRect" : EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.ObjectField(choice.SourceMaterial, typeof(Material), false, GUILayout.Width(230f));
                EditorGUILayout.LabelField(choice.SourceShaderName, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("->", GUILayout.Width(20f));
                var shaderIndex = Array.IndexOf(availableShaders, choice.TargetShaderName);
                if (shaderIndex < 0) shaderIndex = 0;
                EditorGUI.BeginChangeCheck();
                shaderIndex = EditorGUILayout.Popup(shaderIndex, availableShaders, GUILayout.Width(245f));
                if (EditorGUI.EndChangeCheck() && availableShaders.Length > 0)
                {
                    Undo.RecordObject(recipe, "Change mobile material shader");
                    choice.TargetShaderName = availableShaders[shaderIndex];
                    choice.RevokeApproval();
                    MaterialConversionPipeline.InvalidateDownstream(recipe,
                        "A material shader selection changed; rebuild Stages 3-6");
                    SaveRecipe();
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(
                    $"Used by {choice.RendererUsageCount} renderer(s) | animated bindings: {choice.AnimatedBindingCount} | " +
                    $"source queue: {choice.SourceRenderQueue} | classification: {choice.SurfaceClassification}" +
                    (choice.UsedByParticleRenderer ? " | ParticleSystemRenderer" : string.Empty),
                    EditorStyles.wordWrappedMiniLabel);
                var followsRecommendation = string.Equals(choice.TargetShaderName, choice.RecommendedShaderName,
                    StringComparison.Ordinal);
                EditorGUILayout.LabelField("Recommended: " + choice.RecommendedShaderName + " | confidence: " +
                                           choice.RecommendationConfidence +
                                           (followsRecommendation ? " (selected)" : " (custom override)"),
                    followsRecommendation ? EditorStyles.miniLabel : EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(choice.RecommendationSummary, EditorStyles.wordWrappedMiniLabel);
                if (!followsRecommendation && GUILayout.Button("Use Studio recommendation for this material",
                        GUILayout.Height(21f)))
                {
                    Undo.RecordObject(recipe, "Use recommended mobile shader");
                    MaterialConversionPipeline.ApplyRecommendedShader(choice);
                    MaterialConversionPipeline.InvalidateDownstream(recipe,
                        "A material shader selection changed; rebuild Stages 3-6");
                    SaveRecipe();
                }
                if (choice.TargetShaderName.EndsWith("(Outline)", StringComparison.Ordinal))
                    EditorGUILayout.HelpBox(
                        "Toon Standard Outline is PC-only. VRChat automatically falls it back to non-outline Toon Standard on mobile.",
                        MessageType.Warning);
                EditorGUILayout.HelpBox(choice.RiskSummary,
                    choice.Risk == MaterialConversionRisk.Low ? MessageType.Info : MessageType.Warning);

                EditorGUILayout.BeginHorizontal();
                var oldColor = GUI.color;
                GUI.color = currentApproved ? SafeColor : Color.white;
                if (GUILayout.Button(currentApproved ? "Approved - click to revoke" : "Approve this exact mapping",
                        GUILayout.Height(23f)))
                {
                    Undo.RecordObject(recipe, currentApproved ? "Revoke material mapping" : "Approve material mapping");
                    if (currentApproved) choice.RevokeApproval(); else choice.ApproveCurrentMapping();
                    EditorUtility.SetDirty(recipe);
                    AssetDatabase.SaveAssets();
                }
                GUI.color = oldColor;
                if (choice.GeneratedMaterial != null)
                    EditorGUILayout.ObjectField("Generated", choice.GeneratedMaterial, typeof(Material), false, GUILayout.Width(310f));
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();

            var unapprovedCount = choices.Count(choice => !choice.IsCurrentMappingApproved);
            EditorGUI.BeginDisabledGroup(unapprovedCount > 0);
            if (GUILayout.Button("Build Isolated Quest Material Draft", GUILayout.Height(32f)))
                RunDeferredEditorOperation(BuildMaterialDraft);
            EditorGUI.EndDisabledGroup();
            if (unapprovedCount > 0)
                EditorGUILayout.HelpBox(
                    $"{unapprovedCount} mapping(s) still require approval. Review-required materials must be approved individually.",
                    MessageType.Warning);
            else
                EditorGUILayout.HelpBox(
                    "This creates an isolated draft with separate Toon Standard material copies. Texture copies, atlases, animation remapping, PhysBones, and Android/iOS build validation are later stages.",
                    MessageType.Info);
        }

        private void ReanalyzeMaterials()
        {
            try
            {
                MaterialConversionPipeline.Analyze(recipe);
                ShowNotification(new GUIContent($"Scanned {recipe.MaterialChoices.Count} materials"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Material scan failed", exception.Message, "OK");
            }
        }

        private void BuildMaterialDraft()
        {
            try
            {
                var path = MaterialConversionPipeline.BuildMaterialDraft(recipe);
                ShowNotification(new GUIContent("Built " + path));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Material conversion failed", exception.Message, "OK");
            }
        }

        private void DrawTextureWorkspace()
        {
            var choices = recipe.TextureChoices;
            var approved = choices.Count(choice => choice.IsCurrentSettingsApproved);
            var embedded = choices.Count(choice => choice.EmbeddedSource);
            var estimate = choices.Sum(TextureConversionPipeline.EstimateMobileBytes);

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Textures: {choices.Count}", GUILayout.Width(120f));
            EditorGUILayout.LabelField($"Approved: {approved}/{choices.Count}", GUILayout.Width(140f));
            EditorGUILayout.LabelField($"Auto-copy/bake: {embedded}", GUILayout.Width(145f));
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Estimated memory per platform: " + TextureConversionPipeline.FormatBytes(estimate),
                EditorStyles.miniBoldLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "Project textures reuse their original high-quality assets with Android/iOS-only overrides. Package-owned or embedded textures receive deterministic copies used only by the isolated mobile materials. Standalone/PC settings and source packages are not changed.",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan Quest Material Textures", GUILayout.Height(25f)))
                RunDeferredEditorOperation(ScanTextures);
            EditorGUI.BeginDisabledGroup(choices.Count == 0);
            if (GUILayout.Button("Approve All Texture Settings", GUILayout.Height(25f)))
            {
                var confirmed = EditorUtility.DisplayDialog(
                    "Approve all Android/iOS texture settings?",
                    $"Approve the displayed max size and ASTC format for Android and iOS on all {choices.Count} textures?\n\n" +
                    "This does not merge textures and does not change Standalone/PC settings.",
                    "Approve All",
                    "Cancel");
                if (confirmed)
                {
                    Undo.RecordObject(recipe, "Approve all Android/iOS texture settings");
                    foreach (var choice in choices) choice.ApproveCurrentSettings();
                    EditorUtility.SetDirty(recipe);
                    AssetDatabase.SaveAssets();
                }
            }
            if (GUILayout.Button("Restore Previous Android/iOS Settings", GUILayout.Height(25f)))
                RunDeferredEditorOperation(RestoreTextureSettings);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            var duplicateUvTextureCopies = TextureConversionPipeline.CountDuplicateUvSplitTextureCopies(recipe);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(choices.Count == 0 || duplicateUvTextureCopies == 0);
            if (GUILayout.Button($"Share Duplicate UV Textures ({duplicateUvTextureCopies})", GUILayout.Height(25f)))
            {
                var confirmed = EditorUtility.DisplayDialog(
                    "Share duplicate UV-split textures?",
                    $"Reuse one generated texture asset for each group of UV-split pieces that came from the same source texture.\n\n" +
                    $"This can remove up to {duplicateUvTextureCopies} duplicate texture copies. Materials stay separate so shader settings, animations, and toggles remain independent. No atlasing is performed.",
                    "Share Textures",
                    "Cancel");
                if (confirmed) RunDeferredEditorOperation(ShareDuplicateUvTextures);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            if (duplicateUvTextureCopies > 0)
                EditorGUILayout.HelpBox(
                    "Optional memory pass: this shares identical source textures across retained UV pieces while keeping every material and toggle separate.",
                    MessageType.Info);

            if (choices.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Press Scan Quest Material Textures after building the isolated material draft.",
                    MessageType.Info);
                return;
            }

            textureScroll = EditorGUILayout.BeginScrollView(textureScroll);
            foreach (MobileTextureCategory category in Enum.GetValues(typeof(MobileTextureCategory)))
            {
                var categoryChoices = choices.Where(choice => TextureConversionPipeline.GetCategory(choice) == category).ToArray();
                if (categoryChoices.Length == 0) continue;
                var categoryIndex = (int)category;
                var categoryEstimate = categoryChoices.Sum(TextureConversionPipeline.EstimateMobileBytes);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                textureCategoryExpanded[categoryIndex] = EditorGUILayout.Foldout(textureCategoryExpanded[categoryIndex],
                    $"{TextureConversionPipeline.CategoryDisplayName(category)} ({categoryChoices.Length})", true,
                    EditorStyles.foldoutHeader);
                if (EditorGUI.EndChangeCheck()) RememberWorkspaceState();
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(TextureConversionPipeline.FormatBytes(categoryEstimate),
                    EditorStyles.miniBoldLabel, GUILayout.Width(90f));
                EditorGUILayout.EndHorizontal();
                if (textureCategoryExpanded[categoryIndex])
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Category preset", GUILayout.Width(95f));
                    EditorGUI.BeginChangeCheck();
                    var categorySizeIndex = Array.IndexOf(TextureSizeOptions, textureCategoryMaxSizes[categoryIndex]);
                    if (categorySizeIndex < 0) categorySizeIndex = 2;
                    categorySizeIndex = EditorGUILayout.Popup(categorySizeIndex, TextureSizeLabels, GUILayout.Width(80f));
                    textureCategoryMaxSizes[categoryIndex] = TextureSizeOptions[categorySizeIndex];
                    textureCategoryCompressions[categoryIndex] =
                        (MobileTextureCompression)EditorGUILayout.EnumPopup(textureCategoryCompressions[categoryIndex],
                            GUILayout.Width(95f));
                    if (EditorGUI.EndChangeCheck()) RememberWorkspaceState();
                    if (GUILayout.Button("Apply to This Category", GUILayout.Width(165f)))
                        ApplyTextureCategoryPreset(category, categoryChoices);
                    if (GUILayout.Button("Recommended", GUILayout.Width(100f)))
                        ApplyRecommendedTextureCategoryPreset(category, categoryChoices);
                    EditorGUILayout.EndHorizontal();

                    if (category == MobileTextureCategory.MixedReview)
                        EditorGUILayout.HelpBox(
                            "These textures serve more than one incompatible role. Keep the higher-quality setting unless you verify every use visually.",
                            MessageType.Warning);
                    foreach (var choice in categoryChoices) DrawTextureChoice(choice);
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();

            var unapproved = choices.Count(choice => !choice.IsCurrentSettingsApproved);
            EditorGUI.BeginDisabledGroup(unapproved > 0);
            if (GUILayout.Button("Apply Android/iOS Texture Overrides", GUILayout.Height(32f)))
                RunDeferredEditorOperation(ApplyTextureSettings);
            EditorGUI.EndDisabledGroup();
            if (embedded > 0)
                EditorGUILayout.HelpBox(
                    $"{embedded} package-owned or embedded texture(s) will be copied automatically into this generated workspace when the pass runs.",
                    MessageType.Info);
            else if (unapproved > 0)
                EditorGUILayout.HelpBox($"{unapproved} texture setting(s) still require approval.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox(
                    "Ready. The displayed memory is a per-platform estimate; actual Android and iOS bundle sizes must be measured later.",
                    MessageType.Info);
        }

        private void DrawTextureChoice(TextureConversionChoice choice)
        {
            var currentApproved = choice.IsCurrentSettingsApproved;
            EditorGUILayout.BeginVertical(currentApproved ? "SelectionRect" : EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.ObjectField(choice.SourceTexture, typeof(Texture2D), false, GUILayout.Width(230f));
            EditorGUILayout.LabelField($"{choice.SourceWidth}x{choice.SourceHeight}", GUILayout.Width(90f));
            EditorGUILayout.LabelField(choice.Roles.ToString(), EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(TextureConversionPipeline.FormatBytes(
                TextureConversionPipeline.EstimateMobileBytes(choice)), GUILayout.Width(85f));
            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Mobile max", GUILayout.Width(85f));
            var currentSizeIndex = Array.IndexOf(TextureSizeOptions, choice.TargetMaxSize);
            if (currentSizeIndex < 0) currentSizeIndex = 2;
            currentSizeIndex = EditorGUILayout.Popup(currentSizeIndex, TextureSizeLabels, GUILayout.Width(80f));
            choice.TargetMaxSize = TextureSizeOptions[currentSizeIndex];
            EditorGUILayout.LabelField("Compression", GUILayout.Width(80f));
            choice.Compression = (MobileTextureCompression)EditorGUILayout.EnumPopup(choice.Compression, GUILayout.Width(95f));
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(recipe, "Change Android/iOS texture setting");
                choice.RevokeApproval();
                SaveRecipe();
            }

            var platformNote = choice.EmbeddedSource
                ? choice.GeneratedTexture != null
                    ? "A deterministic generated copy is used by the isolated mobile materials. The source package or embedded texture is not modified."
                    : "This package-owned or embedded texture will be copied automatically into the generated workspace for Android/iOS."
                : "The source asset is reused with Android/iOS-only overrides. Standalone/PC settings are not changed.";
            EditorGUILayout.HelpBox(platformNote, choice.EmbeddedSource ? MessageType.Info : MessageType.None);
            var oldColor = GUI.color;
            GUI.color = currentApproved ? SafeColor : Color.white;
            if (GUILayout.Button(currentApproved ? "Approved - click to revoke" : "Approve texture setting",
                    GUILayout.Height(22f)))
            {
                Undo.RecordObject(recipe, currentApproved ? "Revoke texture setting" : "Approve texture setting");
                if (currentApproved) choice.RevokeApproval(); else choice.ApproveCurrentSettings();
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();
            }
            GUI.color = oldColor;
            EditorGUILayout.EndVertical();
        }

        private void DrawFinalWorkspace()
        {
            var effectiveMeshes = UvTileSplitPipeline.GetEffectiveMeshChoices(recipe);
            var meshApprovals = effectiveMeshes.Count(choice => choice.IsCurrentSelectionApproved);
            var materialApprovals = recipe.MaterialChoices.Count(choice => choice.IsCurrentMappingApproved);
            var textureApprovals = recipe.TextureChoices.Count(choice => choice.IsCurrentSettingsApproved);
            var mobileOverrides = recipe.TextureChoices.Count(choice => choice.MobileOverridesApplied);
            var selectedTriangles = effectiveMeshes.Sum(choice => (long)choice.SelectedTriangleCount);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Combined Quest prefab", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "This stage creates one deterministic prefab from the protected PC source, selected isolated meshes, generated Toon Standard materials, and applied Android/iOS texture settings.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(3f);
            EditorGUILayout.LabelField($"Meshes: {effectiveMeshes.Count} | Selected: {selectedTriangles:N0} triangles | " +
                                       $"Visual approvals: {meshApprovals}/{effectiveMeshes.Count}");
            EditorGUILayout.LabelField($"Materials: {materialApprovals}/{recipe.MaterialChoices.Count} approved and generated");
            EditorGUILayout.LabelField($"Textures: {textureApprovals}/{recipe.TextureChoices.Count} approved | " +
                                       $"Android/iOS applied: {mobileOverrides}/{recipe.TextureChoices.Count}");
            EditorGUILayout.LabelField($"Saved material-slot cache: {recipe.MaterialSlotCache.Count} renderer(s)");
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(recipe.CombinedQuestPrefabPath));
            if (GUILayout.Button("Save Current Material Slots", GUILayout.Height(24f)))
                RunDeferredEditorOperation(SaveCurrentMaterialSlots);
            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(recipe.MaterialSlotCache.Count == 0);
            if (GUILayout.Button("Clear Saved Slot Cache", GUILayout.Height(24f)))
            {
                var confirmed = EditorUtility.DisplayDialog("Clear saved material slots?",
                    "The next Stage 5 rebuild will use the Studio-generated material mapping instead of the current prefab slot assignments.",
                    "Clear Cache", "Cancel");
                if (confirmed) RunDeferredEditorOperation(() => FinalAssemblyPipeline.ClearMaterialSlotCache(recipe));
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(
                "Stage 5 automatically saves the current combined prefab's material slots before overwriting it, then restores them by exact renderer path. External materials are copied into the isolated mobile workspace; excluded renderers are not restored.",
                MessageType.Info);

            if (meshApprovals < effectiveMeshes.Count)
                EditorGUILayout.HelpBox(
                    "The combined prefab can be created for testing, but it will remain explicitly marked as visually unapproved until every current mesh selection is approved.",
                    MessageType.Warning);

            if (recipe.SourceBehaviorContract.DetectedBuildSystems.Count > 0)
                EditorGUILayout.HelpBox(
                    "Unresolved build-time systems: " +
                    string.Join(", ", recipe.SourceBehaviorContract.DetectedBuildSystems) +
                    ". Final behavior validation must inspect their resolved VRChat build output.",
                    MessageType.Warning);

            FinalAssemblyPipeline.CanBuild(recipe, out var reason);
            EditorGUI.BeginDisabledGroup(!string.IsNullOrEmpty(reason));
            if (GUILayout.Button("Build / Overwrite Combined Quest Prefab", GUILayout.Height(34f)))
                RunDeferredEditorOperation(BuildFinalAssembly);
            EditorGUI.EndDisabledGroup();
            if (!string.IsNullOrEmpty(reason)) EditorGUILayout.HelpBox(reason, MessageType.Warning);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(recipe.CombinedQuestPrefabPath));
            if (GUILayout.Button("Reveal Combined Prefab", GUILayout.Height(26f)))
            {
                var output = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
                if (output != null) EditorGUIUtility.PingObject(output);
            }
            if (GUILayout.Button("Open Assembly Report", GUILayout.Height(26f)))
            {
                var report = AssetDatabase.LoadAssetAtPath<TextAsset>(recipe.OutputRoot + "/Reports/FinalAssemblyReport.txt");
                if (report != null) AssetDatabase.OpenAsset(report);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "This is the stable editable Quest prefab. The Studio status remains working/not validated until animation remapping, resolved behavior checks, PhysBone decisions, and a real VRChat SDK mobile build pass.",
                MessageType.Info);
            DrawPendingResolvedFallbackNotice();
            if (!string.IsNullOrEmpty(recipe.FinalAssemblyStatus))
                EditorGUILayout.LabelField("Saved status: " + recipe.FinalAssemblyStatus,
                    EditorStyles.wordWrappedMiniLabel);
            if (!string.IsNullOrEmpty(recipe.CombinedQuestPrefabPath) &&
                GUILayout.Button("Continue to Behavior Review", GUILayout.Height(28f)))
                SetStage(StudioStage.Behavior);
        }

        private void BuildFinalAssembly()
        {
            try
            {
                var result = FinalAssemblyPipeline.Build(recipe);
                SaveRecipe();
                var status = result.StructuralValidationPassed ? "Structural validation passed" : "Structural validation failed";
                ShowNotification(new GUIContent(status + ". Combined Quest prefab saved."));
                if (!result.StructuralValidationPassed)
                    EditorUtility.DisplayDialog("Combined prefab validation failed",
                        string.Join("\n", result.Warnings), "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Combined Quest prefab failed", exception.Message, "OK");
            }
        }

        private void SaveCurrentMaterialSlots()
        {
            var count = FinalAssemblyPipeline.CaptureCurrentMaterialSlots(recipe);
            SaveRecipe();
            ShowNotification(new GUIContent($"Saved material slots for {count} renderers"));
        }

        private void RescanMobileToggleDependencies()
        {
            MobileContentPipeline.Analyze(recipe);
            ShowNotification(new GUIContent("Mobile toggle dependencies scanned"));
        }

        private void DrawPendingResolvedFallbackNotice()
        {
            var pending = recipe.RendererChoices.Count(choice => choice.RedirectsToFallback &&
                choice.MobileActivationBindingCount == 0);
            if (pending == 0) return;
            EditorGUILayout.HelpBox(
                $"{pending} fallback rule(s) are generated by a build system such as VRCFury. " +
                "The editable Stage 5/6 prefab can appear bald when those removed choices are selected. " +
                "Run Stage 7, then test the resolved mobile prefab; Stage 7 rewrites the generated activation states to the retained fallback.",
                MessageType.Warning);
        }

        private void DrawBehaviorWorkspace()
        {
            var choices = recipe.BehaviorCurveChoices;
            var exact = choices.Count(choice => choice.Kind == BehaviorCurveMappingKind.ExactProperty);
            var suggested = choices.Count(choice => choice.Kind == BehaviorCurveMappingKind.SuggestedTranslation);
            var geometry = choices.Count(choice => choice.Kind == BehaviorCurveMappingKind.GeometryActivation);
            var unsupported = choices.Count(choice => choice.Kind == BehaviorCurveMappingKind.Unsupported);
            var ready = choices.Count(choice => choice.IsReadyForBuild);
            var staticFallbacks = choices.Count(choice => choice.IsCurrentUnsupportedResolution);
            var unresolvedUnsupported = unsupported - staticFallbacks;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Isolated mobile behavior", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Copies the reachable controllers, animation clips, expression menus, and parameter assets into the generated workspace, then rewires only the combined Quest prefab. Material-swap keyframes are redirected to generated mobile materials.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField($"Shader-property decisions: {ready}/{choices.Count} ready | " +
                                       $"Exact: {exact} | Geometry toggles: {geometry} | Suggested: {suggested} | " +
                                       $"Static mobile fallbacks: {staticFallbacks} | Unresolved: {unresolvedUnsupported}");
            EditorGUILayout.EndVertical();

            EditorGUILayout.HelpBox(
                "Shader names are not enough to prove equal behavior: hue units, emission ranges, masks, and matcaps can use different meanings. Suggested translations remain review-gated. Unsupported controls require an explicit mobile fallback and are never silently discarded.",
                MessageType.Info);
            DrawPendingResolvedFallbackNotice();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan / Refresh Behavior Graph", GUILayout.Height(27f)))
                RunDeferredEditorOperation(AnalyzeBehavior);
            EditorGUI.BeginDisabledGroup(suggested == 0);
            if (GUILayout.Button("Approve All Suggested Translations", GUILayout.Height(27f)))
            {
                var confirmed = EditorUtility.DisplayDialog(
                    "Approve all suggested behavior translations?",
                    $"Approve {suggested} displayed shader-property translations?\n\n" +
                    "This only authorizes generated animation copies. The PC controllers and clips remain unchanged.",
                    "Approve All", "Cancel");
                if (confirmed)
                {
                    Undo.RecordObject(recipe, "Approve behavior translations");
                    foreach (var choice in choices.Where(choice =>
                                 choice.Kind == BehaviorCurveMappingKind.SuggestedTranslation))
                        choice.ApproveCurrentMapping();
                    SaveRecipe();
                }
            }
            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(choices.Count == 0);
            if (GUILayout.Button("Revoke Translation Approvals", GUILayout.Height(27f)))
            {
                Undo.RecordObject(recipe, "Revoke behavior translations");
                foreach (var choice in choices) choice.RevokeApproval();
                SaveRecipe();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(unresolvedUnsupported == 0);
            if (GUILayout.Button("Use Static Mobile Fallback for All Unsupported", GUILayout.Height(27f)))
            {
                var confirmed = EditorUtility.DisplayDialog(
                    "Use static mobile fallbacks?",
                    $"Resolve {unresolvedUnsupported} unsupported animated shader properties by keeping each generated " +
                    "mobile material at its current static appearance and removing only those unsupported curves from " +
                    "the copied mobile animation clips.\n\nThe PC prefab, materials, controllers, and clips remain unchanged. " +
                    "The affected animated effects will not change on mobile.",
                    "Use Static Mobile Fallbacks", "Cancel");
                if (confirmed)
                {
                    Undo.RecordObject(recipe, "Resolve unsupported mobile shader controls");
                    foreach (var choice in choices.Where(choice =>
                                 choice.Kind == BehaviorCurveMappingKind.Unsupported &&
                                 !choice.IsCurrentUnsupportedResolution))
                        choice.ResolveUnsupportedAsStaticMobileMaterial();
                    SaveRecipe();
                }
            }
            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(choices.Count == 0);
            if (GUILayout.Button("Clear All Behavior Decisions", GUILayout.Height(27f)))
            {
                Undo.RecordObject(recipe, "Clear behavior decisions");
                foreach (var choice in choices)
                {
                    choice.RevokeApproval();
                    choice.ClearUnsupportedResolution();
                }
                SaveRecipe();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (choices.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Press Scan / Refresh Behavior Graph. A zero-result scan is valid for avatars with no animated material properties; controllers, clips, menus, and parameters are still isolated during the build.",
                    MessageType.Info);
            }
            else
            {
                behaviorScroll = EditorGUILayout.BeginScrollView(behaviorScroll);
                foreach (var choice in choices) DrawBehaviorChoice(choice);
                EditorGUILayout.EndScrollView();
            }

            BehaviorConversionPipeline.CanBuild(recipe, out var reason);
            EditorGUI.BeginDisabledGroup(!string.IsNullOrEmpty(reason));
            if (GUILayout.Button("Build / Overwrite Isolated Behavior Draft", GUILayout.Height(34f)))
                RunDeferredEditorOperation(BuildBehavior);
            EditorGUI.EndDisabledGroup();
            if (!string.IsNullOrEmpty(reason)) EditorGUILayout.HelpBox(reason, MessageType.Warning);

            DrawManualPolishCheckpoint();

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(!recipe.BehaviorAppliedToCombined);
            if (GUILayout.Button("Reveal Behavior-Ready Prefab", GUILayout.Height(26f)))
            {
                var output = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.BehaviorPrefabPath);
                if (output != null) EditorGUIUtility.PingObject(output);
            }
            if (GUILayout.Button("Open Behavior Report", GUILayout.Height(26f)))
            {
                var report = AssetDatabase.LoadAssetAtPath<TextAsset>(recipe.OutputRoot + "/Reports/BehaviorConversionReport.txt");
                if (report != null) AssetDatabase.OpenAsset(report);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (recipe.SourceBehaviorContract.DetectedBuildSystems.Count > 0)
                EditorGUILayout.HelpBox(
                    "Detected build-time systems: " +
                    string.Join(", ", recipe.SourceBehaviorContract.DetectedBuildSystems) +
                    ". Their resolved output must be inspected on an Android build-target clone before this can be treated as Quest behavior evidence.",
                    MessageType.Warning);
            EditorGUILayout.HelpBox(
                "This stage does not claim upload readiness. The next validation stage resolves optional build systems, checks mobile component restrictions, runs an actual VRChat SDK Android build, and measures the built bundle.",
                MessageType.Info);
            if (!string.IsNullOrEmpty(recipe.BehaviorStatus))
                EditorGUILayout.LabelField("Saved status: " + recipe.BehaviorStatus,
                    EditorStyles.wordWrappedMiniLabel);
            var checkpointReady = recipe.BehaviorAppliedToCombined &&
                                  !string.IsNullOrEmpty(recipe.ManualPolishCheckpointUtc) &&
                                  recipe.ManualPolishTextureCount == recipe.TextureChoices.Count;
            EditorGUI.BeginDisabledGroup(!checkpointReady);
            if (GUILayout.Button("Continue to Android/iOS Validation", GUILayout.Height(28f)))
            {
                if (ManualPolishPipeline.ValidateCheckpoint(recipe, out var checkpointReason))
                    SetStage(StudioStage.Validate);
                else
                    EditorUtility.DisplayDialog("Manual polish checkpoint is stale", checkpointReason, "OK");
            }
            EditorGUI.EndDisabledGroup();
        }

        private void DrawManualPolishCheckpoint()
        {
            EditorGUILayout.Space(5f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Manual material polish checkpoint", EditorStyles.boldLabel);
            if (!recipe.BehaviorAppliedToCombined)
            {
                EditorGUILayout.HelpBox(
                    "Build Stage 6 first. The manual polish checkpoint becomes available after the complete mobile prefab and behavior assets exist.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Adjust the generated mobile prefab's material slots, Toon Standard settings, matcaps, emission, transparency, and colors. Then save here. The Studio isolates external slot materials and rescans the final prefab for every texture added during manual work.",
                    MessageType.Info);
                EditorGUILayout.LabelField("Status: " + recipe.ManualPolishStatus,
                    EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty(recipe.ManualPolishCheckpointUtc))
                    EditorGUILayout.LabelField(
                        $"Saved checkpoint: {recipe.ManualPolishTextureCount} final texture(s)",
                        EditorStyles.miniLabel);
                if (GUILayout.Button("Save Manual Work & Rescan", GUILayout.Height(34f)))
                    RunDeferredEditorOperation(SaveManualWorkAndRescan);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawBehaviorChoice(BehaviorCurveMappingChoice choice)
        {
            var ready = choice.IsReadyForBuild;
            EditorGUILayout.BeginVertical(ready ? "SelectionRect" : EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(choice.SourceProperty, EditorStyles.miniBoldLabel, GUILayout.Width(290f));
            EditorGUILayout.LabelField("->", GUILayout.Width(18f));
            EditorGUILayout.LabelField(string.IsNullOrEmpty(choice.TargetProperties) ? "No safe target" : choice.TargetProperties,
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"{choice.BindingCount} binding(s)", GUILayout.Width(85f));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(choice.Summary, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(
                $"Mode: {choice.Kind} | Scale: {choice.Scale:0.######} | Offset: {choice.Offset:0.######}" +
                (choice.NormalizeStrength ? " | Normalize strength" : string.Empty), EditorStyles.miniLabel);
            if (choice.SourceClipPaths.Count > 0)
            {
                var clipNames = choice.SourceClipPaths.Select(path => Path.GetFileNameWithoutExtension(path)).ToArray();
                EditorGUILayout.LabelField("Affected mobile clip copies: " + string.Join(", ", clipNames),
                    EditorStyles.wordWrappedMiniLabel);
            }
            if (choice.Kind == BehaviorCurveMappingKind.SuggestedTranslation)
            {
                var oldColor = GUI.color;
                GUI.color = ready ? SafeColor : ReviewColor;
                if (GUILayout.Button(ready ? "Approved - click to revoke" : "Approve this translation",
                        GUILayout.Height(21f)))
                {
                    Undo.RecordObject(recipe, ready ? "Revoke behavior translation" : "Approve behavior translation");
                    if (ready) choice.RevokeApproval(); else choice.ApproveCurrentMapping();
                    SaveRecipe();
                }
                GUI.color = oldColor;
            }
            else if (choice.Kind == BehaviorCurveMappingKind.Unsupported)
            {
                EditorGUILayout.LabelField("Feature type: " + GetUnsupportedFeatureLabel(choice.SourceProperty),
                    EditorStyles.miniBoldLabel);
                EditorGUILayout.HelpBox(ready
                        ? "Static mobile fallback selected. Stage 6 will keep the generated material's current appearance and remove this property only from copied mobile clips. The PC source remains unchanged."
                        : "Toon Standard has no verified equivalent. Choose a static mobile fallback to continue, or leave this unresolved to keep the build blocked.",
                    ready ? MessageType.Warning : MessageType.Error);
                var oldColor = GUI.color;
                GUI.color = ready ? SafeColor : ReviewColor;
                if (GUILayout.Button(ready
                        ? "Static mobile fallback selected - click to undo"
                        : "Keep current mobile material and remove copied curves", GUILayout.Height(23f)))
                {
                    if (ready)
                    {
                        Undo.RecordObject(recipe, "Clear unsupported mobile fallback");
                        choice.ClearUnsupportedResolution();
                        SaveRecipe();
                    }
                    else if (EditorUtility.DisplayDialog(
                                 "Keep this effect static on mobile?",
                                 choice.SourceProperty + " has no verified Toon Standard equivalent.\n\n" +
                                 "Stage 6 will preserve the generated mobile material as currently configured and remove " +
                                 "this curve only from copied mobile clips. The original PC assets remain untouched.",
                                 "Use Static Mobile Fallback", "Cancel"))
                    {
                        Undo.RecordObject(recipe, "Resolve unsupported mobile shader control");
                        choice.ResolveUnsupportedAsStaticMobileMaterial();
                        SaveRecipe();
                    }
                }
                GUI.color = oldColor;
            }
            EditorGUILayout.EndVertical();
        }

        private static string GetUnsupportedFeatureLabel(string property)
        {
            if (property.IndexOf("DecalBlendAlpha", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Decal or overlay visibility";
            if (property.IndexOf("DecalHueShift", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Decal or overlay color";
            if (property.IndexOf("Dissolve", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Dissolve transition";
            if (property.IndexOf("Glitter", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Glitter or shine";
            if (property.IndexOf("Matcap", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Secondary matcap color";
            if (property.IndexOf("Saturation", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Color saturation";
            if (property.IndexOf("MainColor", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Animated material color";
            return "Unsupported PC shader animation";
        }

        private void AnalyzeBehavior()
        {
            try
            {
                BehaviorConversionPipeline.Analyze(recipe);
                SaveRecipe();
                ShowNotification(new GUIContent($"Reviewed {recipe.BehaviorCurveChoices.Count} shader properties"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Behavior scan failed", exception.Message, "OK");
            }
        }

        private void BuildBehavior()
        {
            try
            {
                var result = BehaviorConversionPipeline.Build(recipe);
                SaveRecipe();
                ShowNotification(new GUIContent(result.ValidationPassed
                    ? "Isolated behavior applied to combined prefab"
                    : "Behavior validation failed"));
                if (!result.ValidationPassed)
                    EditorUtility.DisplayDialog("Behavior validation failed", string.Join("\n", result.Warnings), "OK");
                else
                {
                    // Do not open a modal dialog from the long-running delayCall
                    // callback. Unity 2022 can report a misleading
                    // "operation completed successfully" dialog error here even
                    // though the conversion passed. The Stage 6 panel already
                    // exposes the manual-polish instructions and save button.
                    var output = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
                    if (output != null)
                    {
                        Selection.activeObject = output;
                        EditorGUIUtility.PingObject(output);
                    }
                    ShowNotification(new GUIContent(
                        "Stage 6 complete — adjust materials, then Save Manual Work & Rescan"));
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Behavior conversion failed", exception.Message, "OK");
            }
        }

        private void SaveManualWorkAndRescan()
        {
            try
            {
                var result = ManualPolishPipeline.SaveAndRescan(recipe);
                SaveRecipe();
                if (result.Complete)
                {
                    EditorUtility.DisplayDialog("Manual polish saved",
                        $"Saved {result.RendererCount} renderer(s), {result.MaterialCount} material(s), and " +
                        $"{result.TextureCount} texture setting(s).\n" +
                        $"Cleaned {result.SanitizedMaterialCount} generated material asset(s) of unused shader data.\n\n" +
                        "Android and iOS overrides are present and match the approved profiles. Stage 7 is unlocked.",
                        "Continue");
                    ShowNotification(new GUIContent("Manual polish checkpoint saved"));
                    return;
                }

                var review = EditorUtility.DisplayDialog("Final texture review required",
                    $"The final polished prefab contains {result.TextureCount} texture(s). " +
                    $"{result.NewTextureCount} were newly discovered during this rescan.\n\n" +
                    result.BlockingReason + "\n\nReview and approve the displayed settings, apply the Android/iOS overrides, then return to Stage 6 and save the checkpoint again.",
                    "Review Textures", "Stay on Stage 6");
                if (review) SetStage(StudioStage.Textures);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Manual polish save failed", exception.Message, "OK");
            }
        }

        private void DrawMobileValidationWorkspace()
        {
            var activeTarget = EditorUserBuildSettings.activeBuildTarget;
            var isMobile = MobilePlatformValidationPipeline.IsMobileTarget(activeTarget);
            var errors = recipe.MobileValidationIssues.Count(issue => issue.Severity == MobileValidationSeverity.Error);
            var warnings = recipe.MobileValidationIssues.Count(issue => issue.Severity == MobileValidationSeverity.Warning);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Fix and validate the mobile upload prefab", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Runs VRChat/VRCFury on a disposable in-memory copy, records exact mobile issues, and leaves the clean editable upload prefab unprocessed.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("Active build target: " + activeTarget,
                isMobile ? EditorStyles.boldLabel : EditorStyles.label);
            EditorGUILayout.EndVertical();

            if (!isMobile)
                EditorGUILayout.HelpBox(
                    "Windows is still active, so VRCFury would resolve its PC behavior. Choose a mobile target explicitly before using that result as Android/iOS evidence.",
                    MessageType.Warning);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(activeTarget == BuildTarget.Android ||
                                         !MobilePlatformValidationPipeline.IsTargetSupported(BuildTarget.Android));
            if (GUILayout.Button("Switch Project to Android...", GUILayout.Height(29f)))
                RunDeferredEditorOperation(() => ConfirmAndSwitchMobileTarget(BuildTarget.Android));
            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(activeTarget == BuildTarget.iOS ||
                                         !MobilePlatformValidationPipeline.IsTargetSupported(BuildTarget.iOS));
            if (GUILayout.Button("Switch Project to iOS...", GUILayout.Height(29f)))
                RunDeferredEditorOperation(() => ConfirmAndSwitchMobileTarget(BuildTarget.iOS));
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (!MobilePlatformValidationPipeline.IsTargetSupported(BuildTarget.Android))
                EditorGUILayout.HelpBox("Android Build Support is not installed for this Unity editor.", MessageType.Error);
            if (!MobilePlatformValidationPipeline.IsTargetSupported(BuildTarget.iOS))
                EditorGUILayout.HelpBox("iOS Build Support is not installed; Android validation can still be completed.", MessageType.Info);

            MobilePlatformValidationPipeline.CanRunResolvedAudit(recipe, out var auditReason);
            EditorGUI.BeginDisabledGroup(!string.IsNullOrEmpty(auditReason));
            if (GUILayout.Button("Run / Refresh Mobile Audit", GUILayout.Height(34f)))
                RunDeferredEditorOperation(RunResolvedMobileAudit);
            EditorGUI.EndDisabledGroup();
            if (!string.IsNullOrEmpty(auditReason))
            {
                EditorGUILayout.HelpBox(auditReason, MessageType.Warning);
                if (auditReason.IndexOf("build marker", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    GUILayout.Button("Repair Upload Marker Without Rebuilding Stage 6", GUILayout.Height(28f)))
                    RunDeferredEditorOperation(SaveManualWorkAndRescan);
            }

            MobilePlatformValidationPipeline.CanRunSdkBuild(recipe, out var continueReason);
            EditorGUI.BeginDisabledGroup(!string.IsNullOrEmpty(continueReason));
            if (GUILayout.Button("Continue to Stage 8 - Official Build", GUILayout.Height(32f)))
                SetStage(StudioStage.Build);
            EditorGUI.EndDisabledGroup();
            if (!string.IsNullOrEmpty(continueReason))
                EditorGUILayout.LabelField("Stage 8 locked: " + continueReason,
                    EditorStyles.wordWrappedMiniLabel);

            if (!string.IsNullOrEmpty(recipe.MobileResolvedAuditUtc))
            {
                var auditMatchesActiveTarget = string.Equals(recipe.MobileResolvedAuditTarget,
                    activeTarget.ToString(), StringComparison.Ordinal);
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Mobile target: " + recipe.MobileResolvedAuditTarget +
                                           (auditMatchesActiveTarget ? string.Empty : " (previous)"), GUILayout.Width(220f));
                EditorGUILayout.LabelField(auditMatchesActiveTarget
                        ? recipe.MobileResolvedAuditPassed ? "PASS" : "FAIL"
                        : "STALE",
                    auditMatchesActiveTarget && recipe.MobileResolvedAuditPassed
                        ? EditorStyles.boldLabel
                        : EditorStyles.label, GUILayout.Width(55f));
                EditorGUILayout.LabelField($"Errors: {errors} | Warnings: {warnings}");
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Reveal upload prefab", GUILayout.Width(150f)))
                {
                    var resolved = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.MobileResolvedPrefabPath);
                    if (resolved != null) EditorGUIUtility.PingObject(resolved);
                }
                if (GUILayout.Button("Open audit report", GUILayout.Width(130f)))
                {
                    var report = AssetDatabase.LoadAssetAtPath<TextAsset>(recipe.OutputRoot + "/Reports/MobileResolvedAuditReport.txt");
                    if (report != null) AssetDatabase.OpenAsset(report);
                }
                EditorGUILayout.EndHorizontal();

                if (!auditMatchesActiveTarget)
                    EditorGUILayout.HelpBox(
                        $"This result belongs to {recipe.MobileResolvedAuditTarget}. Run the audit again for the active {activeTarget} target before Stage 8.",
                        MessageType.Warning);

                if (auditMatchesActiveTarget && recipe.MobileValidationIssues.Count > 0)
                {
                    validationIssueListHeight = Mathf.Clamp(validationIssueListHeight, 80f,
                        Mathf.Max(100f, position.height * 0.55f));
                    validationScroll = EditorGUILayout.BeginScrollView(validationScroll,
                        GUILayout.Height(validationIssueListHeight));
                    foreach (var issue in recipe.MobileValidationIssues) DrawMobileValidationIssue(issue);
                    EditorGUILayout.EndScrollView();
                    DrawHorizontalSplitter("ValidationIssues", ref validationIssueListHeight, 80f,
                        Mathf.Max(100f, position.height * 0.55f), 230f);
                }
                else if (auditMatchesActiveTarget)
                    EditorGUILayout.HelpBox("The clean mobile upload prefab passed all available checks.", MessageType.Info);

                if (auditMatchesActiveTarget) DrawMobileComponentRepairPanel();
            }

            if (!string.IsNullOrEmpty(recipe.MobileValidationStatus))
                EditorGUILayout.LabelField("Saved status: " + recipe.MobileValidationStatus,
                    EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawMobileBuildWorkspace()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Stage 8 - Official mobile build", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Builds the clean upload prefab through VRChat's official SDK pipeline. VRCFury and Mobile Avatar Studio each run exactly once. This measures the bundle but does not upload it.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open VRChat SDK Control Panel", GUILayout.Height(29f)))
                EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
            MobilePlatformValidationPipeline.CanRunSdkBuild(recipe, out var sdkReason);
            EditorGUI.BeginDisabledGroup(!string.IsNullOrEmpty(sdkReason));
            if (GUILayout.Button("Build / Overwrite Official Mobile Bundle", GUILayout.Height(29f)))
                RunDeferredEditorOperation(BuildOfficialMobileSdkBundle);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(sdkReason)) EditorGUILayout.HelpBox(sdkReason, MessageType.Warning);

            if (!string.IsNullOrEmpty(recipe.MobileSdkBuildUtc))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Last SDK target: " + recipe.MobileSdkBuildTarget);
                EditorGUILayout.LabelField("Download size: " +
                                           MobilePlatformValidationPipeline.FormatBytes(recipe.MobileSdkDownloadBytes));
                EditorGUILayout.LabelField("Uncompressed size: " +
                                           MobilePlatformValidationPipeline.FormatBytes(recipe.MobileSdkUncompressedBytes));
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Reveal Current Bundle", GUILayout.Height(24f)) &&
                    File.Exists(recipe.MobileSdkBundlePath))
                    EditorUtility.RevealInFinder(recipe.MobileSdkBundlePath);
                if (GUILayout.Button("Open SDK Build Report", GUILayout.Height(24f)))
                {
                    var report = AssetDatabase.LoadAssetAtPath<TextAsset>(recipe.OutputRoot + "/Reports/MobileSdkBuildReport.txt");
                    if (report != null) AssetDatabase.OpenAsset(report);
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.HelpBox(
                "VRChat mobile size limits are 10 MiB download and 40 MiB uncompressed for the supported Android/iOS targets. If this passes, use VRChat Build & Test, inspect the avatar, then upload manually.",
                MessageType.Info);
            if (!string.IsNullOrEmpty(recipe.MobileValidationStatus))
                EditorGUILayout.LabelField("Saved status: " + recipe.MobileValidationStatus,
                    EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawMobileValidationIssue(MobileValidationIssue issue)
        {
            var type = issue.Severity == MobileValidationSeverity.Error ? MessageType.Error :
                issue.Severity == MobileValidationSeverity.Warning ? MessageType.Warning : MessageType.Info;
            var location = string.IsNullOrEmpty(issue.ObjectPath) ? string.Empty : "\n" + issue.ObjectPath;
            EditorGUILayout.HelpBox(issue.Category + ": " + issue.Message + location, type);
            if (!string.IsNullOrEmpty(issue.ObjectPath) && GUILayout.Button("Select this issue", GUILayout.Height(21f)))
                SelectValidationIssue(issue);
            else if (string.IsNullOrEmpty(issue.ObjectPath) &&
                     (issue.Category == "Particles" || issue.Category == "PhysBones" ||
                      issue.Category == "Contacts"))
                EditorGUILayout.LabelField("Exact components are listed in Mobile issue repair below.",
                    EditorStyles.wordWrappedMiniLabel);
        }

        private void SelectValidationIssue(MobileValidationIssue issue)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(issue.ObjectPath);
            if (asset != null)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
                return;
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.CombinedQuestPrefabPath);
            if (prefab == null) return;
            AssetDatabase.OpenAsset(prefab);
            EditorApplication.delayCall += () =>
            {
                var transform = string.IsNullOrEmpty(issue.ObjectPath)
                    ? prefab.transform
                    : prefab.transform.Find(issue.ObjectPath);
                Selection.activeObject = transform != null ? (UnityEngine.Object)transform.gameObject : prefab;
                EditorGUIUtility.PingObject(Selection.activeObject);
            };
        }

        private void DrawMobileComponentRepairPanel()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Mobile issue repair", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Review exact components from the clean mobile prefab. Applied removals move into a separate restore tab and remain recoverable from the generated cache. The protected PC source is never changed.",
                EditorStyles.wordWrappedLabel);

            var choices = recipe.MobileComponentRepairChoices;
            var available = choices.Where(choice => choice.PresentInUploadPrefab).ToArray();
            var removedChoices = choices.Where(choice => !choice.PresentInUploadPrefab &&
                                                          choice.RemoveFromMobile).ToArray();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh exact component list", GUILayout.Height(24f)))
                RunDeferredEditorOperation(RefreshMobileRepairChoices);
            EditorGUI.BeginDisabledGroup(!available.Any(choice => choice.Category == "Particles"));
            if (GUILayout.Button("Select all ParticleSystems", GUILayout.Height(24f)))
            {
                Undo.RecordObject(recipe, "Select mobile particles for removal");
                foreach (var choice in available.Where(choice => choice.Category == "Particles"))
                    choice.RemoveFromMobile = true;
                SaveRecipe();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            repairTab = GUILayout.Toolbar(Mathf.Clamp(repairTab, 0, 1), new[]
            {
                $"Available ({available.Length})",
                $"Removed / Restore ({removedChoices.Length})"
            }, GUILayout.Height(24f));

            if (choices.Count == 0)
                EditorGUILayout.HelpBox("No removable mobile components were found in the clean upload prefab.",
                    MessageType.Info);
            else if (repairTab == 0)
            {
                var presentPhysBones = available.Count(choice => choice.ComponentTypeName.EndsWith(".VRCPhysBone",
                    StringComparison.Ordinal));
                var selected = available.Count(choice => choice.RemoveFromMobile);
                EditorGUILayout.LabelField(
                    $"Available: {available.Length} | PhysBones: {presentPhysBones}/8 | Selected removals: {selected}",
                    EditorStyles.miniBoldLabel);

                repairListHeight = Mathf.Clamp(repairListHeight, 100f,
                    Mathf.Max(120f, position.height * 0.62f));
                repairScroll = EditorGUILayout.BeginScrollView(repairScroll,
                    GUILayout.Height(repairListHeight));
                string category = null;
                foreach (var choice in available)
                {
                    if (!string.Equals(category, choice.Category, StringComparison.Ordinal))
                    {
                        category = choice.Category;
                        EditorGUILayout.Space(3f);
                        EditorGUILayout.LabelField(category, EditorStyles.boldLabel);
                    }
                    EditorGUILayout.BeginHorizontal(choice.RemoveFromMobile
                        ? "SelectionRect"
                        : EditorStyles.helpBox);
                    var remove = EditorGUILayout.ToggleLeft("Remove", choice.RemoveFromMobile,
                        GUILayout.Width(72f));
                    if (remove != choice.RemoveFromMobile)
                    {
                        Undo.RecordObject(recipe, "Change mobile component repair");
                        choice.RemoveFromMobile = remove;
                        SaveRecipe();
                    }
                    EditorGUILayout.LabelField(choice.DisplayName, EditorStyles.miniLabel);
                    if (choice.EstimatedAffectedTransforms > 0)
                        EditorGUILayout.LabelField($"~{choice.EstimatedAffectedTransforms} transforms",
                            EditorStyles.miniLabel, GUILayout.Width(105f));
                    if (GUILayout.Button("Select", GUILayout.Width(58f)))
                        MobileComponentRepairPipeline.SelectInUploadPrefab(recipe, choice);
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
                DrawHorizontalSplitter("RepairAvailable", ref repairListHeight, 100f,
                    Mathf.Max(120f, position.height * 0.62f), 310f);

                var markedPresent = available.Count(choice => choice.RemoveFromMobile);
                EditorGUI.BeginDisabledGroup(markedPresent == 0);
                if (GUILayout.Button($"Apply {markedPresent} Selected Removal(s) to Mobile Prefab",
                        GUILayout.Height(30f)))
                    RunDeferredEditorOperation(ApplySelectedMobileRepairs);
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.HelpBox(
                    "PhysBones are not auto-selected because hair, clothing, and accessories have different priorities. Select the least important chains until the audit reports 8 or fewer components and 64 or fewer affected transforms.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.LabelField(
                    $"Cached removals: {removedChoices.Length} | Restore cache: " +
                    (string.IsNullOrEmpty(recipe.MobileComponentRestoreCachePath) ? "created on first restore" : "saved"),
                    EditorStyles.miniBoldLabel);
                if (removedChoices.Length == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Applied removals will appear here. Restoring adds the cached component back to its original GameObject with its saved settings and references.",
                        MessageType.Info);
                }
                else
                {
                    repairListHeight = Mathf.Clamp(repairListHeight, 100f,
                        Mathf.Max(120f, position.height * 0.62f));
                    repairScroll = EditorGUILayout.BeginScrollView(repairScroll,
                        GUILayout.Height(repairListHeight));
                    string category = null;
                    foreach (var choice in removedChoices)
                    {
                        if (!string.Equals(category, choice.Category, StringComparison.Ordinal))
                        {
                            category = choice.Category;
                            EditorGUILayout.Space(3f);
                            EditorGUILayout.LabelField(category, EditorStyles.boldLabel);
                        }
                        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                        EditorGUILayout.LabelField(choice.DisplayName, EditorStyles.miniLabel);
                        if (choice.EstimatedAffectedTransforms > 0)
                            EditorGUILayout.LabelField($"~{choice.EstimatedAffectedTransforms} transforms",
                                EditorStyles.miniLabel, GUILayout.Width(105f));
                        if (GUILayout.Button("Restore", GUILayout.Width(72f)))
                            RestoreMobileComponent(choice);
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUILayout.EndScrollView();
                    DrawHorizontalSplitter("RepairRemoved", ref repairListHeight, 100f,
                        Mathf.Max(120f, position.height * 0.62f), 310f);
                    EditorGUILayout.HelpBox(
                        "Restore changes only the generated mobile prefab. After restoring, save the Stage 6 checkpoint again and rerun Stage 7.",
                        MessageType.Info);
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void RestoreMobileComponent(MobileComponentRepairChoice choice)
        {
            if (choice == null || !EditorUtility.DisplayDialog(
                    "Restore mobile component?",
                    "Restore this cached component to its original object in the generated mobile prefab?\n\n" +
                    choice.DisplayName + "\n\nThe protected PC source is not changed.",
                    "Restore", "Cancel")) return;
            try
            {
                var restored = MobileComponentRepairPipeline.RestoreToUploadPrefab(recipe, choice);
                SaveRecipe();
                ShowNotification(new GUIContent($"Restored {restored} mobile component(s)"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Component restore failed", exception.Message, "OK");
            }
        }

        private void RefreshMobileRepairChoices()
        {
            try
            {
                MobileComponentRepairPipeline.Refresh(recipe);
                SaveRecipe();
                ShowNotification(new GUIContent("Mobile component list refreshed"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Component scan failed", exception.Message, "OK");
            }
        }

        private void ApplySelectedMobileRepairs()
        {
            var count = recipe.MobileComponentRepairChoices.Count(choice => choice.PresentInUploadPrefab &&
                                                                     choice.RemoveFromMobile);
            if (count == 0) return;
            if (!EditorUtility.DisplayDialog(
                    "Remove selected components from the mobile prefab?",
                    count + " selected component(s) will be removed only from the generated mobile upload prefab. " +
                    "The PC source remains unchanged. Stage 7 must be rerun afterward.",
                    "Remove from Mobile", "Cancel")) return;
            try
            {
                var removed = MobileComponentRepairPipeline.ApplyMarkedRepairsToUploadPrefab(recipe);
                SaveRecipe();
                ShowNotification(new GUIContent($"Removed {removed} mobile component(s)"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Mobile repair failed", exception.Message, "OK");
            }
        }

        private void ConfirmAndSwitchMobileTarget(BuildTarget target)
        {
            if (!EditorUtility.DisplayDialog(
                    "Switch active build target to " + target + "?",
                    "Unity may reimport platform-dependent assets and shaders. Progress is saved in the recipe first. " +
                    "This does not modify the protected PC prefab or upload anything.",
                    "Switch Target", "Cancel")) return;
            try
            {
                SaveRecipe();
                MobilePlatformValidationPipeline.SwitchTarget(recipe, target);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Platform switch failed", exception.Message, "OK");
            }
        }

        private void RunResolvedMobileAudit()
        {
            try
            {
                var result = MobilePlatformValidationPipeline.RunResolvedAudit(recipe);
                SaveRecipe();
                ShowNotification(new GUIContent(result.Passed
                    ? "Resolved mobile audit passed"
                    : "Resolved mobile audit found blocking issues"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Resolved mobile audit failed", exception.Message, "OK");
            }
        }

        private async void BuildOfficialMobileSdkBundle()
        {
            try
            {
                var result = await MobilePlatformValidationPipeline.BuildWithOfficialSdk(recipe);
                SaveRecipe();
                Repaint();
                ShowNotification(new GUIContent(result.DownloadLimitPassed && result.UncompressedLimitPassed
                    ? "Official mobile SDK build passed size limits"
                    : "Official SDK build exceeded a size limit"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                recipe.MobileValidationStatus = "Official SDK mobile build failed: " + exception.Message;
                SaveRecipe();
                EditorUtility.DisplayDialog("Official mobile SDK build failed", exception.Message, "OK");
            }
        }

        private void ApplyTextureCategoryPreset(MobileTextureCategory category,
            IReadOnlyList<TextureConversionChoice> choices)
        {
            var index = (int)category;
            Undo.RecordObject(recipe, "Apply texture category preset");
            foreach (var choice in choices)
            {
                choice.TargetMaxSize = textureCategoryMaxSizes[index];
                choice.Compression = textureCategoryCompressions[index];
                choice.RevokeApproval();
            }
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
        }

        private void ApplyRecommendedTextureCategoryPreset(MobileTextureCategory category,
            IReadOnlyList<TextureConversionChoice> choices)
        {
            var index = (int)category;
            textureCategoryCompressions[index] = TextureConversionPipeline.RecommendedCompression(category);
            textureCategoryMaxSizes[index] = category == MobileTextureCategory.BaseColor ||
                                             category == MobileTextureCategory.Normal ||
                                             category == MobileTextureCategory.MixedReview
                ? 1024
                : 512;
            Undo.RecordObject(recipe, "Apply recommended texture category preset");
            foreach (var choice in choices)
            {
                choice.TargetMaxSize = TextureConversionPipeline.RecommendedMaxSize(category,
                    choice.SourceWidth, choice.SourceHeight);
                choice.Compression = textureCategoryCompressions[index];
                choice.RevokeApproval();
            }
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssets();
        }

        private void ScanTextures()
        {
            try
            {
                if (recipe.BehaviorAppliedToCombined &&
                    !string.IsNullOrEmpty(recipe.ManualPolishFinalScanUtc))
                    TextureConversionPipeline.AnalyzeFinalPrefab(recipe);
                else
                    TextureConversionPipeline.Analyze(recipe);
                ShowNotification(new GUIContent($"Scanned {recipe.TextureChoices.Count} textures"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Texture scan failed", exception.Message, "OK");
            }
        }

        private void ApplyTextureSettings()
        {
            try
            {
                TextureConversionPipeline.ApplyMobileOverrides(recipe);
                ShowNotification(new GUIContent("Android/iOS texture overrides applied"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Texture override failed", exception.Message, "OK");
            }
        }

        private void RestoreTextureSettings()
        {
            if (!EditorUtility.DisplayDialog(
                    "Restore previous Android/iOS texture settings?",
                    "This restores the Android and iOS settings captured before Mobile Avatar Studio changed them. PC settings are not involved.",
                    "Restore",
                    "Cancel")) return;
            try
            {
                TextureConversionPipeline.RestoreMobileOverrides(recipe);
                ShowNotification(new GUIContent("Previous Android/iOS texture settings restored"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Texture restore failed", exception.Message, "OK");
            }
        }

        private void ShareDuplicateUvTextures()
        {
            try
            {
                var count = TextureConversionPipeline.ShareDuplicateUvSplitTextures(recipe);
                ShowNotification(new GUIContent(count > 0
                    ? $"Shared {count} duplicate UV texture reference(s)"
                    : "No duplicate UV textures found"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("UV texture sharing failed", exception.Message, "OK");
            }
        }

        private void DrawSummary()
        {
            var sourceTriangles = recipe.RendererChoices.Sum(choice => (long)choice.SourceTriangleCount);
            var retained = UvTileSplitPipeline.GetEffectiveMeshChoices(recipe).ToArray();
            var selectedTriangles = retained.Sum(choice => (long)choice.SelectedTriangleCount);
            var generatedCount = retained.Count(choice => choice.Candidates.Count > 0);
            var approvedCount = retained.Count(choice => choice.IsCurrentSelectionApproved);

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Meshes: {retained.Length}", GUILayout.Width(105f));
            EditorGUILayout.LabelField($"Original: {sourceTriangles:N0} tris", GUILayout.Width(160f));
            EditorGUILayout.LabelField($"Selected: {selectedTriangles:N0} tris", GUILayout.Width(160f));
            var reduction = sourceTriangles <= 0 ? 0f : 1f - selectedTriangles / (float)sourceTriangles;
            EditorGUILayout.LabelField($"Reduction: {reduction:P1}", GUILayout.Width(130f));
            EditorGUILayout.LabelField($"Candidates: {generatedCount}/{retained.Length}");
            EditorGUILayout.LabelField($"Approved: {approvedCount}/{retained.Length}");
            EditorGUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(recipe.MeshGenerationState))
                EditorGUILayout.LabelField("Saved generation state: " + recipe.MeshGenerationState,
                    EditorStyles.miniLabel);
        }

        private void DrawMeshListAndCandidates()
        {
            DrawUvTileSplitWorkspace();
            if (showUvTileSplit)
                DrawHorizontalSplitter("UvTileList", ref uvTileListHeight, 70f,
                    Mathf.Max(90f, position.height * 0.55f), 245f);
            EditorGUILayout.Space(3f);
            DrawMobileContentSummary();
            EditorGUILayout.Space(3f);
            EditorGUILayout.BeginHorizontal();
            search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField);
            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(45f))) search = string.Empty;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Check All", EditorStyles.miniButtonLeft))
            {
                foreach (var choice in UvTileSplitPipeline.GetEffectiveMeshChoices(recipe))
                    choice.GenerateCandidates = true;
                SaveRecipe();
            }
            if (GUILayout.Button("Check None", EditorStyles.miniButtonMid))
            {
                foreach (var choice in UvTileSplitPipeline.GetEffectiveMeshChoices(recipe)) choice.GenerateCandidates = false;
                SaveRecipe();
            }
            EditorGUI.BeginDisabledGroup(selectedRendererIndex < 0 || selectedRendererIndex >= recipe.RendererChoices.Count);
            if (GUILayout.Button("Only Selected", EditorStyles.miniButtonRight))
            {
                foreach (var choice in UvTileSplitPipeline.GetEffectiveMeshChoices(recipe)) choice.GenerateCandidates = false;
                var selected = recipe.RendererChoices[selectedRendererIndex];
                var split = UvTileSplitPipeline.FindEnabledSplit(recipe, selected.TransformPath);
                if (split == null) selected.GenerateCandidates = true;
                else foreach (var piece in split.Pieces.Where(item => item.KeepOnMobile))
                    piece.MeshChoice.GenerateCandidates = true;
                SaveRecipe();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            rendererListHeight = Mathf.Clamp(rendererListHeight, 110f,
                Mathf.Max(140f, position.height * 0.60f));
            rendererScroll = EditorGUILayout.BeginScrollView(rendererScroll,
                GUILayout.Height(rendererListHeight));
            for (var index = 0; index < recipe.RendererChoices.Count; index++)
            {
                var choice = recipe.RendererChoices[index];
                var split = UvTileSplitPipeline.FindEnabledSplit(recipe, choice.TransformPath);
                if (split != null)
                {
                    DrawSplitRendererRows(index, choice, split);
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(search) &&
                    choice.DisplayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;

                EditorGUILayout.BeginHorizontal(index == selectedRendererIndex ? "SelectionRect" : GUIStyle.none);
                EditorGUI.BeginChangeCheck();
                EditorGUI.BeginDisabledGroup(choice.IsExcludedFromMobile);
                choice.GenerateCandidates = EditorGUILayout.Toggle(choice.GenerateCandidates, GUILayout.Width(18f));
                EditorGUI.EndDisabledGroup();
                if (EditorGUI.EndChangeCheck()) SaveRecipe();
                if (GUILayout.Button(choice.DisplayName, EditorStyles.label))
                {
                    selectedRendererIndex = index;
                    RebuildPreview();
                    RememberWorkspaceState();
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(choice.IsExcludedFromMobile ? "MOBILE EXCLUDED" : $"{choice.SelectedTriangleCount:N0}",
                    EditorStyles.miniLabel, GUILayout.Width(105f));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
            DrawHorizontalSplitter("RendererList", ref rendererListHeight, 110f,
                Mathf.Max(140f, position.height * 0.60f), Mathf.Max(155f, position.height * 0.31f));

            EditorGUILayout.Space(3f);
            DrawGenerationSettings();
            EditorGUILayout.Space(4f);
            DrawSelectedRenderer();
        }

        private void DrawSplitRendererRows(int rendererIndex, RendererMeshChoice sourceChoice,
            UvTileSplitRenderer split)
        {
            var parentMatches = string.IsNullOrWhiteSpace(search) ||
                                sourceChoice.DisplayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
            var matchingPieces = split.Pieces.Where(piece => parentMatches ||
                piece.DisplayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (!parentMatches && matchingPieces.Length == 0) return;

            EditorGUILayout.BeginHorizontal(rendererIndex == selectedRendererIndex ? "SelectionRect" : GUIStyle.none);
            EditorGUILayout.LabelField(sourceChoice.DisplayName + "  (UV split)", EditorStyles.miniBoldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(split.Pieces.Count + " pieces", EditorStyles.miniLabel, GUILayout.Width(105f));
            EditorGUILayout.EndHorizontal();

            foreach (var piece in matchingPieces)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(sourceChoice.IsExcludedFromMobile);
                EditorGUI.BeginChangeCheck();
                var keep = EditorGUILayout.Toggle(piece.KeepOnMobile, GUILayout.Width(18f));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(recipe, "Keep UV split piece on mobile");
                    piece.KeepOnMobile = keep;
                    piece.MeshChoice.GenerateCandidates = keep;
                    if (!keep) piece.MeshChoice.RevokeApproval();
                    InvalidateAfterUvSplitChoice();
                    RebuildPreview();
                }
                EditorGUI.EndDisabledGroup();
                if (GUILayout.Button("↳ " + piece.DisplayName, EditorStyles.label))
                {
                    SelectUvTilePieceForPreview(split, piece, rendererIndex);
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(piece.KeepOnMobile
                        ? piece.MeshChoice.SelectedTriangleCount.ToString("N0") + " tris"
                        : "MOBILE EXCLUDED", EditorStyles.miniLabel, GUILayout.Width(105f));
                if (GUILayout.Button("Preview", EditorStyles.miniButton, GUILayout.Width(58f)))
                    SelectUvTilePieceForPreview(split, piece, rendererIndex);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawUvTileSplitWorkspace()
        {
            var compatible = recipe.UvTileSplitRenderers.Count(item => item.Compatible);
            var enabledSplitCount = recipe.UvTileSplitRenderers.Count(item => item.Compatible && item.SplitEnabled);
            var retained = recipe.UvTileSplitRenderers.Where(item => item.Compatible && item.SplitEnabled)
                .Sum(item => item.Pieces.Count(piece => piece.KeepOnMobile));
            showUvTileSplit = EditorGUILayout.Foldout(showUvTileSplit,
                $"UV Tile Split/Bake - {enabledSplitCount}/{compatible} renderers, {retained} retained pieces", true);
            if (!showUvTileSplit) return;

            EditorGUILayout.HelpBox(
                "Run this before exclusions and decimation. It converts supported 4x4 UV Tile Dissolve geometry into ordinary child renderers, isolated materials, and isolated texture assets. The PC prefab is never modified. Unsafe topology and non-binary dissolve animation are blocked instead of guessed.",
                MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan & Generate UV Tile Pieces", GUILayout.Height(28f)))
            {
                if (EditorUtility.DisplayDialog("Generate UV tile split assets?",
                        "This replaces only this recipe's generated UV Tile Split cache. Existing downstream mesh candidates, materials, textures, and behavior must be rebuilt afterward.",
                        "Scan and Generate", "Cancel"))
                    RunDeferredEditorOperation(ScanUvTileSplits);
            }
            var pendingApprovals = recipe.UvTileSplitRenderers
                .Where(item => item.Compatible && item.SplitEnabled)
                .SelectMany(item => item.Pieces)
                .Count(piece => piece.KeepOnMobile && piece.MeshChoice.Candidates.Count > 0 &&
                                !piece.MeshChoice.IsCurrentSelectionApproved);
            EditorGUI.BeginDisabledGroup(pendingApprovals == 0);
            if (GUILayout.Button($"Approve All Retained Candidates ({pendingApprovals})", GUILayout.Height(28f)))
                ApproveAllUvTileCandidates();
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Saved status: " + recipe.UvTileSplitStatus,
                EditorStyles.wordWrappedMiniLabel);
            if (!string.IsNullOrEmpty(previewUvPieceKey) &&
                GUILayout.Button("Preview All Retained Pieces", GUILayout.Height(22f)))
            {
                previewUvPieceKey = string.Empty;
                RebuildPreview();
            }
            if (recipe.UvTileSplitRenderers.Count == 0) return;

            uvTileListHeight = Mathf.Clamp(uvTileListHeight, 70f,
                Mathf.Max(90f, position.height * 0.55f));
            uvTileScroll = EditorGUILayout.BeginScrollView(uvTileScroll,
                GUILayout.Height(uvTileListHeight));
            foreach (var split in recipe.UvTileSplitRenderers)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(!split.Compatible);
                EditorGUI.BeginChangeCheck();
                var splitEnabled = EditorGUILayout.Toggle(split.SplitEnabled, GUILayout.Width(18f));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(recipe, "Toggle UV tile split");
                    split.SplitEnabled = splitEnabled;
                    InvalidateAfterUvSplitChoice();
                    RebuildPreview();
                }
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.LabelField(split.DisplayName, EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(split.Compatible ? $"{split.Pieces.Count} pieces" : "BLOCKED",
                    EditorStyles.miniLabel, GUILayout.Width(70f));
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(split.Status, EditorStyles.wordWrappedMiniLabel);

                if (split.Compatible && split.SplitEnabled)
                {
                    foreach (var piece in split.Pieces)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUI.BeginChangeCheck();
                        var keep = EditorGUILayout.Toggle(piece.KeepOnMobile, GUILayout.Width(18f));
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(recipe, "Keep UV tile piece on mobile");
                            piece.KeepOnMobile = keep;
                            piece.MeshChoice.GenerateCandidates = keep;
                            piece.MeshChoice.RevokeApproval();
                            InvalidateAfterUvSplitChoice();
                            RebuildPreview();
                        }
                        EditorGUILayout.LabelField(piece.DisplayName, EditorStyles.miniLabel);
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.LabelField($"{piece.MeshChoice.SelectedTriangleCount:N0} tris",
                            EditorStyles.miniLabel, GUILayout.Width(75f));
                        if (GUILayout.Button("Preview", EditorStyles.miniButton, GUILayout.Width(58f)))
                        {
                            SelectUvTilePieceForPreview(split, piece,
                                recipe.RendererChoices.FindIndex(choice =>
                                    string.Equals(choice.TransformPath, split.TransformPath, StringComparison.Ordinal)));
                        }
                        EditorGUILayout.EndHorizontal();
                        if (piece.KeepOnMobile && !piece.AlwaysVisible)
                        {
                            EditorGUI.BeginChangeCheck();
                            var behavior = (UvTilePieceBehaviorMode)EditorGUILayout.EnumPopup(
                                "Mobile behavior", piece.BehaviorMode);
                            if (EditorGUI.EndChangeCheck())
                            {
                                Undo.RecordObject(recipe, "Change UV tile mobile behavior");
                                piece.BehaviorMode = behavior;
                                InvalidateAfterUvSplitChoice();
                                RebuildPreview();
                            }
                            if (piece.BehaviorMode == UvTilePieceBehaviorMode.AlwaysVisibleOnMobile)
                                EditorGUILayout.LabelField(
                                    "Fixed mobile fallback: PC outfit selections cannot hide this retained piece.",
                                    EditorStyles.wordWrappedMiniLabel);
                        }
                        if (piece.KeepOnMobile && piece.MeshChoice.Candidates.Count > 0)
                        {
                            var labels = piece.MeshChoice.Candidates.Select(candidate =>
                                candidate.Label + " (" + candidate.TriangleCount.ToString("N0") + ")").ToArray();
                            EditorGUILayout.BeginHorizontal();
                            EditorGUI.BeginChangeCheck();
                            var selected = EditorGUILayout.Popup("Candidate", piece.MeshChoice.SelectedCandidateIndex, labels);
                            if (EditorGUI.EndChangeCheck())
                            {
                                Undo.RecordObject(recipe, "Select UV tile mesh candidate");
                                piece.MeshChoice.SelectedCandidateIndex = selected;
                                piece.MeshChoice.RevokeApproval();
                                SaveRecipe();
                                RebuildPreview();
                            }
                            if (GUILayout.Button(piece.MeshChoice.IsCurrentSelectionApproved ? "Approved" : "Approve",
                                    GUILayout.Width(72f)))
                            {
                                Undo.RecordObject(recipe, "Approve UV tile mesh candidate");
                                piece.MeshChoice.ApproveCurrentSelection();
                                SaveRecipe();
                            }
                            EditorGUILayout.EndHorizontal();
                        }
                    }
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        private void ScanUvTileSplits()
        {
            try
            {
                UvTileSplitPipeline.ScanAndGenerate(recipe);
                previewUvPieceKey = string.Empty;
                selectedRendererIndex = recipe.RendererChoices.Count > 0 ? 0 : -1;
                RebuildPreview();
                ShowNotification(new GUIContent(recipe.UvTileSplitStatus));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("UV Tile Split scan failed", exception.Message, "OK");
            }
        }

        private void SelectUvTilePieceForPreview(UvTileSplitRenderer split, UvTilePieceChoice piece,
            int rendererIndex)
        {
            if (split == null || piece == null || rendererIndex < 0) return;
            selectedRendererIndex = rendererIndex;
            previewUvPieceKey = split.TransformPath + "|" + piece.Id;
            previewMode = PreviewMode.SelectedCandidate;
            RebuildPreview();
            RememberWorkspaceState();
        }

        private void ApproveAllUvTileCandidates()
        {
            var choices = recipe.UvTileSplitRenderers
                .Where(item => item.Compatible && item.SplitEnabled)
                .SelectMany(item => item.Pieces)
                .Where(piece => piece.KeepOnMobile && piece.MeshChoice.Candidates.Count > 0 &&
                                !piece.MeshChoice.IsCurrentSelectionApproved)
                .Select(piece => piece.MeshChoice)
                .ToArray();
            if (choices.Length == 0) return;
            Undo.RecordObject(recipe, "Approve all retained UV tile candidates");
            foreach (var choice in choices) choice.ApproveCurrentSelection();
            SaveRecipe();
            ShowNotification(new GUIContent($"Approved {choices.Length} retained UV tile candidate(s)."));
        }

        private void InvalidateAfterUvSplitChoice()
        {
            recipe.MeshGenerationState = "UV tile piece selection changed; regenerate retained candidates";
            MaterialConversionPipeline.InvalidateDownstream(recipe,
                "UV tile piece selection changed; rescan materials and rebuild Stages 3-6");
            SaveRecipe();
        }

        private void DrawMobileContentSummary()
        {
            var excluded = recipe.RendererChoices.Count(choice => choice.IsExcludedFromMobile);
            var redirected = recipe.RendererChoices.Count(choice => choice.RedirectsToFallback);
            showMobileContentRules = EditorGUILayout.Foldout(showMobileContentRules,
                $"Mobile Content - excluded {excluded}, redirected {redirected}", true);
            if (!showMobileContentRules) return;
            EditorGUILayout.HelpBox(
                "Exclude removes only the generated mobile payload. Exclude + Fallback also rewrites mobile animation states so a PC selection for deleted content activates a retained object. Expression parameter order, type, default, and sync layout are preserved.",
                MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Re-scan Toggle Dependencies", GUILayout.Height(23f)))
                RunDeferredEditorOperation(RescanMobileToggleDependencies);
            EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(recipe.MobileContentReportPath));
            if (GUILayout.Button("Open Content Report", GUILayout.Height(23f)))
            {
                var report = AssetDatabase.LoadAssetAtPath<TextAsset>(recipe.MobileContentReportPath);
                if (report != null) Selection.activeObject = report;
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Saved status: " + recipe.MobileContentStatus, EditorStyles.wordWrappedMiniLabel);
            if (!MobileContentPipeline.ValidateConfiguration(recipe, out var reason))
                EditorGUILayout.HelpBox(reason, MessageType.Error);
        }

        private void DrawSelectedMobileContentRule(RendererMeshChoice choice)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Mobile content rule", EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            var mode = (MobileContentMode)EditorGUILayout.EnumPopup("Action", choice.MobileContentMode);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(recipe, "Change mobile content rule");
                choice.MobileContentMode = mode;
                if (mode != MobileContentMode.ExcludeWithFallback)
                    choice.MobileFallbackTransformPath = string.Empty;
                if (mode == MobileContentMode.Keep)
                    choice.GenerateCandidates = true;
                else
                {
                    choice.GenerateCandidates = false;
                    choice.RevokeApproval();
                }
                MobileContentPipeline.InvalidateDownstream(recipe);
                RebuildPreview();
            }

            if (choice.RedirectsToFallback)
            {
                var fallbacks = recipe.RendererChoices.Where(candidate => !ReferenceEquals(candidate, choice) &&
                        !candidate.IsExcludedFromMobile &&
                        !candidate.TransformPath.StartsWith(choice.TransformPath + "/", StringComparison.Ordinal))
                    .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
                var labels = new[] { "<Choose retained fallback>" }
                    .Concat(fallbacks.Select(candidate => candidate.DisplayName + " - " + candidate.TransformPath)).ToArray();
                var current = Array.FindIndex(fallbacks, candidate =>
                    string.Equals(candidate.TransformPath, choice.MobileFallbackTransformPath, StringComparison.Ordinal));
                EditorGUI.BeginChangeCheck();
                var selected = EditorGUILayout.Popup("Fallback", current + 1, labels);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(recipe, "Choose mobile content fallback");
                    choice.MobileFallbackTransformPath = selected <= 0 ? string.Empty : fallbacks[selected - 1].TransformPath;
                    MobileContentPipeline.InvalidateDownstream(recipe);
                }
            }

            EditorGUILayout.LabelField(
                $"Detected activation bindings: {choice.MobileActivationBindingCount} in {choice.MobileActivationClipPaths.Count} clip(s)",
                EditorStyles.wordWrappedMiniLabel);
            foreach (var clip in choice.MobileActivationClipPaths.Take(3))
                EditorGUILayout.LabelField("- " + clip, EditorStyles.wordWrappedMiniLabel);
            if (choice.MobileActivationClipPaths.Count > 3)
                EditorGUILayout.LabelField($"...and {choice.MobileActivationClipPaths.Count - 3} more", EditorStyles.miniLabel);
            if (choice.RedirectsToFallback && choice.MobileActivationBindingCount == 0)
                EditorGUILayout.HelpBox(
                    "No source animation activation was found. Stage 7 will scan VRCFury's resolved controllers, but this fallback still needs visual confirmation.",
                    MessageType.Warning);
            EditorGUILayout.EndVertical();
        }

        private void DrawGenerationSettings()
        {
            EditorGUILayout.LabelField("Candidate levels", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            adaptiveCandidates = EditorGUILayout.ToggleLeft(
                "Adaptive targets (recommended): protect blendshapes, complex skinning, thin geometry, small islands, and cutout silhouettes",
                adaptiveCandidates);
            EditorGUI.BeginDisabledGroup(adaptiveCandidates);
            EditorGUILayout.BeginHorizontal();
            for (var index = 0; index < ratios.Length; index++)
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(90f));
                EditorGUILayout.LabelField(ratioLabels[index], EditorStyles.miniLabel);
                ratios[index] = Mathf.Clamp(EditorGUILayout.FloatField(ratios[index]), 0.05f, 0.99f);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();
            if (EditorGUI.EndChangeCheck()) RememberWorkspaceState();

            EditorGUILayout.HelpBox(backend.AvailabilityMessage,
                backend.IsAvailable ? MessageType.Info : MessageType.Warning);

            EditorGUI.BeginDisabledGroup(!backend.IsAvailable || UvTileSplitPipeline.GetEffectiveMeshChoices(recipe)
                .All(choice => !choice.GenerateCandidates));
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Generate / Resume Candidates", GUILayout.Height(28f)))
                RunDeferredEditorOperation(() => GenerateCandidates(false));
            if (GUILayout.Button("Regenerate Checked From Scratch...", GUILayout.Width(225f), GUILayout.Height(28f)))
            {
                var confirmed = EditorUtility.DisplayDialog(
                    "Regenerate checked meshes?",
                    "This replaces candidate files and revokes mesh approvals only for checked renderers. Other studio stages and the PC prefab remain untouched.",
                    "Regenerate Checked",
                    "Cancel");
                if (confirmed) RunDeferredEditorOperation(() => GenerateCandidates(true));
            }
            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.LabelField(
                "Generate / Resume skips completed renderers with matching settings. The progress window's Cancel button pauses safely at a saved checkpoint.",
                EditorStyles.wordWrappedMiniLabel);

            var generatedChoices = UvTileSplitPipeline.GetEffectiveMeshChoices(recipe)
                .Where(choice => choice.Candidates.Count > 0).ToArray();
            var balancedAvailable = generatedChoices.Count(choice => choice.Candidates.Any(candidate =>
                string.Equals(candidate.Id, "Balanced", StringComparison.OrdinalIgnoreCase) && candidate.CanSelect));
            EditorGUI.BeginDisabledGroup(balancedAvailable == 0);
            if (GUILayout.Button($"Use Balanced for All Generated Meshes ({balancedAvailable})", GUILayout.Height(28f)))
                UseBalancedForAllGeneratedMeshes();
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.LabelField(
                "Bulk selection only: this never approves meshes. Missing or unavailable Balanced candidates stay on their current selection and every changed mesh requires visual approval.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void UseBalancedForAllGeneratedMeshes()
        {
            Undo.RecordObject(recipe, "Use Balanced for all generated meshes");
            var generated = 0;
            var selected = 0;
            var changed = 0;
            var unavailable = 0;

            foreach (var choice in UvTileSplitPipeline.GetEffectiveMeshChoices(recipe))
            {
                if (choice.Candidates.Count == 0) continue;
                generated++;
                var balancedIndex = choice.Candidates.FindIndex(candidate =>
                    string.Equals(candidate.Id, "Balanced", StringComparison.OrdinalIgnoreCase) && candidate.CanSelect);
                if (balancedIndex < 0)
                {
                    unavailable++;
                    continue;
                }

                selected++;
                if (choice.SelectedCandidateIndex == balancedIndex) continue;
                choice.SelectedCandidateIndex = balancedIndex;
                choice.RevokeApproval();
                changed++;
            }

            SaveRecipe();
            RebuildPreview();
            ShowNotification(new GUIContent(
                $"Balanced selected on {selected}/{generated} generated meshes; {changed} changed, {unavailable} unavailable."));
        }

        private void DrawSelectedRenderer()
        {
            if (selectedRendererIndex < 0 || selectedRendererIndex >= recipe.RendererChoices.Count)
            {
                EditorGUILayout.HelpBox("Select a renderer above.", MessageType.Info);
                return;
            }

            var choice = recipe.RendererChoices[selectedRendererIndex];
            EditorGUILayout.LabelField(choice.DisplayName, EditorStyles.boldLabel);
            DrawSelectedMobileContentRule(choice);
            EditorGUILayout.LabelField(
                $"Source: {choice.SourceTriangleCount:N0} triangles, {choice.SourceVertexCount:N0} vertices, " +
                $"{choice.SourceBlendShapeCount} blendshapes, {choice.SourceBoneCount} bones, " +
                $"{(choice.SourceConnectedComponents < 0 ? "unmeasured" : choice.SourceConnectedComponents.ToString())} geometry groups, " +
                $"readable={choice.SourceReadable}",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField($"Reduction risk: {choice.ReductionRisk}/100 — {choice.ReductionRiskReason}",
                EditorStyles.wordWrappedMiniLabel);

            if (choice.IsExcludedFromMobile)
            {
                EditorGUILayout.HelpBox(
                    "This renderer payload will not be carried into mobile. Mesh generation and approval are unnecessary for it.",
                    MessageType.Info);
                return;
            }

            var uvSplit = UvTileSplitPipeline.FindEnabledSplit(recipe, choice.TransformPath);
            if (uvSplit != null)
            {
                EditorGUILayout.HelpBox(
                    "This source renderer is replaced by the retained UV tile pieces shown in the UV Tile Split/Bake panel. Candidate selection and approval are stored per piece.",
                    MessageType.Info);
                return;
            }

            if (adaptiveCandidates)
            {
                var adaptive = MeshCandidatePipeline.CreateAdaptiveLevels(choice);
                EditorGUILayout.LabelField("Adaptive targets: " + string.Join(", ", adaptive.Select(level => $"{level.Label} {level.Ratio:P0}")),
                    EditorStyles.wordWrappedMiniLabel);
            }

            if (choice.Candidates.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    choice.GenerateCandidates
                        ? "Mesh candidates are requested but have not been generated yet. Generate them, or uncheck this renderer to keep the original source mesh."
                        : "Mesh generation is skipped for this renderer. The analyzed original source mesh will be retained; material-only conversion can continue without reduction.",
                    MessageType.Info);
                return;
            }

            candidateScroll = EditorGUILayout.BeginScrollView(candidateScroll);
            for (var index = 0; index < choice.Candidates.Count; index++)
            {
                var candidate = choice.Candidates[index];
                var selected = index == choice.SelectedCandidateIndex;
                EditorGUILayout.BeginVertical(selected ? "SelectionRect" : EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();

                var oldColor = GUI.color;
                GUI.color = StatusColor(candidate.Status);
                EditorGUI.BeginDisabledGroup(!candidate.CanSelect);
                if (GUILayout.Button(selected ? "Selected" : "Use", GUILayout.Width(65f)))
                {
                    Undo.RecordObject(recipe, "Select mesh candidate");
                    choice.SelectedCandidateIndex = index;
                    choice.RevokeApproval();
                    EditorUtility.SetDirty(recipe);
                    AssetDatabase.SaveAssets();
                    RebuildPreview();
                }
                EditorGUI.EndDisabledGroup();
                GUI.color = oldColor;

                EditorGUILayout.LabelField(candidate.Label, EditorStyles.boldLabel, GUILayout.Width(85f));
                EditorGUILayout.LabelField($"{candidate.TriangleCount:N0} tris", GUILayout.Width(90f));
                var reduction = choice.SourceTriangleCount <= 0 ? 0f : 1f - candidate.TriangleCount / (float)choice.SourceTriangleCount;
                EditorGUILayout.LabelField($"-{reduction:P0}", GUILayout.Width(55f));
                EditorGUILayout.LabelField(candidate.Status.ToString(), GUILayout.Width(65f));
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(candidate.ValidationMessage, EditorStyles.wordWrappedMiniLabel);
                DrawQuality(candidate.Quality);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            var approved = choice.IsCurrentSelectionApproved;
            var approvalOldColor = GUI.color;
            GUI.color = approved ? SafeColor : Color.white;
            if (GUILayout.Button(approved ? "Approved" : "Approve Selected After Visual Review", GUILayout.Height(25f)))
            {
                Undo.RecordObject(recipe, approved ? "Revoke mesh approval" : "Approve mesh selection");
                if (approved) choice.RevokeApproval(); else choice.ApproveCurrentSelection();
                EditorUtility.SetDirty(recipe);
                AssetDatabase.SaveAssets();
            }
            GUI.color = approvalOldColor;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPreviewPanel()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            var newMode = (PreviewMode)EditorGUILayout.EnumPopup(previewMode, EditorStyles.toolbarPopup, GUILayout.Width(130f));
            if (newMode != previewMode)
            {
                previewMode = newMode;
                RebuildPreview();
            }
            if (GUILayout.Toggle(isolateSelected, "Isolate", EditorStyles.toolbarButton) != isolateSelected)
            {
                isolateSelected = !isolateSelected;
                RebuildPreview();
            }
            wireframe = GUILayout.Toggle(wireframe, "Wireframe", EditorStyles.toolbarButton);
            if (GUILayout.Button("Reset View", EditorStyles.toolbarButton)) ResetPreviewView();
            EditorGUILayout.EndHorizontal();

            var previewRect = GUILayoutUtility.GetRect(100f, 10000f, 100f, 10000f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            HandlePreviewInput(previewRect);
            RenderPreview(previewRect);
            if (previewMode == PreviewMode.SideBySide && Event.current.type == EventType.Repaint)
            {
                GUI.Label(new Rect(previewRect.x + 8f, previewRect.y + 8f, 150f, 22f), "PC Original", EditorStyles.whiteBoldLabel);
                GUI.Label(new Rect(previewRect.center.x + 8f, previewRect.y + 8f, 180f, 22f), "Selected Candidate", EditorStyles.whiteBoldLabel);
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(recipe == null || string.IsNullOrEmpty(recipe.OutputRoot));
            if (GUILayout.Button("Build Draft Mesh Prefab", GUILayout.Height(30f)))
                RunDeferredEditorOperation(BuildSelectedPrefab);
            if (GUILayout.Button("Reveal Output", GUILayout.Width(105f), GUILayout.Height(30f)))
            {
                var output = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(recipe.OutputRoot);
                if (output != null) EditorGUIUtility.PingObject(output);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(
                "This build creates the approved mesh-selection prefab only. It does not yet convert shaders, atlases, animations, or PhysBones.",
                MessageType.Info);
        }

        private static string ProjectPreferenceKey(string name)
        {
            return "MobileAvatarStudio." + Hash128.Compute(Application.dataPath) + "." + name;
        }

        private void SetStage(StudioStage value)
        {
            stage = value;
            RememberWorkspaceState();
        }

        private void RunDeferredEditorOperation(Action operation)
        {
            if (!deferredOperationQueued)
            {
                deferredOperationQueued = true;
                EditorApplication.delayCall += () =>
                {
                    if (this == null) return;
                    try
                    {
                        operation();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                        EditorUtility.DisplayDialog("Mobile Avatar Studio operation failed", exception.Message, "OK");
                    }
                    finally
                    {
                        if (this != null)
                        {
                            deferredOperationQueued = false;
                            Repaint();
                        }
                    }
                };
            }
            GUIUtility.ExitGUI();
        }

        private void SaveRecipe()
        {
            if (recipe == null) return;
            EditorUtility.SetDirty(recipe);
            if (AssetDatabase.Contains(recipe)) AssetDatabase.SaveAssets();
            RememberWorkspaceState();
        }

        private void RememberWorkspaceState()
        {
            if (recipe != null)
            {
                var recipePath = AssetDatabase.GetAssetPath(recipe);
                if (!string.IsNullOrEmpty(recipePath))
                    EditorPrefs.SetString(ProjectPreferenceKey("LastRecipePath"), recipePath);
            }

            EditorPrefs.SetInt(ProjectPreferenceKey("Stage"), (int)stage);
            EditorPrefs.SetInt(ProjectPreferenceKey("AdaptiveCandidates"), adaptiveCandidates ? 1 : 0);
            EditorPrefs.SetInt(ProjectPreferenceKey("IsolateSelected"), isolateSelected ? 1 : 0);
            EditorPrefs.SetInt(ProjectPreferenceKey("Wireframe"), wireframe ? 1 : 0);
            EditorPrefs.SetInt(ProjectPreferenceKey("PreviewMode"), (int)previewMode);
            EditorPrefs.SetFloat(ProjectPreferenceKey("PreviewYaw"), previewYaw);
            EditorPrefs.SetFloat(ProjectPreferenceKey("PreviewPitch"), previewPitch);
            EditorPrefs.SetFloat(ProjectPreferenceKey("PreviewZoom"), previewZoom);
            EditorPrefs.SetFloat(ProjectPreferenceKey("PreviewPanX"), previewPan.x);
            EditorPrefs.SetFloat(ProjectPreferenceKey("PreviewPanY"), previewPan.y);

            var selectedPath = selectedRendererIndex >= 0 && recipe != null &&
                               selectedRendererIndex < recipe.RendererChoices.Count
                ? recipe.RendererChoices[selectedRendererIndex].TransformPath
                : string.Empty;
            EditorPrefs.SetString(ProjectPreferenceKey("SelectedRendererPath"), selectedPath);

            for (var index = 0; index < ratios.Length; index++)
                EditorPrefs.SetFloat(ProjectPreferenceKey("CandidateRatio" + index), ratios[index]);
            for (var index = 0; index < textureCategoryExpanded.Length; index++)
            {
                EditorPrefs.SetInt(ProjectPreferenceKey("TextureExpanded" + index),
                    textureCategoryExpanded[index] ? 1 : 0);
                EditorPrefs.SetInt(ProjectPreferenceKey("TextureMaxSize" + index), textureCategoryMaxSizes[index]);
                EditorPrefs.SetInt(ProjectPreferenceKey("TextureCompression" + index),
                    (int)textureCategoryCompressions[index]);
            }
        }

        private void RestoreWorkspaceState()
        {
            if (recipe == null)
            {
                var recipePath = EditorPrefs.GetString(ProjectPreferenceKey("LastRecipePath"), string.Empty);
                if (!string.IsNullOrEmpty(recipePath))
                    recipe = AssetDatabase.LoadAssetAtPath<MobileAvatarMeshRecipe>(recipePath);
                if (recipe == null && AssetDatabase.IsValidFolder("Assets/MobileAvatarStudioGenerated"))
                {
                    recipePath = AssetDatabase.FindAssets("t:MobileAvatarMeshRecipe",
                            new[] { "Assets/MobileAvatarStudioGenerated" })
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (!string.IsNullOrEmpty(recipePath))
                        recipe = AssetDatabase.LoadAssetAtPath<MobileAvatarMeshRecipe>(recipePath);
                }
            }
            if (recipe != null) sourcePrefab = recipe.SourcePrefab;

            stage = (StudioStage)Mathf.Clamp(EditorPrefs.GetInt(ProjectPreferenceKey("Stage"), (int)stage),
                0, Enum.GetValues(typeof(StudioStage)).Length - 1);
            adaptiveCandidates = EditorPrefs.GetInt(ProjectPreferenceKey("AdaptiveCandidates"),
                adaptiveCandidates ? 1 : 0) != 0;
            isolateSelected = EditorPrefs.GetInt(ProjectPreferenceKey("IsolateSelected"),
                isolateSelected ? 1 : 0) != 0;
            wireframe = EditorPrefs.GetInt(ProjectPreferenceKey("Wireframe"), wireframe ? 1 : 0) != 0;
            previewMode = (PreviewMode)Mathf.Clamp(EditorPrefs.GetInt(ProjectPreferenceKey("PreviewMode"),
                (int)previewMode), 0, Enum.GetValues(typeof(PreviewMode)).Length - 1);
            previewYaw = EditorPrefs.GetFloat(ProjectPreferenceKey("PreviewYaw"), previewYaw);
            previewPitch = EditorPrefs.GetFloat(ProjectPreferenceKey("PreviewPitch"), previewPitch);
            previewZoom = EditorPrefs.GetFloat(ProjectPreferenceKey("PreviewZoom"), previewZoom);
            previewPan = new Vector2(
                EditorPrefs.GetFloat(ProjectPreferenceKey("PreviewPanX"), previewPan.x),
                EditorPrefs.GetFloat(ProjectPreferenceKey("PreviewPanY"), previewPan.y));

            for (var index = 0; index < ratios.Length; index++)
                ratios[index] = EditorPrefs.GetFloat(ProjectPreferenceKey("CandidateRatio" + index), ratios[index]);
            for (var index = 0; index < textureCategoryExpanded.Length; index++)
            {
                textureCategoryExpanded[index] = EditorPrefs.GetInt(ProjectPreferenceKey("TextureExpanded" + index),
                    textureCategoryExpanded[index] ? 1 : 0) != 0;
                textureCategoryMaxSizes[index] = EditorPrefs.GetInt(ProjectPreferenceKey("TextureMaxSize" + index),
                    textureCategoryMaxSizes[index]);
                textureCategoryCompressions[index] = (MobileTextureCompression)Mathf.Clamp(
                    EditorPrefs.GetInt(ProjectPreferenceKey("TextureCompression" + index),
                        (int)textureCategoryCompressions[index]), 0,
                    Enum.GetValues(typeof(MobileTextureCompression)).Length - 1);
            }

            selectedRendererIndex = -1;
            if (recipe != null && recipe.RendererChoices.Count > 0)
            {
                var selectedPath = EditorPrefs.GetString(ProjectPreferenceKey("SelectedRendererPath"), string.Empty);
                selectedRendererIndex = recipe.RendererChoices.FindIndex(choice =>
                    string.Equals(choice.TransformPath, selectedPath, StringComparison.Ordinal));
                if (selectedRendererIndex < 0) selectedRendererIndex = 0;
            }
        }

        private void MigrateManualPolishCheckpoint()
        {
            if (recipe == null || !recipe.BehaviorAppliedToCombined ||
                !string.IsNullOrEmpty(recipe.ManualPolishCheckpointUtc)) return;
            ManualPolishPipeline.Invalidate(recipe,
                "Manual polish checkpoint required; save and rescan the final Stage 6 prefab");
            AssetDatabase.SaveAssets();
        }

        private void AnalyzeSource(bool replaceExisting)
        {
            try
            {
                recipe = MeshCandidatePipeline.Analyze(sourcePrefab, replaceExisting);
                selectedRendererIndex = recipe.RendererChoices.Count > 0 ? 0 : -1;
                candidateScroll = Vector2.zero;
                materialScroll = Vector2.zero;
                stage = StudioStage.Overview;
                ResetPreviewView();
                RebuildPreview();
                RememberWorkspaceState();
                ShowNotification(new GUIContent(replaceExisting
                    ? $"Started fresh with {recipe.RendererChoices.Count} meshes"
                    : $"Opened resumable project with {recipe.RendererChoices.Count} meshes"));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Mobile Avatar Studio", exception.Message, "OK");
            }
        }

        private void StartFresh()
        {
            if (sourcePrefab == null) return;
            var existing = MeshCandidatePipeline.FindExistingRecipe(sourcePrefab);
            if (existing == null)
            {
                AnalyzeSource(false);
                return;
            }

            var confirmed = EditorUtility.DisplayDialog(
                "Start a fresh mobile avatar project?",
                "This permanently replaces this avatar's generated Mobile Avatar Studio workspace, including mesh candidates, approvals, generated materials, texture decisions, and reports.\n\n" +
                "The PC source prefab is never modified.",
                "Replace Generated Workspace",
                "Keep Existing Work");
            if (confirmed) AnalyzeSource(true);
        }

        private void GenerateCandidates(bool forceRegenerate)
        {
            try
            {
                var levels = new List<MeshCandidatePipeline.CandidateLevel>();
                var ids = new[] { "VeryLight", "Light", "Balanced", "Aggressive" };
                for (var index = 0; index < ratios.Length; index++)
                    levels.Add(new MeshCandidatePipeline.CandidateLevel(ids[index], ratioLabels[index], ratios[index]));

                var completed = MeshCandidatePipeline.GenerateCandidates(recipe, levels, backend, adaptiveCandidates,
                    forceRegenerate);
                RebuildPreview();
                RememberWorkspaceState();
                ShowNotification(new GUIContent(completed
                    ? "Candidate generation is complete. Saved checkpoints are current."
                    : "Candidate generation paused. Press Generate / Resume when ready."));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Candidate generation failed", exception.Message, "OK");
            }
        }

        private void BuildSelectedPrefab()
        {
            try
            {
                var path = MeshCandidatePipeline.BuildSelectedPrefab(recipe);
                ShowNotification(new GUIContent("Built " + path));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Prefab build failed", exception.Message, "OK");
            }
        }

        private void CreatePreviewUtility()
        {
            if (preview != null) return;
            preview = new PreviewRenderUtility(true);
            preview.camera.fieldOfView = 28f;
            preview.camera.nearClipPlane = 0.001f;
            preview.camera.farClipPlane = 1000f;
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(0.11f, 0.12f, 0.14f);
            preview.lights[0].intensity = 1.25f;
            preview.lights[0].transform.rotation = Quaternion.Euler(35f, 35f, 0f);
            preview.lights[1].intensity = 0.75f;
            preview.lights[1].transform.rotation = Quaternion.Euler(340f, 220f, 0f);
            preview.ambientColor = new Color(0.35f, 0.35f, 0.38f);
        }

        private void RebuildPreview()
        {
            DestroyPreviewInstance();
            if (recipe == null || recipe.SourcePrefab == null) return;
            CreatePreviewUtility();

            var allRenderers = new List<Renderer>();
            if (previewMode != PreviewMode.Original)
            {
                previewInstance = UnityEngine.Object.Instantiate(recipe.SourcePrefab);
                previewInstance.name = "Mobile Avatar Studio Candidate Preview";
                MeshCandidatePipeline.ApplySelections(previewInstance, recipe);
                MobileContentPipeline.ApplyPreviewPayloadExclusions(previewInstance, recipe);
                allRenderers.AddRange(ConfigurePreviewRoot(previewInstance));
            }

            if (previewMode != PreviewMode.SelectedCandidate)
            {
                originalPreviewInstance = UnityEngine.Object.Instantiate(recipe.SourcePrefab);
                originalPreviewInstance.name = "Mobile Avatar Studio Original Preview";
                allRenderers.AddRange(ConfigurePreviewRoot(originalPreviewInstance));
            }

            if (previewMode == PreviewMode.SideBySide && previewInstance != null && originalPreviewInstance != null)
            {
                var candidateBounds = CalculateBounds(previewInstance.GetComponentsInChildren<Renderer>(true).Where(item => item.enabled).ToArray(), null);
                var originalBounds = CalculateBounds(originalPreviewInstance.GetComponentsInChildren<Renderer>(true).Where(item => item.enabled).ToArray(), null);
                var spacing = Mathf.Max(candidateBounds.size.x, originalBounds.size.x) * 1.18f;
                originalPreviewInstance.transform.position += Vector3.left * spacing * 0.5f;
                previewInstance.transform.position += Vector3.right * spacing * 0.5f;
            }

            allRenderers = allRenderers.Where(item => item != null && item.enabled).ToList();
            previewBounds = CalculateBounds(allRenderers, null);
            if (originalPreviewInstance != null) preview.AddSingleGO(originalPreviewInstance);
            if (previewInstance != null) preview.AddSingleGO(previewInstance);
            Repaint();
        }

        private List<Renderer> ConfigurePreviewRoot(GameObject root)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                transform.gameObject.hideFlags = HideFlags.HideAndDontSave;

            var selectedRenderers = new HashSet<Renderer>();
            if (selectedRendererIndex >= 0 && selectedRendererIndex < recipe.RendererChoices.Count)
            {
                var choice = recipe.RendererChoices[selectedRendererIndex];
                var selected = MeshAnalysisUtility.FindByPath(root.transform, choice.TransformPath);
                if (selected != null)
                {
                    for (var current = selected; current != null; current = current.parent)
                        current.gameObject.SetActive(true);
                    var originalRenderer = choice.Skinned
                        ? (Renderer)selected.GetComponent<SkinnedMeshRenderer>()
                        : selected.GetComponent<MeshRenderer>();
                    if (originalRenderer != null && originalRenderer.sharedMaterials.Length > 0)
                        selectedRenderers.Add(originalRenderer);
                    var split = UvTileSplitPipeline.FindEnabledSplit(recipe, choice.TransformPath);
                    if (split != null)
                    foreach (var piece in split.Pieces.Where(item => item.KeepOnMobile))
                    {
                        var pieceKey = split.TransformPath + "|" + piece.Id;
                        if (!string.IsNullOrEmpty(previewUvPieceKey) &&
                            !string.Equals(previewUvPieceKey, pieceKey, StringComparison.Ordinal)) continue;
                        var child = selected.Find(piece.GeneratedChildName);
                        var childRenderer = child?.GetComponent<Renderer>();
                        if (childRenderer != null) selectedRenderers.Add(childRenderer);
                    }
                }
            }

            var enabled = new List<Renderer>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = !isolateSelected || selectedRenderers.Contains(renderer);
                if (!renderer.enabled) continue;
                for (var current = renderer.transform; current != null; current = current.parent)
                    current.gameObject.SetActive(true);
                if (renderer is SkinnedMeshRenderer skinned) skinned.updateWhenOffscreen = true;
                enabled.Add(renderer);
            }
            return enabled;
        }

        private void DestroyPreviewInstance()
        {
            if (previewInstance != null)
            {
                UnityEngine.Object.DestroyImmediate(previewInstance);
                previewInstance = null;
            }
            if (originalPreviewInstance != null)
            {
                UnityEngine.Object.DestroyImmediate(originalPreviewInstance);
                originalPreviewInstance = null;
            }
        }

        private void RenderPreview(Rect rect)
        {
            if (preview == null || (previewInstance == null && originalPreviewInstance == null) || Event.current.type != EventType.Repaint)
            {
                if (Event.current.type == EventType.Repaint)
                    EditorGUI.DrawRect(rect, new Color(0.11f, 0.12f, 0.14f));
                return;
            }

            var center = previewBounds.center + new Vector3(previewPan.x, previewPan.y, 0f);
            var radius = Mathf.Max(0.05f, previewBounds.extents.magnitude);
            var distance = radius * 2.7f / Mathf.Max(0.15f, previewZoom);
            var rotation = Quaternion.Euler(previewPitch, previewYaw, 0f);
            preview.camera.transform.position = center + rotation * new Vector3(0f, 0f, -distance);
            preview.camera.transform.rotation = rotation;
            preview.camera.nearClipPlane = Mathf.Max(0.001f, distance - radius * 2f);
            preview.camera.farClipPlane = distance + radius * 4f;

            preview.BeginPreview(rect, GUIStyle.none);
            var previousWireframe = GL.wireframe;
            GL.wireframe = wireframe;
            try
            {
                preview.Render(true);
            }
            finally
            {
                GL.wireframe = previousWireframe;
            }
            var texture = preview.EndPreview();
            GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, false);
        }

        private void HandlePreviewInput(Rect rect)
        {
            var current = Event.current;
            if (!rect.Contains(current.mousePosition)) return;

            if (current.type == EventType.ScrollWheel)
            {
                previewZoom = Mathf.Clamp(previewZoom * (1f - current.delta.y * 0.05f), 0.15f, 8f);
                current.Use();
                Repaint();
            }
            else if (current.type == EventType.MouseDrag && current.button == 0)
            {
                previewYaw += current.delta.x * 0.5f;
                previewPitch = Mathf.Clamp(previewPitch - current.delta.y * 0.5f, -89f, 89f);
                current.Use();
                Repaint();
            }
            else if (current.type == EventType.MouseDrag && (current.button == 1 || current.button == 2))
            {
                var scale = Mathf.Max(0.001f, previewBounds.extents.magnitude) * 0.0025f / Mathf.Max(0.15f, previewZoom);
                previewPan += new Vector2(-current.delta.x * scale, current.delta.y * scale);
                current.Use();
                Repaint();
            }
        }

        private void ResetPreviewView()
        {
            previewYaw = 155f;
            previewPitch = 8f;
            previewZoom = 1f;
            previewPan = Vector2.zero;
            Repaint();
        }

        private static Bounds CalculateBounds(IReadOnlyList<Renderer> renderers, Renderer selected)
        {
            if (selected != null) return selected.bounds;
            if (renderers.Count == 0) return new Bounds(Vector3.zero, Vector3.one * 2f);
            var bounds = renderers[0].bounds;
            for (var index = 1; index < renderers.Count; index++) bounds.Encapsulate(renderers[index].bounds);
            if (bounds.extents.sqrMagnitude < 0.000001f) bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            return bounds;
        }

        private static Color StatusColor(MeshCandidateStatus status)
        {
            switch (status)
            {
                case MeshCandidateStatus.Recommended:
                case MeshCandidateStatus.Safe: return SafeColor;
                case MeshCandidateStatus.ReviewRequired:
                case MeshCandidateStatus.HighRisk: return ReviewColor;
                case MeshCandidateStatus.Unavailable: return Color.gray;
                case MeshCandidateStatus.Rejected: return RejectedColor;
                default: return Color.white;
            }
        }

        private void LoadPaneLayout()
        {
            meshControlPaneWidth = EditorPrefs.GetFloat(LayoutPreferencePrefix + "MeshControlWidth",
                meshControlPaneWidth);
            uvTileListHeight = EditorPrefs.GetFloat(LayoutPreferencePrefix + "UvTileHeight",
                uvTileListHeight);
            rendererListHeight = EditorPrefs.GetFloat(LayoutPreferencePrefix + "RendererHeight",
                rendererListHeight);
            validationIssueListHeight = EditorPrefs.GetFloat(LayoutPreferencePrefix + "ValidationHeight",
                validationIssueListHeight);
            repairListHeight = EditorPrefs.GetFloat(LayoutPreferencePrefix + "RepairHeight",
                repairListHeight);
            behaviorContractListHeight = EditorPrefs.GetFloat(LayoutPreferencePrefix + "ContractHeight",
                behaviorContractListHeight);
        }

        private void SavePaneLayout()
        {
            EditorPrefs.SetFloat(LayoutPreferencePrefix + "MeshControlWidth", meshControlPaneWidth);
            EditorPrefs.SetFloat(LayoutPreferencePrefix + "UvTileHeight", uvTileListHeight);
            EditorPrefs.SetFloat(LayoutPreferencePrefix + "RendererHeight", rendererListHeight);
            EditorPrefs.SetFloat(LayoutPreferencePrefix + "ValidationHeight", validationIssueListHeight);
            EditorPrefs.SetFloat(LayoutPreferencePrefix + "RepairHeight", repairListHeight);
            EditorPrefs.SetFloat(LayoutPreferencePrefix + "ContractHeight", behaviorContractListHeight);
        }

        private void DrawVerticalSplitter(string key, ref float value, float minimum, float maximum,
            float resetValue)
        {
            var rect = GUILayoutUtility.GetRect(6f, 6f, GUILayout.ExpandHeight(true));
            DrawPaneSplitter(key, rect, ref value, minimum, maximum, resetValue, true);
        }

        private void DrawHorizontalSplitter(string key, ref float value, float minimum, float maximum,
            float resetValue)
        {
            var rect = GUILayoutUtility.GetRect(1f, 6f, GUILayout.ExpandWidth(true));
            DrawPaneSplitter(key, rect, ref value, minimum, maximum, resetValue, false);
        }

        private void DrawPaneSplitter(string key, Rect rect, ref float value, float minimum, float maximum,
            float resetValue, bool vertical)
        {
            var controlId = GUIUtility.GetControlID(("MobileAvatarStudio.Splitter." + key).GetHashCode(),
                FocusType.Passive, rect);
            EditorGUIUtility.AddCursorRect(rect,
                vertical ? MouseCursor.ResizeHorizontal : MouseCursor.ResizeVertical, controlId);

            if (Event.current.type == EventType.Repaint)
            {
                var color = GUIUtility.hotControl == controlId
                    ? new Color(0.28f, 0.62f, 0.95f, 0.95f)
                    : new Color(0.34f, 0.34f, 0.34f, 0.9f);
                var line = vertical
                    ? new Rect(rect.center.x - 1f, rect.y, 2f, rect.height)
                    : new Rect(rect.x, rect.center.y - 1f, rect.width, 2f);
                EditorGUI.DrawRect(line, color);
            }

            var current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0 && rect.Contains(current.mousePosition))
            {
                if (current.clickCount >= 2)
                {
                    value = Mathf.Clamp(resetValue, minimum, maximum);
                    SavePaneLayout();
                    GUI.changed = true;
                    Repaint();
                    current.Use();
                    return;
                }
                GUIUtility.hotControl = controlId;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && GUIUtility.hotControl == controlId)
            {
                value = Mathf.Clamp(value + (vertical ? current.delta.x : current.delta.y), minimum, maximum);
                GUI.changed = true;
                Repaint();
                current.Use();
            }
            else if (current.type == EventType.MouseUp && GUIUtility.hotControl == controlId)
            {
                GUIUtility.hotControl = 0;
                SavePaneLayout();
                current.Use();
            }
        }

        private void DrawBehaviorContractSummary()
        {
            var contract = recipe.SourceBehaviorContract;
            if (contract == null) return;
            showBehaviorContract = EditorGUILayout.Foldout(showBehaviorContract,
                $"Source Avatar Behavior Contract — {contract.ContractHash?.Substring(0, Math.Min(12, contract.ContractHash.Length))}", true);
            if (!showBehaviorContract) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Resolution: " + contract.ResolutionState, EditorStyles.boldLabel);
            behaviorContractListHeight = Mathf.Clamp(behaviorContractListHeight, 90f,
                Mathf.Max(110f, position.height * 0.50f));
            contractScroll = EditorGUILayout.BeginScrollView(contractScroll,
                GUILayout.Height(behaviorContractListHeight));
            foreach (var category in contract.Categories)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(category.Name, GUILayout.Width(150f));
                EditorGUILayout.LabelField(category.EntryCount.ToString("N0"), GUILayout.Width(65f));
                EditorGUILayout.LabelField(category.Summary, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndHorizontal();
            }
            if (contract.DetectedBuildSystems.Count > 0)
                EditorGUILayout.HelpBox("Build systems detected: " + string.Join(", ", contract.DetectedBuildSystems) +
                                        ". Current analysis is authoring-prefab analysis; resolved-build analysis is not implemented yet.",
                    MessageType.Warning);
            foreach (var warning in contract.Warnings) EditorGUILayout.HelpBox(warning, MessageType.Warning);
            EditorGUILayout.EndScrollView();
            DrawHorizontalSplitter("BehaviorContract", ref behaviorContractListHeight, 90f,
                Mathf.Max(110f, position.height * 0.50f), 150f);
            EditorGUILayout.EndVertical();
        }

        private static void DrawQuality(MeshCandidateQuality quality)
        {
            EditorGUILayout.BeginHorizontal();
            DrawQualityValue("Structure", quality.StructuralIntegrity);
            DrawQualityValue("Silhouette", quality.SilhouettePreservation);
            DrawQualityValue("Deform", quality.DeformationQuality);
            DrawQualityValue("Shapes", quality.BlendShapeFidelity);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            DrawQualityValue("Normals", quality.NormalQuality);
            DrawQualityValue("UV", quality.UvStability);
            DrawQualityValue("Weights", quality.BoneWeightIntegrity);
            DrawQualityValue("Efficiency", quality.VisualEfficiency);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(quality.MeasurementNotes, EditorStyles.wordWrappedMiniLabel);
        }

        private static void DrawQualityValue(string label, int value)
        {
            EditorGUILayout.LabelField(label + ": " + (value < 0 ? "Not measured" : value + "%"),
                EditorStyles.miniLabel, GUILayout.MinWidth(110f));
        }
    }
}
