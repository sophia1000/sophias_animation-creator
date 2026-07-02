using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public partial class SophiasAnimationCreatorWindow
{
    private const string FavoritesPrefsKey = "SophiasAnimationCreator.Favorites.v1";
    private const int MaxMaterialSearchResults = 80;

    private enum CopyApplyTargetMode
    {
        ManualClip,
        AnimationWindow
    }

    private enum ComponentSearchFilter
    {
        All,
        Unity,
        UnityCommon,
        VRChat,
        ModularAvatar,
        VRCFury
    }

    private sealed class ComponentSearchOption
    {
        public string Label;
        public Type Type;
        public string[] TypeNames;
        public int InstanceCount;
    }

    private sealed class AnimationWindowInfo
    {
        public EditorWindow Window;
        public AnimationClip Clip;
        public float? Time;
        public int? Frame;
        public string Label;
    }

    private sealed class PassiveFloatValue
    {
        public GameObject Target;
        public Component SourceComponent;
        public EditorCurveBinding Binding;
        public ManualValueKind ManualKind;
        public int ManualIndex = -1;
        public float Value;
        public string Label;
    }

    [Serializable]
    private sealed class SavedSetupLibrary
    {
        public List<SavedSetupData> Setups = new List<SavedSetupData>();
    }

    [Serializable]
    private sealed class SavedSetupData
    {
        public string Name;
        public float FrameRate;
        public int DurationFrames;
        public int CreateKeyMode;
        public bool InvertFirstToggleKey;
        public bool LoopClip;
        public List<SavedPropertySetData> PropertySets = new List<SavedPropertySetData>();
    }

    [Serializable]
    private sealed class SavedPropertySetData
    {
        public string Label;
        public List<SavedBindingData> Specs = new List<SavedBindingData>();
    }

    [Serializable]
    private sealed class SavedBindingData
    {
        public string TargetPath;
        public string BindingTypeName;
        public string PropertyName;
        public string Label;
        public int ManualKind;
        public int ManualIndex;
        public bool UseCustomValue;
        public float CustomValue;
        public bool IsObjectReference;
        public string ObjectReferenceAssetPath;
        public int ObjectReferenceIndex;
    }

    [SerializeField] private CopyApplyTargetMode copyApplyTargetMode = CopyApplyTargetMode.AnimationWindow;
    [SerializeField] private bool useAnimationWindowFrame = true;
    [SerializeField] private int selectedAnimationWindowIndex;
    [SerializeField] private string newFavoriteName = "New Favorite";
    [SerializeField] private bool passiveCaptureFoldout = true;
    [SerializeField] private bool listenForAvatarChanges;
    [SerializeField] private string materialPropertySearch = "";
    [SerializeField] private bool materialSearchFoldout;
    [SerializeField] private ComponentSearchFilter componentSearchFilter = ComponentSearchFilter.All;
    [SerializeField] private string componentPickerSearch = "";

    private readonly List<SavedSetupData> savedSetups = new List<SavedSetupData>();
    private Vector2 favoritesScroll;
    private Vector2 propertySearchScroll;
    private Vector2 materialSearchScroll;
    private Vector2 componentPickerScroll;
    private bool savedSetupsLoaded;
    private bool passiveCaptureSubscribed;
    private bool addingFromPassiveCapture;
    private string passiveCaptureStatus = "Off";
    private readonly Dictionary<string, PassiveFloatValue> passiveValueSnapshot = new Dictionary<string, PassiveFloatValue>();
    private double nextPassiveSnapshotTime;
    private List<ComponentSearchOption> cachedUnityComponentOptions;
    private List<ComponentSearchOption> cachedUnityCommonComponentOptions;
    private List<ComponentSearchOption> cachedVrchatComponentOptions;
    private List<ComponentSearchOption> cachedModularAvatarComponentOptions;
    private List<ComponentSearchOption> cachedVrcFuryComponentOptions;

    private void FeatureOnEnable()
    {
        LoadSavedSetups();
        TryAssignActiveAvatarRoot(false);
        SetPassiveCaptureSubscription(listenForAvatarChanges);
        ResetPassiveCaptureSnapshot();
    }

    private void OnDisable()
    {
        SetPassiveCaptureSubscription(false);
        SaveSavedSetups();
    }

    private void OnFocus()
    {
        TryAssignActiveAvatarRoot(false);
    }

    private void OnSelectionChange()
    {
        TryAssignActiveAvatarRoot(false);
        ResetPassiveCaptureSnapshot();
        Repaint();
    }

    private void DrawFavorites()
    {
        favoritesFoldout = BeginFoldoutPanel(favoritesFoldout, "Saved Favorites");
        if (!favoritesFoldout)
            return;

        LoadSavedSetups();

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        newFavoriteName = EditorGUILayout.TextField(new GUIContent("Setup Name", "Saved favorites remember the selected objects, properties, values, and clip settings."), newFavoriteName);
        using (new EditorGUI.DisabledScope(propertySets.Count == 0))
        {
            if (GUILayout.Button("Save Setup", GUILayout.Width(100f)))
                SaveCurrentSetupAsFavorite();
        }
        EditorGUILayout.EndHorizontal();

        if (savedSetups.Count == 0)
        {
            EditorGUILayout.LabelField("No saved setups yet. Build a property setup, name it, then save it here.", miniMutedStyle);
            EditorGUILayout.EndVertical();
            return;
        }

        favoritesScroll = EditorGUILayout.BeginScrollView(favoritesScroll, GUILayout.Height(Mathf.Min(180f, 34f + savedSetups.Count * 32f)));
        for (int i = 0; i < savedSetups.Count; i++)
        {
            SavedSetupData setup = savedSetups[i];
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(setup.Name + " (" + CountSavedSpecs(setup) + ")", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Load", GUILayout.Width(54f)))
                LoadFavoriteSetup(setup);

            if (GUILayout.Button("X", GUILayout.Width(24f)))
            {
                savedSetups.RemoveAt(i);
                SaveSavedSetups();
                i--;
            }

            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.LabelField("Favorites load onto the current avatar root, so the same setup can be reused on matching avatars.", miniMutedStyle);
        EditorGUILayout.EndVertical();
    }

    private void DrawCopyApplyTargetSettings()
    {
        copyApplyTargetMode = (CopyApplyTargetMode)EditorGUILayout.EnumPopup(new GUIContent("Target Source", "Manual Clip uses the field below. Animation Window follows the clip selected in an open Animation window."), copyApplyTargetMode);

        if (copyApplyTargetMode != CopyApplyTargetMode.AnimationWindow)
            return;

        List<AnimationWindowInfo> windows = GetAnimationWindowInfos();
        if (windows.Count == 0)
        {
            EditorGUILayout.HelpBox("No Animation window is open. Open Unity's Animation window or switch Target Source to Manual Clip.", MessageType.Info);
            return;
        }

        if (windows.Count == 1)
        {
            selectedAnimationWindowIndex = 0;
        }
        else
        {
            selectedAnimationWindowIndex = Mathf.Clamp(selectedAnimationWindowIndex, 0, windows.Count - 1);
            selectedAnimationWindowIndex = EditorGUILayout.Popup(new GUIContent("Animation Window", "Pick which open Animation window this tool follows."), selectedAnimationWindowIndex, windows.Select(window => window.Label).ToArray());
        }

        AnimationWindowInfo selected = windows[Mathf.Clamp(selectedAnimationWindowIndex, 0, windows.Count - 1)];
        pasteTargetClip = selected.Clip;
        useAnimationWindowFrame = EditorGUILayout.Toggle(new GUIContent("Use Window Frame", "Apply keys at the frame currently shown in the selected Animation window when available."), useAnimationWindowFrame);

        string clipNameLabel = selected.Clip != null ? selected.Clip.name : "None";
        string frameLabel = selected.Frame.HasValue ? selected.Frame.Value.ToString() : "manual";
        EditorGUILayout.LabelField("Following: " + clipNameLabel + "    Frame: " + frameLabel, miniMutedStyle);
    }

    private void DrawComponentSearchDropdowns()
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Component Picker", EditorStyles.miniBoldLabel);

        EditorGUILayout.BeginHorizontal();
        string[] filterLabels = { "All", "Unity", "Unity Common", "VRChat", "Modular Avatar", "VRCFury" };
        componentSearchFilter = (ComponentSearchFilter)EditorGUILayout.Popup(new GUIContent("Filter"), (int)componentSearchFilter, filterLabels);
        componentPickerSearch = EditorGUILayout.TextField(new GUIContent("Search"), componentPickerSearch);
        if (GUILayout.Button("Clear", GUILayout.Width(48f)))
            componentPickerSearch = "";
        EditorGUILayout.EndHorizontal();

        List<ComponentSearchOption> matches = BuildFilteredComponentPickerResults();
        if (matches.Count == 0)
        {
            EditorGUILayout.LabelField("No matching components found in the current object search scope.", miniMutedStyle);
            return;
        }

        int shown = 0;
        componentPickerScroll = EditorGUILayout.BeginScrollView(componentPickerScroll, GUILayout.Height(180f));
        foreach (ComponentSearchOption option in matches)
        {
            if (shown >= MaxSearchResults)
                break;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(GetComponentOptionDisplayLabel(option), EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Add Objects", GUILayout.Width(92f)))
                AddObjectsWithComponentOption(option);
            EditorGUILayout.EndHorizontal();
            shown++;
        }
        EditorGUILayout.EndScrollView();

        if (matches.Count > MaxSearchResults)
            EditorGUILayout.LabelField("Showing first " + MaxSearchResults + " of " + matches.Count + " matches. Type more to narrow it down.", miniMutedStyle);

        EditorGUILayout.LabelField("Component picker only shows component types found under the current object search scope.", miniMutedStyle);
    }

    private void DrawMaterialPropertySearch()
    {
        materialSearchFoldout = BeginFoldoutPanel(materialSearchFoldout, "Material Property Search");
        if (!materialSearchFoldout)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        materialPropertySearch = EditorGUILayout.TextField(new GUIContent("Search", "Search only material properties on Renderer components for the current object list."), materialPropertySearch);

        if (string.IsNullOrWhiteSpace(materialPropertySearch))
        {
            EditorGUILayout.LabelField("No material properties are added automatically. Type a property name like color, emission, cutoff, metallic, smoothness, or the shader property name.", miniMutedStyle);
            EditorGUILayout.EndVertical();
            return;
        }

        List<BindingSearchResult> results = BuildMaterialPropertySearchResults(materialPropertySearch);
        if (results.Count == 0)
        {
            EditorGUILayout.LabelField("No matching material properties found on the current objects.", miniMutedStyle);
            EditorGUILayout.EndVertical();
            return;
        }

        materialSearchScroll = EditorGUILayout.BeginScrollView(materialSearchScroll, GUILayout.Height(180f));
        int shown = 0;
        foreach (BindingSearchResult result in results)
        {
            if (shown >= MaxMaterialSearchResults)
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

        if (results.Count > MaxMaterialSearchResults)
            EditorGUILayout.LabelField("Showing first " + MaxMaterialSearchResults + " of " + results.Count + " matches. Narrow the search for more.", miniMutedStyle);

        EditorGUILayout.EndVertical();
    }

    private void DrawPassiveCapture()
    {
        passiveCaptureFoldout = BeginFoldoutPanel(passiveCaptureFoldout, "Passive Capture");
        if (!passiveCaptureFoldout)
            return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUI.BeginChangeCheck();
        listenForAvatarChanges = EditorGUILayout.Toggle(new GUIContent("Listen For Avatar Changes", "Adds changed animatable properties to this setup without entering Unity's animation record mode."), listenForAvatarChanges);
        if (EditorGUI.EndChangeCheck())
            SetPassiveCaptureSubscription(listenForAvatarChanges);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Use Active Avatar Root"))
            TryAssignActiveAvatarRoot(true);

        if (GUILayout.Button("Clear Captured Setup"))
        {
            propertySets.Clear();
            targets.Clear();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Status: " + passiveCaptureStatus, miniMutedStyle);
        EditorGUILayout.LabelField("This listens for Inspector/serialized changes under the avatar root and adds matching float curves. It does not play or record an animation.", miniMutedStyle);
        EditorGUILayout.EndVertical();
    }

    private void DrawSpecValueControls(BindingSpec spec)
    {
        if (spec == null || spec.Target == null)
            return;

        GameObject root = ResolveRootForTarget(spec.Target);
        if (root == null)
            return;

        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(32f);

        if (spec.IsObjectReference)
        {
            UnityEngine.Object sceneObject;
            bool hasSceneObject = TryReadCurrentObjectReferenceValue(spec, out sceneObject);
            GUILayout.Label("Material", GUILayout.Width(52f));
            spec.CustomObjectReferenceValue = EditorGUILayout.ObjectField(spec.CustomObjectReferenceValue, typeof(Material), false, GUILayout.Width(130f));

            using (new EditorGUI.DisabledScope(!hasSceneObject))
            {
                if (GUILayout.Button("Grab", GUILayout.Width(46f)))
                    spec.CustomObjectReferenceValue = sceneObject;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(sceneObject, typeof(Material), false, GUILayout.Width(100f));
            }

            EditorGUILayout.EndHorizontal();
            return;
        }

        EditorCurveBinding binding = MakeRuntimeBinding(spec, root);
        float sceneValue;
        bool hasSceneValue = TryReadCurrentSceneValue(spec, root, binding, out sceneValue);

        spec.UseCustomValue = true;
        GUILayout.Label("Value", GUILayout.Width(42f));
        if (spec.IsToggleLike)
        {
            bool toggle = EditorGUILayout.Toggle(spec.CustomValue > 0.5f, GUILayout.Width(36f));
            spec.CustomValue = toggle ? 1f : 0f;
        }
        else
        {
            spec.CustomValue = EditorGUILayout.FloatField(spec.CustomValue, GUILayout.Width(92f));
        }

        using (new EditorGUI.DisabledScope(!hasSceneValue))
        {
            if (GUILayout.Button("Grab", GUILayout.Width(46f)))
            {
                spec.CustomValue = sceneValue;
                spec.UseCustomValue = true;
            }
        }

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.TextField(hasSceneValue ? FormatFloat(sceneValue) : "?", GUILayout.Width(72f));
        }

        EditorGUILayout.EndHorizontal();
    }

    private void InitializeSpecCustomValue(BindingSpec spec)
    {
        if (spec == null || spec.Target == null)
            return;

        spec.UseCustomValue = true;

        GameObject root = ResolveRootForTarget(spec.Target);
        if (root == null)
            return;

        float value;
        if (TryReadCurrentSceneValue(spec, root, MakeRuntimeBinding(spec, root), out value))
            spec.CustomValue = value;
    }

    private AnimationClip GetCurrentPasteTargetClip()
    {
        if (copyApplyTargetMode != CopyApplyTargetMode.AnimationWindow)
            return pasteTargetClip;

        AnimationWindowInfo info = GetSelectedAnimationWindowInfo();
        if (info != null && info.Clip != null)
        {
            pasteTargetClip = info.Clip;
            return info.Clip;
        }

        return pasteTargetClip;
    }

    private int GetCurrentPasteFrame(AnimationClip clip)
    {
        if (copyApplyTargetMode == CopyApplyTargetMode.AnimationWindow && useAnimationWindowFrame)
        {
            AnimationWindowInfo info = GetSelectedAnimationWindowInfo();
            if (info != null)
            {
                if (info.Frame.HasValue)
                    return Mathf.Max(0, info.Frame.Value);

                if (info.Time.HasValue && clip != null)
                    return Mathf.Max(0, Mathf.RoundToInt(info.Time.Value * Mathf.Max(1f, clip.frameRate)));
            }
        }

        return Mathf.Max(0, pasteFrame);
    }

    private bool TryReadCurrentSceneValue(BindingSpec spec, GameObject root, EditorCurveBinding binding, out float value)
    {
        value = 0f;
        switch (spec.ManualKind)
        {
            case ManualValueKind.GameObjectActive:
                value = spec.Target != null && spec.Target.activeSelf ? 1f : 0f;
                return true;
            case ManualValueKind.ComponentEnabled:
                return TryGetComponentEnabled(spec.SourceComponent, out value);
            case ManualValueKind.BlendShape:
                SkinnedMeshRenderer renderer = spec.SourceComponent as SkinnedMeshRenderer;
                if (renderer != null && spec.ManualIndex >= 0 && renderer.sharedMesh != null && spec.ManualIndex < renderer.sharedMesh.blendShapeCount)
                {
                    value = renderer.GetBlendShapeWeight(spec.ManualIndex);
                    return true;
                }
                return false;
        }

        try
        {
            return AnimationUtility.GetFloatValue(root, binding, out value);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SaveCurrentSetupAsFavorite()
    {
        string setupName = string.IsNullOrWhiteSpace(newFavoriteName) ? "New Favorite" : newFavoriteName.Trim();
        SavedSetupData setup = BuildSavedSetup(setupName);
        if (setup.PropertySets.Count == 0)
        {
            EditorUtility.DisplayDialog("Nothing To Save", "Add objects and properties before saving a favorite setup.", "OK");
            return;
        }

        int existing = savedSetups.FindIndex(item => string.Equals(item.Name, setupName, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            savedSetups[existing] = setup;
        else
            savedSetups.Add(setup);

        savedSetups.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        SaveSavedSetups();
        ShowNotification(new GUIContent("Saved favorite setup"));
    }

    private SavedSetupData BuildSavedSetup(string setupName)
    {
        SavedSetupData setup = new SavedSetupData
        {
            Name = setupName,
            FrameRate = frameRate,
            DurationFrames = durationFrames,
            CreateKeyMode = (int)createKeyMode,
            InvertFirstToggleKey = invertFirstToggleKey,
            LoopClip = loopClip
        };

        foreach (PropertySet set in propertySets)
        {
            SavedPropertySetData savedSet = new SavedPropertySetData { Label = set.Label };
            foreach (BindingSpec spec in set.Specs)
            {
                if (spec == null || spec.Target == null || spec.BindingType == null || string.IsNullOrEmpty(spec.PropertyName))
                    continue;

                savedSet.Specs.Add(new SavedBindingData
                {
                    TargetPath = GetSetupTargetPath(spec.Target),
                    BindingTypeName = spec.BindingType.AssemblyQualifiedName,
                    PropertyName = spec.PropertyName,
                    Label = spec.Label,
                    ManualKind = (int)spec.ManualKind,
                    ManualIndex = spec.ManualIndex,
                    UseCustomValue = spec.UseCustomValue,
                    CustomValue = spec.CustomValue,
                    IsObjectReference = spec.IsObjectReference,
                    ObjectReferenceAssetPath = spec.CustomObjectReferenceValue != null ? AssetDatabase.GetAssetPath(spec.CustomObjectReferenceValue) : "",
                    ObjectReferenceIndex = spec.ObjectReferenceIndex
                });
            }

            if (savedSet.Specs.Count > 0)
                setup.PropertySets.Add(savedSet);
        }

        return setup;
    }

    private void LoadFavoriteSetup(SavedSetupData setup)
    {
        if (setup == null)
            return;

        TryAssignActiveAvatarRoot(false);
        GameObject root = GetEffectiveRoot();
        if (root == null)
        {
            EditorUtility.DisplayDialog("No Avatar Root", "Set Animation Root first, or select an avatar with a VRC Avatar Descriptor.", "OK");
            return;
        }

        frameRate = setup.FrameRate > 0f ? setup.FrameRate : frameRate;
        durationFrames = Mathf.Max(1, setup.DurationFrames);
        createKeyMode = (CreateKeyMode)Mathf.Clamp(setup.CreateKeyMode, 0, 1);
        invertFirstToggleKey = setup.InvertFirstToggleKey;
        loopClip = setup.LoopClip;
        activeQuickAddMode = QuickAddMode.None;

        targets.Clear();
        propertySets.Clear();
        int skipped = 0;

        foreach (SavedPropertySetData savedSet in setup.PropertySets)
        {
            PropertySet set = new PropertySet
            {
                Label = savedSet.Label,
                Expanded = false
            };

            foreach (SavedBindingData savedSpec in savedSet.Specs)
            {
                GameObject target = FindChildByAnimationPath(root, savedSpec.TargetPath);
                if (target == null)
                {
                    skipped++;
                    continue;
                }

                Type bindingType = FindTypeByName(savedSpec.BindingTypeName);
                if (bindingType == null)
                {
                    skipped++;
                    continue;
                }

                Component source = FindComponentForBindingType(target, bindingType);
                BindingSpec spec = new BindingSpec
                {
                    Target = target,
                    SourceComponent = source,
                    BindingType = bindingType,
                    PropertyName = savedSpec.PropertyName,
                    Label = savedSpec.Label,
                    ManualKind = (ManualValueKind)Mathf.Clamp(savedSpec.ManualKind, 0, 3),
                    ManualIndex = savedSpec.ManualIndex,
                    UseCustomValue = true,
                    CustomValue = savedSpec.CustomValue,
                    IsObjectReference = savedSpec.IsObjectReference,
                    CustomObjectReferenceValue = !string.IsNullOrEmpty(savedSpec.ObjectReferenceAssetPath) ? AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(savedSpec.ObjectReferenceAssetPath) : null,
                    ObjectReferenceIndex = savedSpec.ObjectReferenceIndex
                };

                set.Specs.Add(spec);
                AddTarget(target);
            }

            if (set.Specs.Count > 0)
            {
                set.Target = set.Specs[0].Target;
                set.SourceComponent = set.Specs[0].SourceComponent;
                propertySets.Add(set);
            }
        }

        ShowNotification(new GUIContent(skipped > 0 ? "Loaded with " + skipped + " missing" : "Loaded favorite setup"));
    }

    private void LoadSavedSetups()
    {
        if (savedSetupsLoaded)
            return;

        savedSetupsLoaded = true;
        savedSetups.Clear();

        string json = EditorPrefs.GetString(FavoritesPrefsKey, "");
        if (string.IsNullOrEmpty(json))
            return;

        try
        {
            SavedSetupLibrary library = JsonUtility.FromJson<SavedSetupLibrary>(json);
            if (library != null && library.Setups != null)
                savedSetups.AddRange(library.Setups.Where(setup => setup != null && !string.IsNullOrEmpty(setup.Name)));
        }
        catch (Exception)
        {
            savedSetups.Clear();
        }
    }

    private void SaveSavedSetups()
    {
        SavedSetupLibrary library = new SavedSetupLibrary { Setups = savedSetups };
        EditorPrefs.SetString(FavoritesPrefsKey, JsonUtility.ToJson(library));
    }

    private int CountSavedSpecs(SavedSetupData setup)
    {
        if (setup == null || setup.PropertySets == null)
            return 0;

        return setup.PropertySets.Sum(set => set != null && set.Specs != null ? set.Specs.Count : 0);
    }

    private string GetSetupTargetPath(GameObject target)
    {
        GameObject root = GetEffectiveRoot();
        if (target == null)
            return "";

        if (root != null && target == root)
            return "";

        if (root != null && IsChildOf(target.transform, root.transform))
            return AnimationUtility.CalculateTransformPath(target.transform, root.transform);

        return GetFullHierarchyPath(target.transform);
    }

    private static string GetFullHierarchyPath(Transform transform)
    {
        if (transform == null)
            return "";

        List<string> parts = new List<string>();
        Transform current = transform;
        while (current != null)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts.ToArray());
    }

    private static GameObject FindChildByAnimationPath(GameObject root, string path)
    {
        if (root == null)
            return null;

        if (string.IsNullOrEmpty(path))
            return root;

        Transform current = root.transform;
        string[] parts = path.Split('/');
        foreach (string part in parts)
        {
            if (string.IsNullOrEmpty(part))
                continue;

            current = FindDirectChild(current, part);
            if (current == null)
                return null;
        }

        return current.gameObject;
    }

    private static Transform FindDirectChild(Transform parent, string childName)
    {
        if (parent == null)
            return null;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == childName)
                return child;
        }

        return null;
    }

    private List<BindingSearchResult> BuildMaterialPropertySearchResults(string query)
    {
        List<BindingSearchResult> results = new List<BindingSearchResult>();
        string trimmed = query.Trim();
        HashSet<string> seen = new HashSet<string>();

        foreach (GameObject target in ValidTargets())
        {
            Renderer renderer = target.GetComponent<Renderer>();
            if (renderer == null)
                continue;

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
                if (binding.isPPtrCurve || binding.propertyName.IndexOf("material.", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string humanProperty = HumanizePropertyName(binding.propertyName);
                string label = GetTargetLabel(target) + " / Material / " + humanProperty;
                string haystack = label + " " + binding.propertyName;
                if (haystack.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string key = GetSpecKey(target, binding.type, binding.propertyName);
                if (!seen.Add(key))
                    continue;

                results.Add(new BindingSearchResult
                {
                    Target = target,
                    SourceComponent = renderer,
                    Binding = binding,
                    Label = label
                });
            }
        }

        results.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    private List<ComponentSearchOption> BuildFilteredComponentPickerResults()
    {
        List<ComponentSearchOption> source;
        switch (componentSearchFilter)
        {
            case ComponentSearchFilter.All:
                source = GetAllScopedComponentOptions();
                break;
            case ComponentSearchFilter.Unity:
                source = GetUnityComponentOptions();
                break;
            case ComponentSearchFilter.UnityCommon:
                source = GetUnityCommonComponentOptions();
                break;
            case ComponentSearchFilter.VRChat:
                source = GetVrchatComponentOptions();
                break;
            case ComponentSearchFilter.ModularAvatar:
                source = GetModularAvatarComponentOptions();
                break;
            case ComponentSearchFilter.VRCFury:
                source = GetVrcFuryComponentOptions();
                break;
            default:
                source = GetAllScopedComponentOptions();
                break;
        }

        Dictionary<string, int> componentCounts = BuildScopedComponentCounts();
        List<ComponentSearchOption> scopedSource = new List<ComponentSearchOption>();
        foreach (ComponentSearchOption option in source)
        {
            ResolveFallbackType(option);
            int count = GetScopedComponentCount(option, componentCounts);
            if (count <= 0)
                continue;

            scopedSource.Add(new ComponentSearchOption
            {
                Label = option.Label,
                Type = option.Type,
                TypeNames = option.TypeNames,
                InstanceCount = count
            });
        }

        string query = componentPickerSearch != null ? componentPickerSearch.Trim() : "";
        IEnumerable<ComponentSearchOption> matches = scopedSource;
        if (!string.IsNullOrEmpty(query))
        {
            matches = matches.Where(option => ComponentOptionMatches(option, query));
        }

        return matches.OrderByDescending(option => option.InstanceCount).ThenBy(option => option.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<ComponentSearchOption> GetAllScopedComponentOptions()
    {
        Dictionary<string, int> counts = BuildScopedComponentCounts();
        List<ComponentSearchOption> options = new List<ComponentSearchOption>();
        HashSet<string> seen = new HashSet<string>();

        foreach (Transform root in GetSearchRoots())
        {
            Component[] components = root.GetComponentsInChildren<Component>(includeInactiveSearch);
            foreach (Component component in components)
            {
                if (component == null)
                    continue;

                Type type = component.GetType();
                string fullName = type.FullName ?? type.Name;
                if (!seen.Add(fullName))
                    continue;

                options.Add(new ComponentSearchOption
                {
                    Label = type.Name,
                    Type = type,
                    TypeNames = new[] { fullName, type.Name },
                    InstanceCount = GetScopedComponentCount(new ComponentSearchOption { Type = type, TypeNames = new[] { fullName, type.Name } }, counts)
                });
            }
        }

        return options;
    }

    private List<ComponentSearchOption> GetUnityComponentOptions()
    {
        if (cachedUnityComponentOptions == null)
            cachedUnityComponentOptions = BuildComponentOptions(type => type.Namespace != null && type.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal), null);

        return cachedUnityComponentOptions;
    }

    private List<ComponentSearchOption> GetUnityCommonComponentOptions()
    {
        if (cachedUnityCommonComponentOptions == null)
        {
            cachedUnityCommonComponentOptions = BuildComponentOptions(type => false, new[]
            {
                MakeTypeOption<Animator>("Animator"),
                MakeTypeOption<Animation>("Animation"),
                MakeTypeOption<AudioSource>("Audio Source"),
                MakeTypeOption<BoxCollider>("Box Collider"),
                MakeTypeOption<Camera>("Camera"),
                MakeTypeOption<Canvas>("Canvas"),
                MakeTypeOption<CapsuleCollider>("Capsule Collider"),
                MakeTypeOption<Collider>("Any Collider"),
                MakeTypeOption<Light>("Light"),
                MakeTypeOption<MeshFilter>("Mesh Filter"),
                MakeTypeOption<MeshRenderer>("Mesh Renderer"),
                MakeTypeOption<ParticleSystem>("Particle System"),
                MakeTypeOption<RectTransform>("Rect Transform"),
                MakeTypeOption<Renderer>("Any Renderer"),
                MakeTypeOption<Rigidbody>("Rigidbody"),
                MakeTypeOption<SkinnedMeshRenderer>("Skinned Mesh Renderer"),
                MakeTypeOption<SphereCollider>("Sphere Collider"),
                MakeTypeOption<TrailRenderer>("Trail Renderer"),
                MakeTypeOption<Transform>("Transform")
            });
        }

        return cachedUnityCommonComponentOptions;
    }

    private List<ComponentSearchOption> GetVrchatComponentOptions()
    {
        if (cachedVrchatComponentOptions == null)
        {
            cachedVrchatComponentOptions = BuildComponentOptions(IsVrchatComponentType, new[]
            {
                MakeFallbackOption("VRC Avatar Descriptor", "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"),
                MakeFallbackOption("VRC Contact Receiver", "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver"),
                MakeFallbackOption("VRC Contact Sender", "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender"),
                MakeFallbackOption("VRC PhysBone", "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone"),
                MakeFallbackOption("VRC PhysBone Collider", "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider")
            });
        }

        return cachedVrchatComponentOptions;
    }

    private List<ComponentSearchOption> GetModularAvatarComponentOptions()
    {
        if (cachedModularAvatarComponentOptions == null)
        {
            cachedModularAvatarComponentOptions = BuildComponentOptions(IsModularAvatarComponentType, new[]
            {
                MakeFallbackOption("MA Merge Animator", "nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator"),
                MakeFallbackOption("MA Menu Item", "nadena.dev.modular_avatar.core.ModularAvatarMenuItem"),
                MakeFallbackOption("MA Parameters", "nadena.dev.modular_avatar.core.ModularAvatarParameters"),
                MakeFallbackOption("MA Object Toggle", "nadena.dev.modular_avatar.core.ModularAvatarObjectToggle")
            });
        }

        return cachedModularAvatarComponentOptions;
    }

    private List<ComponentSearchOption> GetVrcFuryComponentOptions()
    {
        if (cachedVrcFuryComponentOptions == null)
        {
            cachedVrcFuryComponentOptions = BuildComponentOptions(IsVrcFuryComponentType, new[]
            {
                MakeFallbackOption("VRCFury", "VF.Model.VRCFury"),
                MakeFallbackOption("VRCFury Full Controller", "VF.Model.Feature.FullController"),
                MakeFallbackOption("VRCFury Toggle", "VF.Model.Feature.Toggle")
            });
        }

        return cachedVrcFuryComponentOptions;
    }

    private List<ComponentSearchOption> BuildComponentOptions(Func<Type, bool> filter, IEnumerable<ComponentSearchOption> fallbacks)
    {
        List<ComponentSearchOption> options = new List<ComponentSearchOption>();

        HashSet<string> seen = new HashSet<string>();
        if (fallbacks != null)
        {
            foreach (ComponentSearchOption fallback in fallbacks)
            {
                ResolveFallbackType(fallback);
                if (seen.Add(fallback.Label))
                    options.Add(fallback);

                if (fallback.Type != null)
                {
                    seen.Add(fallback.Type.Name);
                    if (!string.IsNullOrEmpty(fallback.Type.FullName))
                        seen.Add(fallback.Type.FullName);
                }

                if (fallback.TypeNames != null)
                {
                    foreach (string typeName in fallback.TypeNames)
                    {
                        if (!string.IsNullOrEmpty(typeName))
                            seen.Add(typeName);
                    }
                }
            }
        }

        List<ComponentSearchOption> dynamicOptions = new List<ComponentSearchOption>();
        foreach (Type type in GetAllLoadedComponentTypes())
        {
            if (!filter(type))
                continue;

            string fullName = type.FullName ?? type.Name;
            if (!seen.Add(fullName))
                continue;

            dynamicOptions.Add(new ComponentSearchOption
            {
                Label = type.Name,
                Type = type,
                TypeNames = new[] { fullName, type.Name }
            });
        }

        dynamicOptions.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        options.AddRange(dynamicOptions);
        return options;
    }

    private ComponentSearchOption MakeTypeOption<T>(string label) where T : Component
    {
        Type type = typeof(T);
        return new ComponentSearchOption
        {
            Label = label,
            Type = type,
            TypeNames = new[] { type.FullName, type.Name }
        };
    }

    private ComponentSearchOption MakeFallbackOption(string label, params string[] typeNames)
    {
        return new ComponentSearchOption
        {
            Label = label,
            TypeNames = typeNames
        };
    }

    private bool ComponentOptionMatches(ComponentSearchOption option, string query)
    {
        if (option == null)
            return false;

        if (!string.IsNullOrEmpty(option.Label) && option.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (option.Type != null)
        {
            string typeName = option.Type.Name;
            string fullName = option.Type.FullName ?? "";
            if (typeName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || fullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        if (option.TypeNames != null)
        {
            foreach (string typeName in option.TypeNames)
            {
                if (!string.IsNullOrEmpty(typeName) && typeName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }

        return false;
    }

    private Dictionary<string, int> BuildScopedComponentCounts()
    {
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Transform root in GetSearchRoots())
        {
            Component[] components = root.GetComponentsInChildren<Component>(includeInactiveSearch);
            foreach (Component component in components)
            {
                if (component == null)
                    continue;

                Type type = component.GetType();
                AddComponentCount(counts, type.Name);
                if (!string.IsNullOrEmpty(type.FullName))
                    AddComponentCount(counts, type.FullName);
            }
        }

        return counts;
    }

    private static void AddComponentCount(Dictionary<string, int> counts, string key)
    {
        if (string.IsNullOrEmpty(key))
            return;

        int count;
        counts.TryGetValue(key, out count);
        counts[key] = count + 1;
    }

    private int GetScopedComponentCount(ComponentSearchOption option, Dictionary<string, int> counts)
    {
        if (option == null || counts == null)
            return 0;

        ResolveFallbackType(option);
        int count;
        if (option.Type != null)
        {
            if (!string.IsNullOrEmpty(option.Type.FullName) && counts.TryGetValue(option.Type.FullName, out count))
                return count;

            if (counts.TryGetValue(option.Type.Name, out count))
                return count;
        }

        if (option.TypeNames != null)
        {
            foreach (string typeName in option.TypeNames)
            {
                if (!string.IsNullOrEmpty(typeName) && counts.TryGetValue(typeName, out count))
                    return count;
            }
        }

        return 0;
    }
    private string GetComponentOptionDisplayLabel(ComponentSearchOption option)
    {
        if (option == null)
            return "Missing";

        if (option.Type != null && !string.IsNullOrEmpty(option.Type.Namespace))
            return option.Label + " (" + option.InstanceCount + ")    " + option.Type.Namespace;

        return option.Label + " (" + option.InstanceCount + ")";
    }

    private void ResolveFallbackType(ComponentSearchOption option)
    {
        if (option == null || option.Type != null || option.TypeNames == null)
            return;

        foreach (string typeName in option.TypeNames)
        {
            Type type = FindTypeByName(typeName);
            if (type != null && typeof(Component).IsAssignableFrom(type))
            {
                option.Type = type;
                return;
            }
        }
    }

    private void AddObjectsWithComponentOption(ComponentSearchOption option)
    {
        if (option == null)
            return;

        ResolveFallbackType(option);
        if (option.Type != null)
        {
            AddObjectsWithComponent(option.Type);
            return;
        }

        AddObjectsWithComponentTypeNames(option.TypeNames);
    }

    private void AddObjectsWithComponentTypeNames(string[] typeNames)
    {
        if (typeNames == null || typeNames.Length == 0)
            return;

        HashSet<string> names = new HashSet<string>(typeNames.Where(name => !string.IsNullOrEmpty(name)), StringComparer.OrdinalIgnoreCase);
        foreach (Transform root in GetSearchRoots())
        {
            Component[] components = root.GetComponentsInChildren<Component>(includeInactiveSearch);
            Array.Sort(components, CompareComponentHierarchyOrder);
            foreach (Component component in components)
            {
                if (component == null)
                    continue;

                Type type = component.GetType();
                if (names.Contains(type.FullName) || names.Contains(type.Name))
                    AddTarget(component.gameObject);
            }
        }
    }

    private static IEnumerable<Type> GetAllLoadedComponentTypes()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(type => type != null).ToArray();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (Type type in types)
            {
                if (type == null || type.IsAbstract || type.IsGenericTypeDefinition)
                    continue;

                if (typeof(Component).IsAssignableFrom(type))
                    yield return type;
            }
        }
    }

    private static bool IsVrchatComponentType(Type type)
    {
        string fullName = type.FullName ?? type.Name;
        string assemblyName = type.Assembly.GetName().Name;
        return fullName.StartsWith("VRC.", StringComparison.Ordinal)
            || fullName.StartsWith("VRCSDK", StringComparison.Ordinal)
            || fullName.IndexOf("VRC.SDK", StringComparison.OrdinalIgnoreCase) >= 0
            || assemblyName.IndexOf("VRChat", StringComparison.OrdinalIgnoreCase) >= 0
            || assemblyName.IndexOf("VRCSDK", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsModularAvatarComponentType(Type type)
    {
        string fullName = type.FullName ?? type.Name;
        string assemblyName = type.Assembly.GetName().Name;
        return fullName.IndexOf("modular_avatar", StringComparison.OrdinalIgnoreCase) >= 0
            || fullName.IndexOf("ModularAvatar", StringComparison.OrdinalIgnoreCase) >= 0
            || assemblyName.IndexOf("ModularAvatar", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsVrcFuryComponentType(Type type)
    {
        string fullName = type.FullName ?? type.Name;
        string assemblyName = type.Assembly.GetName().Name;
        return fullName.IndexOf("VRCFury", StringComparison.OrdinalIgnoreCase) >= 0
            || fullName.StartsWith("VF.", StringComparison.Ordinal)
            || assemblyName.IndexOf("VRCFury", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static Type FindTypeByName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return null;

        Type direct = Type.GetType(typeName);
        if (direct != null)
            return direct;

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(typeName);
            if (type != null)
                return type;

            try
            {
                type = assembly.GetTypes().FirstOrDefault(candidate => candidate != null && (candidate.FullName == typeName || candidate.Name == typeName));
                if (type != null)
                    return type;
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    private void SetPassiveCaptureSubscription(bool enabled)
    {
        if (enabled && !passiveCaptureSubscribed)
        {
            Undo.postprocessModifications += OnUndoPostprocessModifications;
            EditorApplication.update += TickPassiveCapture;
            passiveCaptureSubscribed = true;
            passiveCaptureStatus = "Listening";
            ResetPassiveCaptureSnapshot();
        }
        else if (!enabled && passiveCaptureSubscribed)
        {
            Undo.postprocessModifications -= OnUndoPostprocessModifications;
            EditorApplication.update -= TickPassiveCapture;
            passiveCaptureSubscribed = false;
            passiveCaptureStatus = "Off";
            passiveValueSnapshot.Clear();
        }
    }

    private void TickPassiveCapture()
    {
        if (!listenForAvatarChanges || addingFromPassiveCapture)
            return;

        double now = EditorApplication.timeSinceStartup;
        if (now < nextPassiveSnapshotTime)
            return;

        nextPassiveSnapshotTime = now + 0.15d;
        Dictionary<string, PassiveFloatValue> currentValues = GatherPassiveCaptureValues();

        if (passiveValueSnapshot.Count == 0)
        {
            ReplacePassiveSnapshot(currentValues);
            return;
        }

        int addedOrUpdated = 0;
        foreach (KeyValuePair<string, PassiveFloatValue> pair in currentValues)
        {
            PassiveFloatValue previous;
            if (!passiveValueSnapshot.TryGetValue(pair.Key, out previous))
                continue;

            if (Mathf.Abs(previous.Value - pair.Value.Value) < 0.0001f)
                continue;

            if (AddOrUpdatePassiveValue(pair.Value))
                addedOrUpdated++;
        }

        ReplacePassiveSnapshot(currentValues);
        if (addedOrUpdated > 0)
        {
            passiveCaptureStatus = "Recorded " + addedOrUpdated + " changed propert" + (addedOrUpdated == 1 ? "y" : "ies");
            Repaint();
        }
    }

    private void ResetPassiveCaptureSnapshot()
    {
        passiveValueSnapshot.Clear();
        if (listenForAvatarChanges)
            ReplacePassiveSnapshot(GatherPassiveCaptureValues());
    }

    private void ReplacePassiveSnapshot(Dictionary<string, PassiveFloatValue> values)
    {
        passiveValueSnapshot.Clear();
        foreach (KeyValuePair<string, PassiveFloatValue> pair in values)
            passiveValueSnapshot[pair.Key] = pair.Value;
    }

    private Dictionary<string, PassiveFloatValue> GatherPassiveCaptureValues()
    {
        Dictionary<string, PassiveFloatValue> values = new Dictionary<string, PassiveFloatValue>();
        List<GameObject> selected = Selection.gameObjects.Where(go => go != null).Distinct().ToList();
        selected.Sort(CompareHierarchyOrder);

        if (selected.Count == 0)
            selected = ValidTargets();

        foreach (GameObject target in selected)
            AddPassiveValuesForTarget(target, values);

        return values;
    }

    private void AddPassiveValuesForTarget(GameObject target, Dictionary<string, PassiveFloatValue> values)
    {
        if (target == null)
            return;

        GameObject root = GetEffectiveRoot();
        if (root != null && !IsChildOf(target.transform, root.transform))
            return;

        root = ResolveRootForTarget(target);
        if (root == null)
            return;

        EditorCurveBinding activeBinding = EditorCurveBinding.FloatCurve(GetPathForTarget(target), typeof(GameObject), "m_IsActive");
        AddPassiveValue(values, target, null, activeBinding, ManualValueKind.GameObjectActive, -1, target.activeSelf ? 1f : 0f);

        EditorCurveBinding[] bindings;
        try
        {
            bindings = AnimationUtility.GetAnimatableBindings(target, root);
        }
        catch (Exception)
        {
            return;
        }

        foreach (EditorCurveBinding binding in bindings)
        {
            if (binding.isPPtrCurve)
                continue;

            float value;
            try
            {
                if (!AnimationUtility.GetFloatValue(root, binding, out value))
                    continue;
            }
            catch (Exception)
            {
                continue;
            }

            Component source = FindComponentForBindingType(target, binding.type);
            ManualValueKind manualKind = GetManualKindForBinding(target, source, binding);
            int manualIndex = GetManualIndexForBinding(target, source, binding);
            AddPassiveValue(values, target, source, binding, manualKind, manualIndex, value);
        }
    }

    private void AddPassiveValue(Dictionary<string, PassiveFloatValue> values, GameObject target, Component source, EditorCurveBinding binding, ManualValueKind manualKind, int manualIndex, float value)
    {
        string key = GetPassiveBindingKey(target, binding);
        if (values.ContainsKey(key))
            return;

        values.Add(key, new PassiveFloatValue
        {
            Target = target,
            SourceComponent = source,
            Binding = binding,
            ManualKind = manualKind,
            ManualIndex = manualIndex,
            Value = value,
            Label = HumanizePropertyName(binding.propertyName)
        });
    }

    private bool AddOrUpdatePassiveValue(PassiveFloatValue value)
    {
        if (value == null || value.Target == null || value.Binding.type == null)
            return false;

        BindingSpec existing = FindExistingSpec(value.Target, value.Binding.type, value.Binding.propertyName);
        if (existing != null)
        {
            existing.CustomValue = value.Value;
            existing.UseCustomValue = true;
            return true;
        }

        BindingSpec spec = MakeSpec(value.Target, value.SourceComponent, value.Binding, value.Label, value.ManualKind, value.ManualIndex);
        spec.UseCustomValue = true;
        spec.CustomValue = value.Value;
        return AddBindingSpecSilently(value.Target, value.SourceComponent, GetTargetLabel(value.Target) + " / " + value.Binding.type.Name + " / " + value.Label, spec);
    }

    private BindingSpec FindExistingSpec(GameObject target, Type bindingType, string propertyName)
    {
        string key = GetSpecKey(target, bindingType, propertyName);
        foreach (PropertySet set in propertySets)
        {
            foreach (BindingSpec spec in set.Specs)
            {
                if (spec != null && GetSpecKey(spec.Target, spec.BindingType, spec.PropertyName) == key)
                    return spec;
            }
        }

        return null;
    }

    private string GetPassiveBindingKey(GameObject target, EditorCurveBinding binding)
    {
        return (target != null ? target.GetInstanceID().ToString() : "0") + "|" + GetBindingKey(binding);
    }

    private ManualValueKind GetManualKindForBinding(GameObject target, Component source, EditorCurveBinding binding)
    {
        if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive")
            return ManualValueKind.GameObjectActive;

        if (source != null && binding.propertyName == "m_Enabled" && SupportsEnabled(source))
            return ManualValueKind.ComponentEnabled;

        if (source is SkinnedMeshRenderer && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
            return ManualValueKind.BlendShape;

        return ManualValueKind.None;
    }

    private int GetManualIndexForBinding(GameObject target, Component source, EditorCurveBinding binding)
    {
        SkinnedMeshRenderer renderer = source as SkinnedMeshRenderer;
        if (renderer == null || renderer.sharedMesh == null || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
            return -1;

        string shapeName = binding.propertyName.Substring("blendShape.".Length);
        for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
        {
            if (renderer.sharedMesh.GetBlendShapeName(i) == shapeName)
                return i;
        }

        return -1;
    }

    private UndoPropertyModification[] OnUndoPostprocessModifications(UndoPropertyModification[] modifications)
    {
        if (!listenForAvatarChanges || addingFromPassiveCapture || modifications == null)
            return modifications;

        try
        {
            addingFromPassiveCapture = true;
            int added = 0;
            foreach (UndoPropertyModification modification in modifications)
            {
                if (TryCaptureModification(modification))
                    added++;
            }

            if (added > 0)
            {
                passiveCaptureStatus = "Added " + added + " changed propert" + (added == 1 ? "y" : "ies");
                Repaint();
            }
        }
        finally
        {
            addingFromPassiveCapture = false;
        }

        return modifications;
    }

    private bool TryCaptureModification(UndoPropertyModification modification)
    {
        if (modification.currentValue == null || modification.currentValue.target == null)
            return false;

        string propertyPath = modification.currentValue.propertyPath;
        if (string.IsNullOrEmpty(propertyPath))
            return false;

        UnityEngine.Object modifiedObject = modification.currentValue.target;
        GameObject target = null;
        Component source = null;
        Type bindingType = null;
        ManualValueKind manualKind = ManualValueKind.None;

        GameObject changedGameObject = modifiedObject as GameObject;
        if (changedGameObject != null)
        {
            target = changedGameObject;
            bindingType = typeof(GameObject);
            if (PropertyPathMatches("m_IsActive", propertyPath))
                manualKind = ManualValueKind.GameObjectActive;
        }
        else
        {
            source = modifiedObject as Component;
            if (source == null)
                return false;

            target = source.gameObject;
            bindingType = source.GetType();
            if (PropertyPathMatches("m_Enabled", propertyPath) && SupportsEnabled(source))
                manualKind = ManualValueKind.ComponentEnabled;
        }

        if (target == null || bindingType == null)
            return false;

        TryAssignActiveAvatarRoot(false);
        GameObject root = GetEffectiveRoot();
        if (root != null && !IsChildOf(target.transform, root.transform))
            return false;

        EditorCurveBinding binding;
        if (!TryFindBindingForModifiedProperty(target, source, bindingType, propertyPath, out binding))
        {
            if (manualKind == ManualValueKind.GameObjectActive)
                binding = EditorCurveBinding.FloatCurve(GetPathForTarget(target), typeof(GameObject), "m_IsActive");
            else if (manualKind == ManualValueKind.ComponentEnabled)
                binding = EditorCurveBinding.FloatCurve(GetPathForTarget(target), bindingType, "m_Enabled");
            else
                return false;
        }

        string label = GetTargetLabel(target) + " / " + binding.type.Name + " / " + HumanizePropertyName(binding.propertyName);
        BindingSpec spec = MakeSpec(target, source, binding, HumanizePropertyName(binding.propertyName), manualKind, -1);
        return AddBindingSpecSilently(target, source, label, spec);
    }

    private bool TryFindBindingForModifiedProperty(GameObject target, Component source, Type bindingType, string propertyPath, out EditorCurveBinding match)
    {
        match = default(EditorCurveBinding);
        GameObject root = ResolveRootForTarget(target);
        if (root == null)
            return false;

        EditorCurveBinding[] bindings;
        try
        {
            bindings = AnimationUtility.GetAnimatableBindings(target, root);
        }
        catch (Exception)
        {
            return false;
        }

        foreach (EditorCurveBinding binding in bindings)
        {
            if (binding.isPPtrCurve)
                continue;

            if (!BindingTypeMatches(binding.type, bindingType))
                continue;

            if (!PropertyPathMatches(binding.propertyName, propertyPath))
                continue;

            match = binding;
            return true;
        }

        return false;
    }

    private bool AddBindingSpecSilently(GameObject target, Component source, string setLabel, BindingSpec spec)
    {
        if (spec == null || target == null || spec.BindingType == null || string.IsNullOrEmpty(spec.PropertyName))
            return false;

        BindingSpec existing = FindExistingSpec(target, spec.BindingType, spec.PropertyName);
        if (existing != null)
        {
            float value;
            GameObject root = ResolveRootForTarget(target);
            if (root != null && TryReadCurrentSceneValue(existing, root, MakeRuntimeBinding(existing, root), out value))
            {
                existing.CustomValue = value;
                existing.UseCustomValue = true;
                return true;
            }

            return false;
        }

        AddTarget(target);
        InitializeSpecCustomValue(spec);
        spec.UseCustomValue = true;
        PropertySet set = new PropertySet
        {
            Label = setLabel,
            Target = target,
            SourceComponent = source,
            Expanded = false
        };
        set.Specs.Add(spec);
        propertySets.Add(set);
        return true;
    }

    private string GetPathForTarget(GameObject target)
    {
        GameObject root = ResolveRootForTarget(target);
        if (target == null || root == null || target == root)
            return "";

        return AnimationUtility.CalculateTransformPath(target.transform, root.transform);
    }

    private static bool PropertyPathMatches(string bindingProperty, string modifiedProperty)
    {
        if (string.Equals(bindingProperty, modifiedProperty, StringComparison.OrdinalIgnoreCase))
            return true;

        string binding = NormalizePropertyPath(bindingProperty);
        string modified = NormalizePropertyPath(modifiedProperty);
        if (binding == modified)
            return true;

        if (modified.Contains("localrotation") && binding.Contains("localeuler") && SameAxis(binding, modified))
            return true;

        return false;
    }

    private static string NormalizePropertyPath(string propertyPath)
    {
        return (propertyPath ?? "")
            .Replace("m_", "")
            .Replace("_", "")
            .Replace(" ", "")
            .ToLowerInvariant();
    }

    private static bool SameAxis(string a, string b)
    {
        return (a.EndsWith(".x", StringComparison.OrdinalIgnoreCase) && b.EndsWith(".x", StringComparison.OrdinalIgnoreCase))
            || (a.EndsWith(".y", StringComparison.OrdinalIgnoreCase) && b.EndsWith(".y", StringComparison.OrdinalIgnoreCase))
            || (a.EndsWith(".z", StringComparison.OrdinalIgnoreCase) && b.EndsWith(".z", StringComparison.OrdinalIgnoreCase))
            || (a.EndsWith(".w", StringComparison.OrdinalIgnoreCase) && b.EndsWith(".w", StringComparison.OrdinalIgnoreCase));
    }

    private void TryAssignActiveAvatarRoot(bool force)
    {
        if (!force && animationRoot != null)
            return;

        GameObject avatar = FindAvatarRootFromSelection();
        if (avatar == null)
            avatar = FindFirstAvatarRootInOpenScenes();

        if (avatar != null)
            animationRoot = avatar;
    }

    private GameObject FindAvatarRootFromSelection()
    {
        List<GameObject> candidates = new List<GameObject>();
        if (Selection.activeGameObject != null)
            candidates.Add(Selection.activeGameObject);

        candidates.AddRange(Selection.gameObjects.Where(go => go != null && go != Selection.activeGameObject));

        foreach (GameObject candidate in candidates)
        {
            Transform current = candidate.transform;
            while (current != null)
            {
                if (HasAvatarDescriptor(current.gameObject))
                    return current.gameObject;

                current = current.parent;
            }
        }

        return null;
    }

    private GameObject FindFirstAvatarRootInOpenScenes()
    {
        for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.isLoaded)
                continue;

            foreach (GameObject rootObject in scene.GetRootGameObjects())
            {
                GameObject avatar = FindAvatarDescriptorInChildren(rootObject);
                if (avatar != null)
                    return avatar;
            }
        }

        return null;
    }

    private static GameObject FindAvatarDescriptorInChildren(GameObject rootObject)
    {
        if (rootObject == null)
            return null;

        Transform rootTransform = rootObject.transform;
        List<Transform> stack = new List<Transform> { rootTransform };
        for (int i = 0; i < stack.Count; i++)
        {
            Transform current = stack[i];
            if (HasAvatarDescriptor(current.gameObject))
                return current.gameObject;

            for (int child = 0; child < current.childCount; child++)
                stack.Add(current.GetChild(child));
        }

        return null;
    }

    private static bool HasAvatarDescriptor(GameObject gameObject)
    {
        if (gameObject == null)
            return false;

        foreach (Component component in gameObject.GetComponents<Component>())
        {
            if (component == null)
                continue;

            Type type = component.GetType();
            if (type.Name == "VRCAvatarDescriptor" || (type.FullName != null && type.FullName.EndsWith(".VRCAvatarDescriptor", StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    private AnimationWindowInfo GetSelectedAnimationWindowInfo()
    {
        List<AnimationWindowInfo> windows = GetAnimationWindowInfos();
        if (windows.Count == 0)
            return null;

        if (windows.Count == 1)
            selectedAnimationWindowIndex = 0;

        selectedAnimationWindowIndex = Mathf.Clamp(selectedAnimationWindowIndex, 0, windows.Count - 1);
        return windows[selectedAnimationWindowIndex];
    }

    private List<AnimationWindowInfo> GetAnimationWindowInfos()
    {
        List<AnimationWindowInfo> infos = new List<AnimationWindowInfo>();
        Type animationWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.AnimationWindow");
        if (animationWindowType == null)
            return infos;

        UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(animationWindowType);
        foreach (UnityEngine.Object windowObject in windows)
        {
            EditorWindow window = windowObject as EditorWindow;
            if (window == null)
                continue;

            object state = GetMemberValue(window, "state");
            AnimationClip clip = GetMemberValue(window, "activeAnimationClip") as AnimationClip
                ?? GetMemberValue(window, "animationClip") as AnimationClip
                ?? GetMemberValue(window, "selectedAnimationClip") as AnimationClip
                ?? GetMemberValue(state, "activeAnimationClip") as AnimationClip
                ?? GetMemberValue(state, "animationClip") as AnimationClip
                ?? GetMemberValue(state, "selectedAnimationClip") as AnimationClip;

            float? time = GetNullableFloatMember(window, "currentTime")
                ?? GetNullableFloatMember(window, "time")
                ?? GetNullableFloatMember(state, "currentTime")
                ?? GetNullableFloatMember(state, "time");

            int? frame = GetNullableIntMember(window, "currentFrame")
                ?? GetNullableIntMember(window, "frame")
                ?? GetNullableIntMember(state, "currentFrame")
                ?? GetNullableIntMember(state, "frame");

            if (!frame.HasValue && time.HasValue && clip != null)
                frame = Mathf.RoundToInt(time.Value * Mathf.Max(1f, clip.frameRate));

            string title = window.titleContent != null ? window.titleContent.text : "Animation";
            string clipLabel = clip != null ? clip.name : "No Clip";
            infos.Add(new AnimationWindowInfo
            {
                Window = window,
                Clip = clip,
                Time = time,
                Frame = frame,
                Label = title + " - " + clipLabel
            });
        }

        return infos;
    }

    private static object GetMemberValue(object target, string memberName)
    {
        if (target == null || string.IsNullOrEmpty(memberName))
            return null;

        Type type = target.GetType();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo property = type.GetProperty(memberName, flags);
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            try
            {
                return property.GetValue(target, null);
            }
            catch (Exception)
            {
            }
        }

        FieldInfo field = type.GetField(memberName, flags);
        if (field != null)
        {
            try
            {
                return field.GetValue(target);
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    private static float? GetNullableFloatMember(object target, string memberName)
    {
        object value = GetMemberValue(target, memberName);
        if (value is float)
            return (float)value;

        if (value is double)
            return (float)(double)value;

        if (value is int)
            return (int)value;

        return null;
    }

    private static int? GetNullableIntMember(object target, string memberName)
    {
        object value = GetMemberValue(target, memberName);
        if (value is int)
            return (int)value;

        if (value is float)
            return Mathf.RoundToInt((float)value);

        if (value is double)
            return Mathf.RoundToInt((float)(double)value);

        return null;
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.###");
    }
}



