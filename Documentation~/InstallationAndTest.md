# Mobile Avatar Studio 0.1.4 installation and second-avatar test

## Requirements

- A VRChat Avatars project created through VRChat Creator Companion.
- Unity 2022.3 with the platform modules needed for the targets being tested.
- The avatar's own required packages, shaders, and build systems installed before analysis.
- AutoLOD only when reduced mesh candidates are required. It is not distributed with Mobile Avatar Studio.

## Install

1. Back up or copy the target Unity project.
2. Open `Window > Package Manager` in Unity.
3. Choose `+ > Add package from disk...`.
4. Select the extracted package folder containing `package.json`, or install through the VCC listing.
5. Wait for script compilation and confirm the Console has no Mobile Avatar Studio errors.
6. Open `Tools > Mobile Avatar Studio > Open`.

## Serious user-test checklist

Use an avatar unrelated to the original development avatar. Do not copy any prior generated workspace into the test project.

1. Analyze the PC source prefab and confirm its mesh, material, behavior, and menu totals look believable.
2. Generate and visually inspect mesh candidates. Keep Original where a reduction damages the mesh.
3. Review mobile material mappings and texture categories rather than approving unexpected mappings blindly.
4. Apply Android/iOS texture overrides and confirm Standalone settings remain unchanged.
5. Build Stages 5 and 6, then perform the requested manual mobile-material polish.
6. Click `Save Manual Work & Rescan`. The Studio sanitizes renderer and animation-only generated materials, then discovers their textures. Review anything newly found, apply both mobile profiles, and save the checkpoint again.
7. Run Stage 7 separately for every intended mobile target. Resolve errors; visually judge warnings such as particles and PhysBones.
8. Run Stage 8 and record measured compressed and uncompressed sizes.
9. Use VRChat Build & Test and test menus, synced toggles, fallbacks, animations, PhysBones, contacts, particles, materials, and avatar visibility with another client before upload.
10. Confirm that the PC prefab, PC texture settings, and PC behavior assets were not modified.

## Reporting a preview problem

Include the Unity version, VRChat SDK version, installed avatar build systems, active target, failing stage, exact Console error, the generated report from `Assets/MobileAvatarStudioGenerated/<AvatarJob>/Current/Reports`, and whether the problem reproduces after reopening the saved recipe. Do not distribute the avatar itself unless its license permits it.
