# Mobile Avatar Studio product direction

Mobile Avatar Studio is a non-destructive cross-platform avatar optimization and compatibility studio for VRChat.

Its purpose is to maintain the relationship between an original PC avatar, its generated mobile counterpart, the user's protected features, approved visual compromises, current VRChat limits, and later changes to the source avatar.

## Product promise

Select a PC avatar, inspect every proposed tradeoff, approve the mobile representation, and generate validated Android/iOS counterparts without modifying the original avatar.

The product does not promise perfect automatic conversion, identical shader or PhysBone behavior, a guaranteed performance rank, or one-click success for every avatar. It must provide source protection, explainable changes, reproducible conversion, user-approved compromises, dependency isolation, measured Android/iOS results, and detectable behavior breakage.

## Central model

The Avatar Behavior Contract is the center of every conversion. It records menu controls, parameters, animator states and clips, object toggles, material properties, blendshapes, visemes, contacts, PhysBones, constraints, particles, audio, renderer paths, and external build-system features.

Every conversion pass must declare which contract entries it reads, preserves, changes, approximates, removes, or places at risk.

## Required workflow

1. Resolve build-time avatar systems.
2. Create a protected source snapshot.
3. Build the Avatar Behavior Contract.
4. Analyze meshes, materials, dynamics, features, and dependencies.
5. Generate adaptive mesh candidates.
6. Measure structural, visual, deformation, blendshape, normal, UV, and skinning quality independently.
7. Preview and explicitly approve candidates per renderer.
8. Compare whole-avatar budget strategies.
9. Convert shaders and textures with reviewable mappings.
10. Build behavior-aware material control domains and atlases.
11. Convert and validate animation curves.
12. Optimize PhysBones through an approved dynamics recipe.
13. Generate an isolated mobile prefab.
14. Simulate menus, controls, and high-risk state combinations.
15. Diff the PC and mobile behavior contracts.
16. Build actual Android/iOS bundles and validate current SDK limits.
17. Capture visual regression references.
18. Configure the Android/iOS platform overrides.
19. Save a deterministic recipe, dependency manifest, hashes, and conversion history.

## Uncompromising rules

1. Never modify the PC source.
2. Never silently choose visual compromises.
3. Never call a conversion successful without behavior validation.
4. Never use estimates when actual Android/iOS builds can provide the answer.
5. Never make the user repeat approved work when the source avatar changes.

## Development rule

Incomplete measurements must be labeled as not measured. A technically compliant but visually unapproved build is a draft, not a successful conversion.
