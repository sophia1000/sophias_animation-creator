using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public partial class SophiasAnimationCreatorWindow : EditorWindow
{
    private const string DefaultOutputFolder = "Assets/! animation creator/Generated Animations";
    private const int MaxSearchResults = 80;

    private enum OperationMode
    {
        CreateClip,
        CopyApply
    }

    private enum CreateKeyMode
    {
        TwoKeys,
        SingleKey
    }

    private enum QuickAddMode
    {
        None,
        ObjectToggle,
        Position,
        Rotation,
        Scale
    }

    private enum ManualValueKind
    {
        None,
        GameObjectActive,
        ComponentEnabled,
        BlendShape
    }

    private sealed class BindingSpec
    {
        public GameObject Target;
        public Component SourceComponent;
        public Type BindingType;
        public string PropertyName;
        public string Label;
        public ManualValueKind ManualKind;
        public int ManualIndex = -1;
        public bool UseCustomValue = true;
        public float CustomValue;
        public bool IsObjectReference;
        public UnityEngine.Object CustomObjectReferenceValue;
        public int ObjectReferenceIndex = -1;
        public bool IsQuickAdd;

        public bool IsToggleLike
        {
            get { return ManualKind == ManualValueKind.GameObjectActive || ManualKind == ManualValueKind.ComponentEnabled; }
        }
    }

    private sealed class PropertySet
    {
        public string Label;
        public GameObject Target;
        public Component SourceComponent;
        public readonly List<BindingSpec> Specs = new List<BindingSpec>();
        public bool Expanded;
    }

    private sealed class CapturedFloat
    {
        public EditorCurveBinding Binding;
        public float Value;
        public string Label;
        public bool IsToggleLike;
    }

    private sealed class CapturedObjectReference
    {
        public EditorCurveBinding Binding;
        public UnityEngine.Object StartValue;
        public UnityEngine.Object Value;
        public string Label;
    }

    private sealed class CopyBuffer
    {
        public readonly List<CapturedFloat> Keys = new List<CapturedFloat>();
        public readonly List<CapturedObjectReference> ObjectKeys = new List<CapturedObjectReference>();
        public float FrameRate;
        public string RootName;
        public DateTime CapturedAt;
    }

    private sealed class BindingSearchResult
    {
        public GameObject Target;
        public Component SourceComponent;
        public EditorCurveBinding Binding;
        public string Label;
    }

    [SerializeField] private OperationMode operationMode = OperationMode.CreateClip;
    [SerializeField] private CreateKeyMode createKeyMode = CreateKeyMode.TwoKeys;
    [SerializeField] private GameObject animationRoot;
    [SerializeField] private string outputFolder = DefaultOutputFolder;
    [SerializeField] private string clipName = "New Animation";
    [SerializeField] private float frameRate = 60f;
    [SerializeField] private int durationFrames = 1;
    [SerializeField] private bool invertFirstToggleKey;
    [SerializeField] private bool loopClip;
    [SerializeField] private AnimationClip pasteTargetClip;
    [SerializeField] private int pasteFrame;
    [SerializeField] private bool includeInactiveSearch = true;
    [SerializeField] private GameObject objectSearchScopeRoot;
    [SerializeField] private string objectNameSearch = "";
    [SerializeField] private string componentNameSearch = "";
    [SerializeField] private string propertySearch = "";
    [SerializeField] private Material materialSwapMaterial;
    [SerializeField] private bool materialSwapAllSlots;
    [SerializeField] private List<GameObject> targets = new List<GameObject>();
    [SerializeField] private QuickAddMode activeQuickAddMode = QuickAddMode.None;

    private readonly List<PropertySet> propertySets = new List<PropertySet>();
    private Vector2 scroll;
    private bool settingsFoldout = true;
    private bool objectsFoldout = true;
    private bool objectSearchFoldout = true;
    private bool favoritesFoldout = true;
    private bool quickAddFoldout = true;
    private bool materialSwapFoldout = true;
    private bool propertySearchFoldout = true;
    private bool chosenPropertiesFoldout = true;
    private bool copyFoldout = true;
    private GUIStyle titleStyle;
    private GUIStyle miniMutedStyle;

    private static CopyBuffer copiedBuffer;
    private static AnimationClip nativeClipboardFallbackClip;

    [MenuItem("Tools/Sophia's Animation Creator")]
    public static void OpenWindow()
    {
        SophiasAnimationCreatorWindow window = GetWindow<SophiasAnimationCreatorWindow>("Sophia's Animation Creator");
        window.minSize = new Vector2(500f, 620f);
        window.Show();
    }

    private void OnEnable()
    {
        if (string.IsNullOrEmpty(outputFolder))
            outputFolder = DefaultOutputFolder;

        if (string.IsNullOrEmpty(clipName))
            clipName = "New Animation";

        if (targets == null)
            targets = new List<GameObject>();

        minSize = new Vector2(500f, 620f);
        FeatureOnEnable();
    }

    private void OnGUI()
    {
        EnsureStyles();
        HandleCopyApplyShortcuts();
        CleanupTargetList();

        DrawHeader();
        scroll = EditorGUILayout.BeginScrollView(scroll);

        DrawSettings();
        DrawObjects();
        DrawObjectSearch();
        DrawFavorites();
        DrawQuickAdd();
        DrawMaterialSwap();
        DrawMaterialPropertySearch();
        DrawPropertySearch();
        DrawChosenProperties();
        DrawActions();

        EditorGUILayout.EndScrollView();
    }

    private void EnsureStyles()
    {
        if (titleStyle != null)
            return;

        titleStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 15,
            fixedHeight = 22f
        };

        miniMutedStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            wordWrap = true
        };
    }

    private void DrawHeader()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Sophia's Animation Creator", titleStyle);
        GUILayout.FlexibleSpace();
        DrawRecordModeHeaderButton();
        GUILayout.Space(6f);
        operationMode = (OperationMode)GUILayout.Toolbar((int)operationMode, new[] { "Create Clip", "Copy / Apply" }, GUILayout.Width(210f));
        EditorGUILayout.EndHorizontal();

        GameObject effectiveRoot = GetEffectiveRoot();
        string rootLabel = effectiveRoot != null ? effectiveRoot.name : "None";
        int specCount = propertySets.Sum(set => set.Specs.Count);
        EditorGUILayout.LabelField("Root: " + rootLabel + "    Objects: " + ValidTargets().Count + "    Properties: " + specCount, miniMutedStyle);
        EditorGUILayout.EndVertical();
    }

    private void DrawRecordModeHeaderButton()
    {
        Color previousBackground = GUI.backgroundColor;
        GUI.backgroundColor = listenForAvatarChanges ? new Color(1f, 0.28f, 0.28f) : new Color(0.58f, 0.58f, 0.58f);

        if (GUILayout.Button(new GUIContent("Record Mode", "Listen for property changes under the current root without entering Unity's animation record mode."), GUILayout.Width(104f), GUILayout.Height(22f)))
            ToggleRecordMode();

        GUI.backgroundColor = previousBackground;
    }

    private void ToggleRecordMode()
    {
        bool enabled = !listenForAvatarChanges;
        if (enabled && GetEffectiveRoot() == null)
            TryAssignActiveAvatarRoot(true);

        if (enabled && GetEffectiveRoot() == null)
        {
            ShowNotification(new GUIContent("Select an avatar root first"));
            return;
        }

        listenForAvatarChanges = enabled;
        SetPassiveCaptureSubscription(listenForAvatarChanges);
        Repaint();
    }

    private void DrawSettings()
    {
        settingsFoldout = BeginFoldoutPanel(settingsFoldout, "Clip Settings");
        if (!settingsFoldout)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUI.BeginChangeCheck();
        animationRoot = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Animation Root", "The avatar or object the clip will be played on. Paths are calculated relative to this object."), animationRoot, typeof(GameObject), true);
        if (EditorGUI.EndChangeCheck())
            Repaint();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Use Active Selection"))
            animationRoot = Selection.activeGameObject;

        if (GUILayout.Button("Use Common Parent"))
            animationRoot = FindCommonRoot(ValidTargets());
        EditorGUILayout.EndHorizontal();

        frameRate = Mathf.Max(1f, EditorGUILayout.FloatField(new GUIContent("Frame Rate", "Frames per second used when converting frame numbers into clip time."), frameRate));

        if (operationMode == OperationMode.CreateClip)
        {
            EditorGUILayout.BeginHorizontal();
            outputFolder = EditorGUILayout.TextField(new GUIContent("Output Folder", "Must be inside Assets."), outputFolder);
            if (GUILayout.Button("Browse", GUILayout.Width(70f)))
                PickOutputFolder();
            EditorGUILayout.EndHorizontal();

            clipName = EditorGUILayout.TextField("Clip Name", clipName);
            createKeyMode = (CreateKeyMode)EditorGUILayout.EnumPopup(new GUIContent("Create Keys", "Two keys gives Unity a tiny held duration. Single key records only frame 0."), createKeyMode);

            using (new EditorGUI.DisabledScope(createKeyMode == CreateKeyMode.SingleKey))
            {
                durationFrames = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent("Duration Frames", "Default is 1, giving keys at frame 0 and frame 1."), durationFrames));
                invertFirstToggleKey = EditorGUILayout.Toggle(new GUIContent("Opposite First Toggle Key", "For active/enabled properties only, frame 0 is the opposite value and the final key is the current value."), invertFirstToggleKey);
            }

            loopClip = EditorGUILayout.Toggle(new GUIContent("Loop Time", "Sets the generated clip's loopTime import setting."), loopClip);
        }
        else
        {
            copyFoldout = EditorGUILayout.Foldout(copyFoldout, "Copy / Apply Settings", true);
            if (copyFoldout)
            {
                DrawCopyApplyTargetSettings();
                using (new EditorGUI.DisabledScope(copyApplyTargetMode == CopyApplyTargetMode.AnimationWindow))
                {
                    pasteTargetClip = (AnimationClip)EditorGUILayout.ObjectField(new GUIContent("Target Clip", "Existing clip to receive copied one-frame keys."), pasteTargetClip, typeof(AnimationClip), false);
                }
                pasteFrame = Mathf.Max(0, EditorGUILayout.IntField(new GUIContent("Apply At Frame", "Frame index in the target clip."), pasteFrame));

                if (copiedBuffer != null && copiedBuffer.Keys.Count + copiedBuffer.ObjectKeys.Count > 0)
                {
                    EditorGUILayout.LabelField("Copied: " + (copiedBuffer.Keys.Count + copiedBuffer.ObjectKeys.Count) + " keys from " + copiedBuffer.RootName + " at " + copiedBuffer.CapturedAt.ToShortTimeString(), miniMutedStyle);
                }
                else
                {
                    EditorGUILayout.LabelField("Copied: nothing yet", miniMutedStyle);
                }
            }
        }

        DrawRootWarning();
        EditorGUILayout.EndVertical();
    }

    private void DrawObjects()
    {
        objectsFoldout = BeginFoldoutPanel(objectsFoldout, "Objects");
        if (!objectsFoldout)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Add Selected"))
        {
            List<GameObject> selected = AddSelectedObjects();
            ApplyActiveQuickAddModeToTargets(selected);
        }

        if (GUILayout.Button("Clear Objects"))
        {
            targets.Clear();
            PrunePropertySetsToCurrentTargets();
        }
        EditorGUILayout.EndHorizontal();

        for (int i = 0; i < targets.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            GameObject previous = targets[i];
            targets[i] = (GameObject)EditorGUILayout.ObjectField("Object " + (i + 1), targets[i], typeof(GameObject), true);
            if (targets[i] != previous)
            {
                PrunePropertySetsToCurrentTargets();
                ApplyActiveQuickAddModeToTarget(targets[i]);
            }

            if (GUILayout.Button("X", GUILayout.Width(24f)))
            {
                targets.RemoveAt(i);
                PrunePropertySetsToCurrentTargets();
                i--;
            }
            EditorGUILayout.EndHorizontal();
        }

        GameObject added = (GameObject)EditorGUILayout.ObjectField("Add Object", null, typeof(GameObject), true);
        if (added != null)
        {
            AddTarget(added);
            ApplyActiveQuickAddModeToTarget(added);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawObjectSearch()
    {
        objectSearchFoldout = BeginFoldoutPanel(objectSearchFoldout, "Object Search");
        if (!objectSearchFoldout)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        includeInactiveSearch = EditorGUILayout.Toggle(new GUIContent("Include Inactive", "Search inactive children under the selected root objects."), includeInactiveSearch);

        EditorGUILayout.BeginHorizontal();
        objectSearchScopeRoot = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Search Under", "Optional. Narrows object and component search to this object and its children."), objectSearchScopeRoot, typeof(GameObject), true);
        using (new EditorGUI.DisabledScope(objectSearchScopeRoot == null))
        {
            if (GUILayout.Button("Clear", GUILayout.Width(52f)))
                objectSearchScopeRoot = null;
        }
        EditorGUILayout.EndHorizontal();

        GameObject searchRoot = GetPrimarySearchRootObject();
        EditorGUILayout.LabelField("Scope: " + (searchRoot != null ? GetTargetLabel(searchRoot) : "None"), miniMutedStyle);

        EditorGUILayout.BeginHorizontal();
        objectNameSearch = EditorGUILayout.TextField(new GUIContent("Name Contains", "Searches under Search Under, Animation Root/avatar, or the Objects list."), objectNameSearch);
        using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(objectNameSearch)))
        {
            if (GUILayout.Button("Add Matches", GUILayout.Width(100f)))
                AddObjectsByNameSearch();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Add objects that have component:", EditorStyles.miniBoldLabel);
        EditorGUILayout.BeginHorizontal();
        ComponentQuickButton<Light>("Lights");
        ComponentQuickButton<AudioSource>("Audio");
        ComponentQuickButton<SkinnedMeshRenderer>("Skinned Mesh");
        ComponentQuickButton<MeshRenderer>("Mesh Renderer");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        ComponentQuickButton<Renderer>("Any Renderer");
        ComponentQuickButton<Collider>("Colliders");
        ComponentQuickButton<ParticleSystem>("Particles");
        ComponentQuickButton<Animator>("Animators");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        componentNameSearch = EditorGUILayout.TextField(new GUIContent("Component Contains", "Example: Light, Audio, Skinned, Constraint."), componentNameSearch);
        using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(componentNameSearch)))
        {
            if (GUILayout.Button("Add Matches", GUILayout.Width(100f)))
                AddObjectsByComponentNameSearch();
        }
        EditorGUILayout.EndHorizontal();

        DrawComponentSearchDropdowns();

        EditorGUILayout.LabelField("Search source is Search Under first, then Animation Root/avatar, then the Objects list.", miniMutedStyle);
        EditorGUILayout.EndVertical();
    }

    private void DrawQuickAdd()
    {
        quickAddFoldout = BeginFoldoutPanel(quickAddFoldout, "Quick Add");
        if (!quickAddFoldout)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        QuickAddModeButton("Object Toggle", QuickAddMode.ObjectToggle);
        QuickAddModeButton("Position", QuickAddMode.Position);
        QuickAddModeButton("Rotation", QuickAddMode.Rotation);
        QuickAddModeButton("Scale", QuickAddMode.Scale);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Current: " + GetQuickAddModeLabel(activeQuickAddMode), EditorStyles.miniLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Off", GUILayout.Width(48f)))
            activeQuickAddMode = QuickAddMode.None;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Choose a mode, then use Add Selected or drag objects into the Objects list. New objects keep that property setup until you switch modes or turn it off.", miniMutedStyle);
        EditorGUILayout.EndVertical();
    }

    private void DrawMaterialSwap()
    {
        materialSwapFoldout = BeginFoldoutPanel(materialSwapFoldout, "Material Swap");
        if (!materialSwapFoldout)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        materialSwapMaterial = (Material)EditorGUILayout.ObjectField(new GUIContent("Swap Material", "Material to key onto renderer material slots."), materialSwapMaterial, typeof(Material), false);
        materialSwapAllSlots = EditorGUILayout.Toggle(new GUIContent("All Material Slots", "Off keys only slot 0. On keys every material slot on each renderer in the Objects list."), materialSwapAllSlots);

        List<GameObject> objectListTargets = ValidTargets();
        int rendererCount = objectListTargets.Count(go => go.GetComponent<Renderer>() != null);
        EditorGUILayout.LabelField("Object list renderers: " + rendererCount, miniMutedStyle);

        using (new EditorGUI.DisabledScope(materialSwapMaterial == null || rendererCount == 0))
        {
            if (GUILayout.Button("Add Material Swap For Objects", GUILayout.Height(28f)))
                AddMaterialSwapForObjects();
        }

        EditorGUILayout.LabelField("Uses the Objects list, not the current hierarchy selection. Two-key clips keep the current slot material at frame 0 and swap to this material at the final frame. Copy/apply writes one frame.", miniMutedStyle);
        EditorGUILayout.EndVertical();
    }
    private void DrawPropertySearch()
    {
        propertySearchFoldout = BeginFoldoutPanel(propertySearchFoldout, "Component And Property Search");
        if (!propertySearchFoldout)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        propertySearch = EditorGUILayout.TextField(new GUIContent("Search", "Search target names, component names, and animatable property names."), propertySearch);

        if (string.IsNullOrWhiteSpace(propertySearch))
        {
            EditorGUILayout.LabelField("Type things like transform, blendshape, material, color, light, volume, enabled, position, or a component name.", miniMutedStyle);
            EditorGUILayout.EndVertical();
            return;
        }

        List<BindingSearchResult> results = GetCachedPropertySearchResults(propertySearch);
        if (results.Count == 0)
        {
            EditorGUILayout.LabelField("No matching animatable float properties found on the current objects.", miniMutedStyle);
            EditorGUILayout.EndVertical();
            return;
        }

        propertySearchScroll = EditorGUILayout.BeginScrollView(propertySearchScroll, GUILayout.Height(220f));
        int shown = 0;
        foreach (BindingSearchResult result in results)
        {
            if (shown >= MaxSearchResults)
                break;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(result.Label, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("+", GUILayout.Width(26f)))
                AddBindingSet(result.Target, result.SourceComponent, result.Label, new[] { MakeSpec(result.Target, result.SourceComponent, result.Binding, HumanizePropertyName(result.Binding.propertyName), ManualValueKind.None, -1) });
            EditorGUILayout.EndHorizontal();
            shown++;
        }
        EditorGUILayout.EndScrollView();

        if (results.Count > MaxSearchResults)
            EditorGUILayout.LabelField("Showing first " + MaxSearchResults + " of " + results.Count + " matches. Narrow the search for more.", miniMutedStyle);

        EditorGUILayout.EndVertical();
    }

    private void DrawChosenProperties()
    {
        chosenPropertiesFoldout = BeginFoldoutPanel(chosenPropertiesFoldout, "Chosen Properties");
        if (!chosenPropertiesFoldout)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        PrunePropertySetsToCurrentTargets();

        if (propertySets.Count == 0)
        {
            EditorGUILayout.LabelField("Add favorites or search for properties to build a clip.", miniMutedStyle);
            EditorGUILayout.EndVertical();
            return;
        }

        for (int i = 0; i < propertySets.Count; i++)
        {
            PropertySet set = propertySets[i];
            for (int specIndex = 0; specIndex < set.Specs.Count; specIndex++)
            {
                BindingSpec spec = set.Specs[specIndex];
                if (spec == null || spec.Target == null)
                    continue;

                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                GUILayout.Label(GetTargetLabel(spec.Target) + " / " + spec.Label, EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                DrawInlineSpecValueControls(spec);
                if (GUILayout.Button("X", GUILayout.Width(22f)))
                {
                    set.Specs.RemoveAt(specIndex);
                    specIndex--;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (set.Specs.Count == 0)
            {
                propertySets.RemoveAt(i);
                i--;
            }
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawActions()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        List<string> warnings;
        List<CapturedFloat> preview = CaptureCurrentValues(out warnings);
        List<CapturedObjectReference> objectPreview = CaptureObjectReferenceValues(warnings);
        int readyKeyCount = preview.Count + objectPreview.Count;

        EditorGUILayout.LabelField("Ready Keys: " + readyKeyCount, EditorStyles.boldLabel);
        foreach (string warning in warnings.Take(4))
            EditorGUILayout.HelpBox(warning, MessageType.Warning);

        if (warnings.Count > 4)
            EditorGUILayout.HelpBox((warnings.Count - 4) + " more warnings hidden.", MessageType.Warning);

        using (new EditorGUI.DisabledScope(readyKeyCount == 0))
        {
            if (operationMode == OperationMode.CreateClip)
            {
                if (GUILayout.Button("Create Animation Clip", GUILayout.Height(34f)))
                    CreateAnimationClip();
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Copy Current One Frame", GUILayout.Height(34f)))
                    CopyCurrentFrame();

                using (new EditorGUI.DisabledScope(copiedBuffer == null || copiedBuffer.Keys.Count + copiedBuffer.ObjectKeys.Count == 0 || GetCurrentPasteTargetClip() == null))
                {
                    if (GUILayout.Button("Apply Copied Frame To Clip", GUILayout.Height(34f)))
                        ApplyCopiedFrameToClip();
                }
                EditorGUILayout.EndHorizontal();

                using (new EditorGUI.DisabledScope(GetCurrentPasteTargetClip() == null))
                {
                    if (GUILayout.Button("Apply Current Frame Directly To Clip"))
                    {
                        CopyCurrentFrame();
                        ApplyCopiedFrameToClip();
                    }
                }
            }
        }

        EditorGUILayout.EndVertical();
    }

    private bool BeginFoldoutPanel(bool value, string label)
    {
        EditorGUILayout.Space(4f);
        Rect rect = EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        value = EditorGUILayout.Foldout(value, label, true, EditorStyles.foldout);
        EditorGUILayout.EndHorizontal();
        EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
        return value;
    }

    private void FavoriteButton(string label, Action action)
    {
        if (GUILayout.Button(label, GUILayout.Height(26f)))
            action();
    }

    private void HandleCopyApplyShortcuts()
    {
        Event current = Event.current;
        if (current == null || current.type != EventType.KeyDown)
            return;

        if (!current.control || current.keyCode != KeyCode.V)
            return;

        if (operationMode != OperationMode.CopyApply || copiedBuffer == null || copiedBuffer.Keys.Count + copiedBuffer.ObjectKeys.Count == 0 || GetCurrentPasteTargetClip() == null)
            return;

        ApplyCopiedFrameToClip();
        current.Use();
    }

    private void QuickAddModeButton(string label, QuickAddMode mode)
    {
        bool selected = activeQuickAddMode == mode;
        bool clicked;
        using (new EditorGUI.DisabledScope(selected))
        {
            clicked = GUILayout.Button(label, GUILayout.Height(26f));
        }

        if (!clicked)
            return;

        activeQuickAddMode = mode;
    }

    private string GetQuickAddModeLabel(QuickAddMode mode)
    {
        switch (mode)
        {
            case QuickAddMode.ObjectToggle:
                return "Object Toggle";
            case QuickAddMode.Position:
                return "Position";
            case QuickAddMode.Rotation:
                return "Rotation";
            case QuickAddMode.Scale:
                return "Scale";
            default:
                return "Off";
        }
    }

    private void ApplyActiveQuickAddModeToTargets(IEnumerable<GameObject> targetObjects)
    {
        if (activeQuickAddMode == QuickAddMode.None)
            return;

        foreach (GameObject target in targetObjects.Where(go => go != null).Distinct())
            ApplyActiveQuickAddModeToTarget(target);
    }

    private void ApplyActiveQuickAddModeToTarget(GameObject target)
    {
        if (target == null || activeQuickAddMode == QuickAddMode.None)
            return;

        switch (activeQuickAddMode)
        {
            case QuickAddMode.ObjectToggle:
                RemoveQuickAddSpecsForTarget(target);
                AddObjectToggleForTarget(target, false, true);
                break;
            case QuickAddMode.Position:
                RemoveQuickAddSpecsForTarget(target);
                AddTransformPropertiesForTarget(target, "Position", true, false, false, false, true);
                break;
            case QuickAddMode.Rotation:
                RemoveQuickAddSpecsForTarget(target);
                AddTransformPropertiesForTarget(target, "Rotation", false, true, false, false, true);
                break;
            case QuickAddMode.Scale:
                RemoveQuickAddSpecsForTarget(target);
                AddTransformPropertiesForTarget(target, "Scale", false, false, true, false, true);
                break;
        }
    }

    private void RemoveQuickAddSpecsForTarget(GameObject target)
    {
        if (target == null)
            return;

        int targetId = target.GetInstanceID();
        for (int i = propertySets.Count - 1; i >= 0; i--)
        {
            PropertySet set = propertySets[i];
            set.Specs.RemoveAll(spec => spec != null && spec.IsQuickAdd && spec.Target != null && spec.Target.GetInstanceID() == targetId);
            if (set.Specs.Count == 0)
                propertySets.RemoveAt(i);
        }
    }

    private void PrunePropertySetsToCurrentTargets()
    {
        HashSet<int> targetIds = new HashSet<int>(ValidTargets().Select(target => target.GetInstanceID()));
        for (int i = propertySets.Count - 1; i >= 0; i--)
        {
            PropertySet set = propertySets[i];
            set.Specs.RemoveAll(spec => spec == null || spec.Target == null || !targetIds.Contains(spec.Target.GetInstanceID()));
            if (set.Specs.Count == 0)
                propertySets.RemoveAt(i);
        }
    }

    private void DrawInlineSpecValueControls(BindingSpec spec)
    {
        if (spec == null || spec.Target == null)
            return;

        GameObject root = ResolveRootForTarget(spec.Target);
        if (root == null)
            return;

        if (spec.IsObjectReference)
        {
            UnityEngine.Object sceneObject;
            bool hasSceneObject = TryReadCurrentObjectReferenceValue(spec, out sceneObject);
            spec.CustomObjectReferenceValue = EditorGUILayout.ObjectField(spec.CustomObjectReferenceValue, typeof(Material), false, GUILayout.Width(110f));

            using (new EditorGUI.DisabledScope(!hasSceneObject))
            {
                if (GUILayout.Button("Grab", GUILayout.Width(44f)))
                    spec.CustomObjectReferenceValue = sceneObject;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(sceneObject, typeof(Material), false, GUILayout.Width(90f));
            }

            return;
        }

        float sceneValue;
        bool hasSceneValue = TryReadCurrentSceneValue(spec, root, MakeRuntimeBinding(spec, root), out sceneValue);

        spec.UseCustomValue = true;
        if (spec.IsToggleLike)
        {
            bool toggle = EditorGUILayout.Toggle(spec.CustomValue > 0.5f, GUILayout.Width(24f));
            spec.CustomValue = toggle ? 1f : 0f;
        }
        else
        {
            spec.CustomValue = EditorGUILayout.FloatField(spec.CustomValue, GUILayout.Width(72f));
        }

        using (new EditorGUI.DisabledScope(!hasSceneValue))
        {
            if (GUILayout.Button("Grab", GUILayout.Width(44f)))
            {
                spec.CustomValue = sceneValue;
                spec.UseCustomValue = true;
            }
        }

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.TextField(hasSceneValue ? FormatFloat(sceneValue) : "?", GUILayout.Width(58f));
        }
    }

    private void ComponentQuickButton<T>(string label) where T : Component
    {
        if (GUILayout.Button(label))
            AddObjectsWithComponent(typeof(T));
    }

    private void DrawRootWarning()
    {
        List<GameObject> valid = ValidTargets();
        if (valid.Count == 0)
            return;

        GameObject root = GetEffectiveRoot();
        if (root == null)
            return;

        int outside = valid.Count(target => !IsChildOf(target.transform, root.transform));
        if (outside > 0)
            EditorGUILayout.HelpBox(outside + " object(s) are not under the current animation root. They will be captured as their own roots, which is usually not what you want for avatar clips.", MessageType.Warning);
    }

    private void PickOutputFolder()
    {
        string startFolder = Application.dataPath;
        if (!string.IsNullOrEmpty(outputFolder) && outputFolder.StartsWith("Assets", StringComparison.Ordinal))
            startFolder = Application.dataPath + outputFolder.Substring("Assets".Length);

        string picked = EditorUtility.OpenFolderPanel("Animation Output Folder", startFolder, "");
        if (string.IsNullOrEmpty(picked))
            return;

        picked = picked.Replace("\\", "/");
        string dataPath = Application.dataPath.Replace("\\", "/");
        if (!picked.StartsWith(dataPath, StringComparison.Ordinal))
        {
            EditorUtility.DisplayDialog("Folder Must Be In Assets", "Animation clips need to be saved somewhere inside this Unity project's Assets folder.", "OK");
            return;
        }

        outputFolder = "Assets" + picked.Substring(dataPath.Length);
    }

    private List<GameObject> AddSelectedObjects()
    {
        List<GameObject> selected = Selection.gameObjects.Where(go => go != null).ToList();
        selected.Sort(CompareHierarchyOrder);
        foreach (GameObject go in selected)
            AddTarget(go);

        return selected;
    }

    private void AddTarget(GameObject go)
    {
        if (go == null)
            return;

        if (!targets.Contains(go))
            targets.Add(go);
    }

    private void CleanupTargetList()
    {
        if (targets == null)
            targets = new List<GameObject>();

        for (int i = targets.Count - 1; i >= 0; i--)
        {
            if (targets[i] == null)
                targets.RemoveAt(i);
        }
    }

    private List<GameObject> ValidTargets()
    {
        CleanupTargetList();
        return targets.Where(go => go != null).Distinct().ToList();
    }

    private void AddObjectsByNameSearch()
    {
        string query = objectNameSearch.Trim();
        foreach (GameObject go in EnumerateSearchObjects())
        {
            if (go.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                AddTarget(go);
        }
    }

    private void AddObjectsWithComponent(Type componentType)
    {
        foreach (Transform root in GetSearchRoots())
        {
            Component[] components = root.GetComponentsInChildren(componentType, includeInactiveSearch);
            Array.Sort(components, CompareComponentHierarchyOrder);
            foreach (Component component in components)
            {
                if (component != null)
                    AddTarget(component.gameObject);
            }
        }
    }

    private void AddObjectsByComponentNameSearch()
    {
        string query = componentNameSearch.Trim();
        foreach (Transform root in GetSearchRoots())
        {
            Component[] components = root.GetComponentsInChildren<Component>(includeInactiveSearch);
            Array.Sort(components, CompareComponentHierarchyOrder);
            foreach (Component component in components)
            {
                if (component == null)
                    continue;

                if (component.GetType().Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    AddTarget(component.gameObject);
            }
        }
    }

    private IEnumerable<GameObject> EnumerateSearchObjects()
    {
        foreach (Transform root in GetSearchRoots())
        {
            List<Transform> ordered = new List<Transform>();
            AddTransformsDepthFirst(root, ordered);
            foreach (Transform transform in ordered)
            {
                if (includeInactiveSearch || transform.gameObject.activeInHierarchy)
                    yield return transform.gameObject;
            }
        }
    }

    private IEnumerable<Transform> GetSearchRoots()
    {
        List<Transform> roots = new List<Transform>();

        if (objectSearchScopeRoot != null)
        {
            roots.Add(objectSearchScopeRoot.transform);
        }
        else if (animationRoot != null)
        {
            roots.Add(animationRoot.transform);
        }
        else
        {
            TryAssignActiveAvatarRoot(false);
            if (animationRoot != null)
            {
                roots.Add(animationRoot.transform);
            }
            else
            {
                foreach (GameObject target in ValidTargets())
                    roots.Add(target.transform);
            }
        }

        return TopLevelTransforms(roots);
    }

    private GameObject GetPrimarySearchRootObject()
    {
        Transform root = GetSearchRoots().FirstOrDefault();
        return root != null ? root.gameObject : null;
    }

    private void AddObjectToggles()
    {
        foreach (GameObject target in ValidTargets())
            AddObjectToggleForTarget(target, true, false);
    }

    private void AddObjectToggleForTarget(GameObject target, bool notifyIfDuplicate, bool isQuickAdd)
    {
        if (target == null)
            return;

        BindingSpec spec = new BindingSpec
        {
            Target = target,
            SourceComponent = null,
            BindingType = typeof(GameObject),
            PropertyName = "m_IsActive",
            Label = "Active Toggle",
            ManualKind = ManualValueKind.GameObjectActive,
            IsQuickAdd = isQuickAdd
        };

        AddBindingSet(target, null, GetTargetLabel(target) + " / Active Toggle", new[] { spec }, notifyIfDuplicate);
    }

    private void AddEnabledProperties()
    {
        foreach (GameObject target in ValidTargets())
        {
            foreach (Component component in target.GetComponents<Component>())
            {
                if (component == null || !SupportsEnabled(component))
                    continue;

                AddComponentEnabledSpec(target, component);
            }
        }
    }

    private void AddRendererEnabled()
    {
        foreach (GameObject target in ValidTargets())
        {
            Renderer renderer = target.GetComponent<Renderer>();
            if (renderer != null)
                AddComponentEnabledSpec(target, renderer);
        }
    }

    private void AddComponentEnabledSpec(GameObject target, Component component)
    {
        BindingSpec spec = new BindingSpec
        {
            Target = target,
            SourceComponent = component,
            BindingType = component.GetType(),
            PropertyName = "m_Enabled",
            Label = component.GetType().Name + " Enabled",
            ManualKind = ManualValueKind.ComponentEnabled
        };

        AddBindingSet(target, component, GetTargetLabel(target) + " / " + component.GetType().Name + " Enabled", new[] { spec });
    }

    private void AddTransformTRS()
    {
        AddTransformProperties("Transform TRS", true, true, true);
    }

    private void AddTransformPosition()
    {
        AddTransformProperties("Position", true, false, false);
    }

    private void AddTransformRotation()
    {
        AddTransformProperties("Rotation", false, true, false);
    }

    private void AddTransformScale()
    {
        AddTransformProperties("Scale", false, false, true);
    }

    private void AddTransformProperties(string label, bool includePosition, bool includeRotation, bool includeScale)
    {
        foreach (GameObject target in ValidTargets())
            AddTransformPropertiesForTarget(target, label, includePosition, includeRotation, includeScale, true, false);
    }

    private void AddTransformPropertiesForTarget(GameObject target, string label, bool includePosition, bool includeRotation, bool includeScale, bool notifyIfDuplicate, bool isQuickAdd)
    {
        if (target == null)
            return;

        List<BindingSpec> allTransformSpecs = GetAnimatableSpecs(target, target.transform, typeof(Transform), binding => true, label).ToList();
        List<BindingSpec> specs = new List<BindingSpec>();

        if (includePosition)
            specs.AddRange(allTransformSpecs.Where(spec => IsTransformPositionName(spec.PropertyName)));

        if (includeRotation)
        {
            List<BindingSpec> rotationSpecs = allTransformSpecs.Where(spec => IsTransformRotationName(spec.PropertyName)).ToList();
            List<BindingSpec> eulerSpecs = rotationSpecs.Where(spec => IsTransformEulerName(spec.PropertyName)).ToList();
            specs.AddRange(eulerSpecs.Count > 0 ? eulerSpecs : rotationSpecs);
        }

        if (includeScale)
            specs.AddRange(allTransformSpecs.Where(spec => IsTransformScaleName(spec.PropertyName)));

        foreach (BindingSpec spec in specs)
            spec.IsQuickAdd = isQuickAdd;

        AddBindingSet(target, target.transform, GetTargetLabel(target) + " / " + label, specs, notifyIfDuplicate);
    }

    private void AddBlendShapes()
    {
        foreach (GameObject target in ValidTargets())
        {
            SkinnedMeshRenderer renderer = target.GetComponent<SkinnedMeshRenderer>();
            if (renderer == null)
                continue;

            List<BindingSpec> specs = GetAnimatableSpecs(target, renderer, typeof(SkinnedMeshRenderer), binding => binding.propertyName.StartsWith("blendShape.", StringComparison.OrdinalIgnoreCase), "BlendShape").ToList();

            if (specs.Count == 0 && renderer.sharedMesh != null)
            {
                for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
                {
                    string shapeName = renderer.sharedMesh.GetBlendShapeName(i);
                    specs.Add(new BindingSpec
                    {
                        Target = target,
                        SourceComponent = renderer,
                        BindingType = typeof(SkinnedMeshRenderer),
                        PropertyName = "blendShape." + shapeName,
                        Label = "BlendShape " + shapeName,
                        ManualKind = ManualValueKind.BlendShape,
                        ManualIndex = i
                    });
                }
            }

            AddBindingSet(target, renderer, GetTargetLabel(target) + " / BlendShapes", specs);
        }
    }

    private void AddMaterialSwapForObjects()
    {
        if (materialSwapMaterial == null)
            return;

        List<GameObject> objectListTargets = ValidTargets();
        objectListTargets.Sort(CompareHierarchyOrder);
        int changed = 0;

        foreach (GameObject target in objectListTargets)
        {
            Renderer renderer = target.GetComponent<Renderer>();
            if (renderer == null)
                continue;

            Material[] materials = renderer.sharedMaterials;
            int slotCount = Mathf.Max(1, materials != null ? materials.Length : 0);
            int slotsToAdd = materialSwapAllSlots ? slotCount : 1;
            List<BindingSpec> specs = new List<BindingSpec>();

            for (int slot = 0; slot < slotsToAdd; slot++)
            {
                string propertyName = GetMaterialSlotPropertyName(slot);
                BindingSpec existing = FindExistingSpec(target, typeof(Renderer), propertyName);
                if (existing != null && existing.IsObjectReference)
                {
                    existing.CustomObjectReferenceValue = materialSwapMaterial;
                    changed++;
                    continue;
                }

                specs.Add(new BindingSpec
                {
                    Target = target,
                    SourceComponent = renderer,
                    BindingType = typeof(Renderer),
                    PropertyName = propertyName,
                    Label = "Material Slot " + slot,
                    IsObjectReference = true,
                    CustomObjectReferenceValue = materialSwapMaterial,
                    ObjectReferenceIndex = slot
                });
            }

            if (specs.Count > 0)
            {
                AddBindingSet(target, renderer, GetTargetLabel(target) + " / Material Swap", specs, false);
                changed += specs.Count;
            }
        }

        ShowNotification(new GUIContent(changed > 0 ? "Added material swap keys" : "No renderer slots added from Objects list"));
    }

    private static string GetMaterialSlotPropertyName(int slot)
    {
        return "m_Materials.Array.data[" + Mathf.Max(0, slot) + "]";
    }
    private void AddMaterialProperties()
    {
        materialSearchFoldout = true;
        ShowNotification(new GUIContent("Use Material Property Search"));
    }

    private void AddLightProperties()
    {
        AddFilteredAnimatable("Light Properties", typeof(Light), binding => true);
    }

    private void AddAudioProperties()
    {
        AddFilteredAnimatable("Audio Properties", typeof(AudioSource), binding => true);
    }

    private void AddAllAnimatableProperties()
    {
        foreach (GameObject target in ValidTargets())
        {
            List<BindingSpec> specs = GetAnimatableSpecs(target, null, null, binding => !binding.isPPtrCurve, "Animatable").ToList();
            AddBindingSet(target, null, GetTargetLabel(target) + " / All Float Properties", specs);
        }
    }

    private void AddFilteredAnimatable(string label, Type componentType, Func<EditorCurveBinding, bool> filter)
    {
        foreach (GameObject target in ValidTargets())
        {
            Component source = FindComponentForBindingType(target, componentType);
            if (source == null)
                continue;

            List<BindingSpec> specs = GetAnimatableSpecs(target, source, componentType, filter, label).ToList();
            AddBindingSet(target, source, GetTargetLabel(target) + " / " + label, specs);
        }
    }

    private IEnumerable<BindingSpec> GetAnimatableSpecs(GameObject target, Component source, Type componentType, Func<EditorCurveBinding, bool> filter, string labelPrefix)
    {
        GameObject root = ResolveRootForTarget(target);
        if (root == null)
            yield break;

        EditorCurveBinding[] bindings;
        try
        {
            bindings = AnimationUtility.GetAnimatableBindings(target, root);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (EditorCurveBinding binding in bindings)
        {
            if (binding.isPPtrCurve)
                continue;

            if (componentType != null && !BindingTypeMatches(binding.type, componentType))
                continue;

            if (filter != null && !filter(binding))
                continue;

            Component bindingSource = source != null && BindingTypeMatches(binding.type, source.GetType())
                ? source
                : FindComponentForBindingType(target, binding.type);

            yield return MakeSpec(target, bindingSource, binding, labelPrefix + " " + HumanizePropertyName(binding.propertyName), ManualValueKind.None, -1);
        }
    }

    private List<BindingSearchResult> BuildPropertySearchResults(string query)
    {
        List<BindingSearchResult> results = new List<BindingSearchResult>();
        string trimmed = query.Trim();
        HashSet<string> seen = new HashSet<string>();

        foreach (GameObject target in ValidTargets())
        {
            GameObject root = ResolveRootForTarget(target);
            if (root == null)
                continue;

            EditorCurveBinding[] bindings;
            try
            {
                bindings = AnimationUtility.GetAnimatableBindings(target, root);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (EditorCurveBinding binding in bindings)
            {
                if (binding.isPPtrCurve)
                    continue;

                string humanProperty = HumanizePropertyName(binding.propertyName);
                string label = GetTargetLabel(target) + " / " + binding.type.Name + " / " + humanProperty;
                string haystack = label + " " + binding.propertyName;

                if (haystack.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string key = GetSpecKey(target, binding.type, binding.propertyName);
                if (!seen.Add(key))
                    continue;

                results.Add(new BindingSearchResult
                {
                    Target = target,
                    SourceComponent = FindComponentForBindingType(target, binding.type),
                    Binding = binding,
                    Label = label
                });
            }
        }

        results.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    private BindingSpec MakeSpec(GameObject target, Component source, EditorCurveBinding binding, string label, ManualValueKind manualKind, int manualIndex)
    {
        return new BindingSpec
        {
            Target = target,
            SourceComponent = source,
            BindingType = binding.type,
            PropertyName = binding.propertyName,
            Label = label,
            ManualKind = manualKind,
            ManualIndex = manualIndex
        };
    }

    private void AddBindingSet(GameObject target, Component source, string label, IEnumerable<BindingSpec> specs)
    {
        AddBindingSet(target, source, label, specs, true);
    }

    private void AddBindingSet(GameObject target, Component source, string label, IEnumerable<BindingSpec> specs, bool notifyIfDuplicate)
    {
        if (target == null)
            return;

        List<BindingSpec> newSpecs = new List<BindingSpec>();
        foreach (BindingSpec spec in specs)
        {
            if (spec == null || spec.Target == null || spec.BindingType == null || string.IsNullOrEmpty(spec.PropertyName))
                continue;

            if (HasSpec(spec.Target, spec.BindingType, spec.PropertyName))
                continue;

            InitializeSpecCustomValue(spec);
            newSpecs.Add(spec);
        }

        if (newSpecs.Count == 0)
        {
            if (notifyIfDuplicate)
                ShowNotification(new GUIContent("Nothing new to add"));
            return;
        }

        PropertySet set = new PropertySet
        {
            Label = label,
            Target = target,
            SourceComponent = source,
            Expanded = false
        };

        set.Specs.AddRange(newSpecs);
        propertySets.Add(set);
    }

    private bool HasSpec(GameObject target, Type bindingType, string propertyName)
    {
        string key = GetSpecKey(target, bindingType, propertyName);
        foreach (PropertySet set in propertySets)
        {
            foreach (BindingSpec spec in set.Specs)
            {
                if (GetSpecKey(spec.Target, spec.BindingType, spec.PropertyName) == key)
                    return true;
            }
        }

        return false;
    }

    private string GetSpecKey(GameObject target, Type bindingType, string propertyName)
    {
        int id = target != null ? target.GetInstanceID() : 0;
        string typeName = bindingType != null ? bindingType.AssemblyQualifiedName : "";
        return id + "|" + typeName + "|" + propertyName;
    }

    private List<CapturedFloat> CaptureCurrentValues(out List<string> warnings)
    {
        warnings = new List<string>();
        List<CapturedFloat> captured = new List<CapturedFloat>();
        HashSet<string> seenBindings = new HashSet<string>();

        foreach (PropertySet set in propertySets)
        {
            foreach (BindingSpec spec in set.Specs)
            {
                if (spec == null)
                    continue;

                if (spec.IsObjectReference)
                    continue;

                if (spec.Target == null)
                {
                    warnings.Add("A chosen property has a missing target.");
                    continue;
                }

                GameObject root = ResolveRootForTarget(spec.Target);
                if (root == null)
                {
                    warnings.Add("No usable root for " + spec.Target.name + ".");
                    continue;
                }

                EditorCurveBinding binding = MakeRuntimeBinding(spec, root);
                float value;
                if (!TryReadValue(spec, root, binding, out value))
                {
                    warnings.Add("Could not read " + spec.Label + " on " + spec.Target.name + ".");
                    continue;
                }

                string bindingKey = GetBindingKey(binding);
                if (!seenBindings.Add(bindingKey))
                    continue;

                captured.Add(new CapturedFloat
                {
                    Binding = binding,
                    Value = value,
                    Label = spec.Label,
                    IsToggleLike = spec.IsToggleLike
                });
            }
        }

        return captured;
    }

    private List<CapturedObjectReference> CaptureObjectReferenceValues(List<string> warnings)
    {
        List<CapturedObjectReference> captured = new List<CapturedObjectReference>();
        HashSet<string> seenBindings = new HashSet<string>();

        foreach (PropertySet set in propertySets)
        {
            foreach (BindingSpec spec in set.Specs)
            {
                if (spec == null || !spec.IsObjectReference)
                    continue;

                if (spec.Target == null)
                {
                    warnings.Add("A chosen material swap has a missing target.");
                    continue;
                }

                GameObject root = ResolveRootForTarget(spec.Target);
                if (root == null)
                {
                    warnings.Add("No usable root for " + spec.Target.name + ".");
                    continue;
                }

                if (spec.CustomObjectReferenceValue == null)
                {
                    warnings.Add("No material set for " + spec.Label + " on " + spec.Target.name + ".");
                    continue;
                }

                EditorCurveBinding binding = MakeRuntimeBinding(spec, root);
                string bindingKey = GetBindingKey(binding);
                if (!seenBindings.Add(bindingKey))
                    continue;

                UnityEngine.Object sceneObject;
                if (!TryReadCurrentObjectReferenceValue(spec, out sceneObject))
                    sceneObject = spec.CustomObjectReferenceValue;

                captured.Add(new CapturedObjectReference
                {
                    Binding = binding,
                    StartValue = sceneObject,
                    Value = spec.CustomObjectReferenceValue,
                    Label = spec.Label
                });
            }
        }

        return captured;
    }

    private bool TryReadCurrentObjectReferenceValue(BindingSpec spec, out UnityEngine.Object value)
    {
        value = null;
        if (spec == null || !spec.IsObjectReference)
            return false;

        Renderer renderer = spec.SourceComponent as Renderer;
        if (renderer == null && spec.Target != null)
            renderer = spec.Target.GetComponent<Renderer>();

        if (renderer == null || spec.ObjectReferenceIndex < 0)
            return false;

        Material[] materials = renderer.sharedMaterials;
        if (materials == null || spec.ObjectReferenceIndex >= materials.Length)
            return false;

        value = materials[spec.ObjectReferenceIndex];
        return true;
    }
    private bool TryReadValue(BindingSpec spec, GameObject root, EditorCurveBinding binding, out float value)
    {
        value = spec.CustomValue;
        spec.UseCustomValue = true;
        return true;
    }

    private EditorCurveBinding MakeRuntimeBinding(BindingSpec spec, GameObject root)
    {
        string path = "";
        if (spec.Target != null && root != null && spec.Target != root)
            path = AnimationUtility.CalculateTransformPath(spec.Target.transform, root.transform);

        if (spec.IsObjectReference)
            return EditorCurveBinding.PPtrCurve(path, spec.BindingType, spec.PropertyName);

        return EditorCurveBinding.FloatCurve(path, spec.BindingType, spec.PropertyName);
    }

    private void CreateAnimationClip()
    {
        List<string> warnings;
        List<CapturedFloat> captured = CaptureCurrentValues(out warnings);
        List<CapturedObjectReference> capturedObjects = CaptureObjectReferenceValues(warnings);
        if (captured.Count + capturedObjects.Count == 0)
        {
            EditorUtility.DisplayDialog("No Keys To Create", "Add objects and properties first.", "OK");
            return;
        }

        if (!EnsureOutputFolder())
            return;

        string safeName = SanitizeFileName(string.IsNullOrWhiteSpace(clipName) ? "New Animation" : clipName.Trim());
        string assetPath = AssetDatabase.GenerateUniqueAssetPath(NormalizeAssetPath(outputFolder) + "/" + safeName + ".anim");

        AnimationClip clip = new AnimationClip();
        clip.frameRate = frameRate;

        float endTime = Mathf.Max(1, durationFrames) / Mathf.Max(1f, frameRate);
        bool singleKey = createKeyMode == CreateKeyMode.SingleKey;

        foreach (CapturedFloat key in captured)
        {
            float startValue = key.Value;
            if (!singleKey && invertFirstToggleKey && key.IsToggleLike)
                startValue = key.Value > 0.5f ? 0f : 1f;

            AnimationCurve curve = BuildCurve(startValue, key.Value, singleKey, endTime);
            AnimationUtility.SetEditorCurve(clip, key.Binding, curve);
        }

        foreach (CapturedObjectReference key in capturedObjects)
            SetObjectReferenceCurve(clip, key.Binding, key.StartValue, key.Value, singleKey, endTime);

        if (loopClip)
        {
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
        }

        AssetDatabase.CreateAsset(clip, assetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorGUIUtility.PingObject(clip);
        Selection.activeObject = clip;
        ShowNotification(new GUIContent("Created " + (captured.Count + capturedObjects.Count) + " keys"));
    }

    private void CopyCurrentFrame()
    {
        GUI.FocusControl(null);
        EditorGUIUtility.editingTextField = false;

        List<string> warnings;
        List<CapturedFloat> captured = CaptureCurrentValues(out warnings);
        List<CapturedObjectReference> capturedObjects = CaptureObjectReferenceValues(warnings);
        if (captured.Count + capturedObjects.Count == 0)
        {
            EditorUtility.DisplayDialog("No Keys To Copy", "Add objects and properties first.", "OK");
            return;
        }

        copiedBuffer = new CopyBuffer
        {
            FrameRate = frameRate,
            RootName = GetEffectiveRoot() != null ? GetEffectiveRoot().name : "No Root",
            CapturedAt = DateTime.Now
        };

        copiedBuffer.Keys.AddRange(captured);
        copiedBuffer.ObjectKeys.AddRange(capturedObjects);
        AnimationClip nativeTargetClip = GetNativeClipboardSourceClip();
        bool copiedNative = capturedObjects.Count == 0 && TryCopyCurrentFrameToUnityAnimationClipboard(captured, nativeTargetClip);
        if (copiedNative)
            ScheduleNativeClipboardRetry(captured, nativeTargetClip);

        EditorGUIUtility.systemCopyBuffer = copiedNative ? string.Empty : BuildClipboardSummary(copiedBuffer);
        ShowNotification(new GUIContent((copiedNative ? "Copied native " : "Copied ") + (captured.Count + capturedObjects.Count) + " one-frame keys"));
    }

    private bool TryCopyCurrentFrameToUnityAnimationClipboard(List<CapturedFloat> captured, AnimationClip targetClip)
    {
        if (targetClip == null || captured == null || captured.Count == 0)
            return false;

        try
        {
            Type stateType = FindTypeByName("UnityEditorInternal.AnimationWindowState");
            Type curveType = FindTypeByName("UnityEditorInternal.AnimationWindowCurve");
            Type keyframeType = FindTypeByName("UnityEditorInternal.AnimationWindowKeyframe");
            if (stateType == null || curveType == null || keyframeType == null)
                return false;

            Type listType = typeof(List<>).MakeGenericType(keyframeType);
            object keyframeList = Activator.CreateInstance(listType);
            MethodInfo addMethod = listType.GetMethod("Add");
            ConstructorInfo curveConstructor = curveType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(AnimationClip), typeof(EditorCurveBinding), typeof(Type) }, null);
            ConstructorInfo keyframeConstructor = keyframeType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { curveType, typeof(Keyframe) }, null);
            if (addMethod == null || curveConstructor == null || keyframeConstructor == null)
                return false;

            foreach (CapturedFloat capturedKey in captured)
            {
                object curve = curveConstructor.Invoke(new object[] { targetClip, capturedKey.Binding, typeof(float) });
                object keyframe = keyframeConstructor.Invoke(new object[] { curve, MakeKey(0f, capturedKey.Value) });
                addMethod.Invoke(keyframeList, new[] { keyframe });
            }

            FieldInfo clipboardField = stateType.GetField("s_KeyframeClipboard", BindingFlags.Static | BindingFlags.NonPublic);
            if (clipboardField == null)
                return false;

            clipboardField.SetValue(null, keyframeList);
            TryRefreshAnimationWindows();
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Sophia's Animation Creator could not populate Unity's native keyframe clipboard. Falling back to Apply mode. " + exception.Message);
            return false;
        }
    }

    private AnimationClip GetNativeClipboardSourceClip()
    {
        AnimationClip targetClip = GetCurrentPasteTargetClip();
        if (targetClip != null)
            return targetClip;

        if (nativeClipboardFallbackClip == null)
        {
            nativeClipboardFallbackClip = new AnimationClip
            {
                name = "Sophia Native Clipboard Source",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        nativeClipboardFallbackClip.ClearCurves();
        nativeClipboardFallbackClip.frameRate = Mathf.Max(1f, frameRate);
        return nativeClipboardFallbackClip;
    }

    private void ScheduleNativeClipboardRetry(List<CapturedFloat> captured, AnimationClip targetClip)
    {
        List<CapturedFloat> capturedCopy = new List<CapturedFloat>();
        foreach (CapturedFloat key in captured)
        {
            capturedCopy.Add(new CapturedFloat
            {
                Binding = key.Binding,
                Value = key.Value,
                Label = key.Label,
                IsToggleLike = key.IsToggleLike
            });
        }

        EditorApplication.delayCall += () =>
        {
            TryCopyCurrentFrameToUnityAnimationClipboard(capturedCopy, targetClip);
            EditorGUIUtility.systemCopyBuffer = string.Empty;
        };
    }

    private void TryRefreshAnimationWindows()
    {
        Type animationWindowType = FindTypeByName("UnityEditor.AnimationWindow");
        if (animationWindowType == null)
            return;

        UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(animationWindowType);
        foreach (UnityEngine.Object windowObject in windows)
        {
            EditorWindow window = windowObject as EditorWindow;
            if (window != null)
                window.Repaint();
        }
    }

    private void ApplyCopiedFrameToClip()
    {
        AnimationClip targetClip = GetCurrentPasteTargetClip();
        if (targetClip == null)
        {
            EditorUtility.DisplayDialog("No Target Clip", "Assign an existing animation clip first.", "OK");
            return;
        }

        if (copiedBuffer == null || copiedBuffer.Keys.Count + copiedBuffer.ObjectKeys.Count == 0)
        {
            EditorUtility.DisplayDialog("Nothing Copied", "Copy a frame first.", "OK");
            return;
        }

        Undo.RecordObject(targetClip, "Apply Sophia Animation Keys");

        float clipRate = targetClip.frameRate > 0f ? targetClip.frameRate : Mathf.Max(1f, copiedBuffer.FrameRate);
        targetClip.frameRate = clipRate;
        int targetFrame = GetCurrentPasteFrame(targetClip);
        float time = Mathf.Max(0, targetFrame) / clipRate;

        foreach (CapturedFloat key in copiedBuffer.Keys)
            AddOrReplaceKey(targetClip, key.Binding, time, key.Value);

        foreach (CapturedObjectReference key in copiedBuffer.ObjectKeys)
            AddOrReplaceObjectReferenceKey(targetClip, key.Binding, time, key.Value);

        EditorUtility.SetDirty(targetClip);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(targetClip);
        ShowNotification(new GUIContent("Applied " + (copiedBuffer.Keys.Count + copiedBuffer.ObjectKeys.Count) + " keys"));
    }

    private static AnimationCurve BuildCurve(float startValue, float endValue, bool singleKey, float endTime)
    {
        AnimationCurve curve = new AnimationCurve();
        curve.AddKey(MakeKey(0f, startValue));
        if (!singleKey)
            curve.AddKey(MakeKey(endTime, endValue));

        return curve;
    }

    private static void SetObjectReferenceCurve(AnimationClip clip, EditorCurveBinding binding, UnityEngine.Object startValue, UnityEngine.Object endValue, bool singleKey, float endTime)
    {
        List<ObjectReferenceKeyframe> keys = new List<ObjectReferenceKeyframe>
        {
            new ObjectReferenceKeyframe { time = 0f, value = singleKey ? endValue : startValue }
        };

        if (!singleKey)
            keys.Add(new ObjectReferenceKeyframe { time = endTime, value = endValue });

        AnimationUtility.SetObjectReferenceCurve(clip, binding, keys.ToArray());
    }
    private static Keyframe MakeKey(float time, float value)
    {
        Keyframe key = new Keyframe(time, value)
        {
            inTangent = 0f,
            outTangent = 0f,
            weightedMode = WeightedMode.None
        };

        return key;
    }

    private static void AddOrReplaceKey(AnimationClip clip, EditorCurveBinding binding, float time, float value)
    {
        AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
        if (curve == null)
            curve = new AnimationCurve();

        for (int i = curve.length - 1; i >= 0; i--)
        {
            if (Mathf.Abs(curve.keys[i].time - time) < 0.0001f)
                curve.RemoveKey(i);
        }

        curve.AddKey(MakeKey(time, value));
        AnimationUtility.SetEditorCurve(clip, binding, curve);
    }

    private static void AddOrReplaceObjectReferenceKey(AnimationClip clip, EditorCurveBinding binding, float time, UnityEngine.Object value)
    {
        ObjectReferenceKeyframe[] existing = AnimationUtility.GetObjectReferenceCurve(clip, binding) ?? new ObjectReferenceKeyframe[0];
        List<ObjectReferenceKeyframe> keys = existing.Where(key => Mathf.Abs(key.time - time) >= 0.0001f).ToList();
        keys.Add(new ObjectReferenceKeyframe { time = time, value = value });
        keys.Sort((a, b) => a.time.CompareTo(b.time));
        AnimationUtility.SetObjectReferenceCurve(clip, binding, keys.ToArray());
    }
    private bool EnsureOutputFolder()
    {
        outputFolder = NormalizeAssetPath(outputFolder);
        if (string.IsNullOrWhiteSpace(outputFolder) || !outputFolder.StartsWith("Assets", StringComparison.Ordinal))
        {
            EditorUtility.DisplayDialog("Bad Output Folder", "The output folder must be inside Assets.", "OK");
            return false;
        }

        if (AssetDatabase.IsValidFolder(outputFolder))
            return true;

        string[] parts = outputFolder.Split('/');
        string current = "Assets";
        for (int i = 1; i < parts.Length; i++)
        {
            if (string.IsNullOrEmpty(parts[i]))
                continue;

            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);

            current = next;
        }

        return AssetDatabase.IsValidFolder(outputFolder);
    }

    private static string NormalizeAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return DefaultOutputFolder;

        return path.Replace("\\", "/").Trim().TrimEnd('/');
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid.ToString(), "");

        return string.IsNullOrWhiteSpace(fileName) ? "New Animation" : fileName;
    }

    private string BuildClipboardSummary(CopyBuffer buffer)
    {
        List<string> lines = new List<string>();
        lines.Add("Sophia's Animation Creator - one-frame copy");
        lines.Add("Root: " + buffer.RootName);
        lines.Add("Keys: " + (buffer.Keys.Count + buffer.ObjectKeys.Count));
        foreach (CapturedFloat key in buffer.Keys.Take(30))
            lines.Add(key.Binding.path + " | " + key.Binding.type.Name + " | " + key.Binding.propertyName + " = " + key.Value);

        foreach (CapturedObjectReference key in buffer.ObjectKeys.Take(Mathf.Max(0, 30 - buffer.Keys.Count)))
            lines.Add(key.Binding.path + " | " + key.Binding.type.Name + " | " + key.Binding.propertyName + " = " + (key.Value != null ? key.Value.name : "None"));

        int totalKeys = buffer.Keys.Count + buffer.ObjectKeys.Count;
        if (totalKeys > 30)
            lines.Add("... " + (totalKeys - 30) + " more");

        return string.Join(Environment.NewLine, lines.ToArray());
    }

    private GameObject ResolveRootForTarget(GameObject target)
    {
        if (target == null)
            return null;

        GameObject root = GetEffectiveRoot();
        if (root != null && IsChildOf(target.transform, root.transform))
            return root;

        return target;
    }

    private GameObject GetEffectiveRoot()
    {
        if (animationRoot != null)
            return animationRoot;

        List<GameObject> valid = ValidTargets();
        if (valid.Count == 0)
            return null;

        if (valid.Count == 1)
            return valid[0];

        return FindCommonRoot(valid);
    }

    private static GameObject FindCommonRoot(List<GameObject> objects)
    {
        List<Transform> transforms = objects.Where(go => go != null).Select(go => go.transform).ToList();
        if (transforms.Count == 0)
            return null;

        Transform common = transforms[0];
        while (common != null)
        {
            bool allUnder = true;
            foreach (Transform transform in transforms)
            {
                if (!IsChildOf(transform, common))
                {
                    allUnder = false;
                    break;
                }
            }

            if (allUnder)
                return common.gameObject;

            common = common.parent;
        }

        return transforms[0].root.gameObject;
    }

    private static bool IsChildOf(Transform child, Transform possibleParent)
    {
        if (child == null || possibleParent == null)
            return false;

        Transform current = child;
        while (current != null)
        {
            if (current == possibleParent)
                return true;

            current = current.parent;
        }

        return false;
    }

    private static IEnumerable<Transform> TopLevelTransforms(IEnumerable<Transform> transforms)
    {
        List<Transform> list = transforms.Where(transform => transform != null).Distinct().ToList();
        list.Sort(CompareTransformHierarchyOrder);

        foreach (Transform transform in list)
        {
            bool hasSelectedAncestor = list.Any(other => other != transform && IsChildOf(transform, other));
            if (!hasSelectedAncestor)
                yield return transform;
        }
    }

    private static void AddTransformsDepthFirst(Transform root, List<Transform> output)
    {
        if (root == null)
            return;

        output.Add(root);
        for (int i = 0; i < root.childCount; i++)
            AddTransformsDepthFirst(root.GetChild(i), output);
    }

    private static int CompareHierarchyOrder(GameObject a, GameObject b)
    {
        if (a == b)
            return 0;

        if (a == null)
            return 1;

        if (b == null)
            return -1;

        return CompareTransformHierarchyOrder(a.transform, b.transform);
    }

    private static int CompareComponentHierarchyOrder(Component a, Component b)
    {
        if (a == b)
            return 0;

        if (a == null)
            return 1;

        if (b == null)
            return -1;

        return CompareTransformHierarchyOrder(a.transform, b.transform);
    }

    private static int CompareTransformHierarchyOrder(Transform a, Transform b)
    {
        return string.Compare(GetHierarchySortKey(a), GetHierarchySortKey(b), StringComparison.Ordinal);
    }

    private static string GetHierarchySortKey(Transform transform)
    {
        if (transform == null)
            return "";

        List<string> parts = new List<string>();
        Transform current = transform;
        while (current != null)
        {
            parts.Add(current.GetSiblingIndex().ToString("D5") + "_" + current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts.ToArray());
    }

    private string GetTargetLabel(GameObject target)
    {
        if (target == null)
            return "Missing";

        GameObject root = GetEffectiveRoot();
        if (root != null && target != root && IsChildOf(target.transform, root.transform))
            return AnimationUtility.CalculateTransformPath(target.transform, root.transform);

        return target.name;
    }

    private static bool BindingTypeMatches(Type bindingType, Type requestedType)
    {
        if (bindingType == null || requestedType == null)
            return false;

        return bindingType == requestedType || requestedType.IsAssignableFrom(bindingType) || bindingType.IsAssignableFrom(requestedType);
    }

    private static Component FindComponentForBindingType(GameObject target, Type bindingType)
    {
        if (target == null || bindingType == null || bindingType == typeof(GameObject))
            return null;

        Component[] components = target.GetComponents<Component>();
        foreach (Component component in components)
        {
            if (component != null && component.GetType() == bindingType)
                return component;
        }

        foreach (Component component in components)
        {
            if (component != null && BindingTypeMatches(component.GetType(), bindingType))
                return component;
        }

        return null;
    }

    private static bool SupportsEnabled(Component component)
    {
        if (component == null)
            return false;

        PropertyInfo property = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
        return property != null && property.PropertyType == typeof(bool) && property.CanRead;
    }

    private static bool TryGetComponentEnabled(Component component, out float value)
    {
        value = 0f;
        if (component == null)
            return false;

        PropertyInfo property = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
        if (property == null || property.PropertyType != typeof(bool) || !property.CanRead)
            return false;

        try
        {
            value = (bool)property.GetValue(component, null) ? 1f : 0f;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsTransformTRS(EditorCurveBinding binding)
    {
        return IsTransformPosition(binding) || IsTransformRotation(binding) || IsTransformScale(binding);
    }

    private static bool IsTransformPosition(EditorCurveBinding binding)
    {
        return IsTransformPositionName(binding.propertyName);
    }

    private static bool IsTransformRotation(EditorCurveBinding binding)
    {
        return IsTransformRotationName(binding.propertyName);
    }

    private static bool IsTransformScale(EditorCurveBinding binding)
    {
        return IsTransformScaleName(binding.propertyName);
    }

    private static bool IsTransformPositionName(string propertyName)
    {
        string property = propertyName.ToLowerInvariant();
        return property.Contains("localposition") || property.Contains("m_localposition");
    }

    private static bool IsTransformRotationName(string propertyName)
    {
        string property = propertyName.ToLowerInvariant();
        return property.Contains("localeuler") || property.Contains("localrotation") || property.Contains("m_localrotation");
    }

    private static bool IsTransformEulerName(string propertyName)
    {
        string property = propertyName.ToLowerInvariant();
        return property.Contains("localeuler");
    }

    private static bool IsTransformScaleName(string propertyName)
    {
        string property = propertyName.ToLowerInvariant();
        return property.Contains("localscale") || property.Contains("m_localscale");
    }

    private static string HumanizePropertyName(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return "";

        string result = propertyName;
        result = result.Replace("m_", "");
        result = result.Replace("material.", "Material ");
        result = result.Replace("blendShape.", "BlendShape ");
        result = result.Replace(".", " ");
        result = result.Replace("_", " ");
        return result;
    }

    private static string GetBindingKey(EditorCurveBinding binding)
    {
        string typeName = binding.type != null ? binding.type.AssemblyQualifiedName : "";
        return binding.path + "|" + typeName + "|" + binding.propertyName;
    }

    private void RemoveMissingPropertySets()
    {
        for (int i = propertySets.Count - 1; i >= 0; i--)
        {
            PropertySet set = propertySets[i];
            set.Specs.RemoveAll(spec => spec == null || spec.Target == null || spec.BindingType == null || string.IsNullOrEmpty(spec.PropertyName));
            if (set.Specs.Count == 0)
                propertySets.RemoveAt(i);
        }
    }
}

