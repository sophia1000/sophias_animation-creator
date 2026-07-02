# Changelog

## 1.0.3

### Changed

- Material Swap now uses the tool's Objects list instead of the current Unity hierarchy selection.
- Material Swap labels and button text now say they operate on Objects list renderers.
- Object Search fallback now uses the Objects list when no Search Under or Animation Root is set, instead of using selected hierarchy roots.
- Passive Record Mode snapshot polling now watches the Objects list instead of the current hierarchy selection.
- Quick Add helper text now explains that objects are added through Add Selected or object fields.

### Package

- Bumped the package version to `1.0.3`.
- Added the `1.0.3` package entry to `vpm.json` while keeping previous versions available.
- Built `dist/com.sophia.animation-creator-1.0.3.zip`.

## 1.0.2

### Changed

- Improved editor performance on larger avatars and busier Unity scenes.
- Component Picker now caches its filtered results instead of rebuilding the list on every `OnGUI` repaint.
- Object Search component instance counts are cached, so the Unity, Unity Common, VRChat, Modular Avatar, VRCFury, and All filters do not repeatedly rescan the avatar hierarchy.
- Component And Property Search results are cached until the selected objects, animation root, or search text changes.
- Material Property Search results are cached until the selected objects, animation root, or material search text changes.
- Loaded component type discovery is cached, reducing repeated assembly scans for Unity, VRChat, Modular Avatar, and VRCFury component filters.
- Animation Window reflection member lookups used by Copy / Apply mode are cached.
- Search caches now refresh when Unity selection, hierarchy, project assets, or window focus changes.

### Package

- Bumped the package version to `1.0.2`.
- Added the `1.0.2` package entry to `vpm.json` while keeping `1.0.0` and `1.0.1` available.
- Built `dist/com.sophia.animation-creator-1.0.2.zip`.
- Updated the VPM build script to strip generated Unity `.meta` files from package zips.
- Added `*.meta` to `.gitignore` so Unity-generated package meta files do not clutter commits.

## 1.0.1

### Added

- Added material swap animation support for selected renderer objects.
- Material swap clips key Unity object-reference curves for renderer material slots.
- Object search can now be narrowed to a dragged GameObject and its children.
- Component picker now has an All filter, only shows components found in the current avatar/search scope, and displays instance counts.

## 1.0.0

### Added

- Initial VCC/VPM package release.
- Adds `Tools > Sophia's Animation Creator`.
- Supports creating clips, copying one-frame keys, applying keys to existing clips, saved setups, object/property search, quick add modes, and passive record mode.
