# Sophia's Animation Creator

Sophia's Animation Creator is a Unity editor tool for quickly creating, copying, and applying animation keyframes without entering Unity's animation record mode.

Open it in Unity from:

```text
Tools > Sophia's Animation Creator
```

## Install With VCC

Add this VPM repository URL to VCC:

```text
https://raw.githubusercontent.com/sophia1000/sophias_animation-creator/main/vpm.json
```

Then add `Sophia's Animation Creator` to your Unity 2022.3 avatar project.

## Releasing Versions

Update the `version` field in `package.json`, then run:

```powershell
pwsh ./Tools/BuildVpmRepository.ps1
```

The script creates a versioned zip in `dist/` and updates `vpm.json` without removing previous versions. That is what lets VCC show older versions in its version selector.

If GitHub Actions is enabled, pushing a changed `package.json` to `main` will run the same update automatically and commit the changed `vpm.json` plus `dist/` zip.
