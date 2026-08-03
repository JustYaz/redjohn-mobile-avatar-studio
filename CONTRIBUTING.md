# Contributing to Mobile Avatar Studio

Thank you for helping improve the project.

## Development setup

1. Use a Unity 2022.3 VRChat Avatars project.
2. Install the VRChat Avatars SDK and any avatar-specific build systems needed by your test avatar.
3. Add this package from the local package folder or a local tarball.
4. Keep test avatars and generated workspaces outside the package source.
5. Run the editor smoke tests from `Tools > Mobile Avatar Studio > Development` when the matching test assets are available.

## Pull requests

- Keep the PC source prefab, source materials, source textures, and source behavior assets untouched.
- Do not add avatar-specific paths or assumptions to production code.
- Keep mobile compromises explicit, reviewable, and reversible.
- Add or update a focused test and changelog entry for behavior changes.
- Report the Unity version, VRChat SDK version, target platform, stage, and exact Console error for regressions.

Generated files under `Assets/MobileAvatarStudioGenerated` belong to a test project and should not be committed as product source.
