# Known issues

This page separates known limitations from ordinary setup warnings. It is updated as compatibility testing expands.

## Confirmed limitations

- A PC shader does not always have an equivalent mobile shader. Review mappings and animated material properties manually.
- Transparent, additive, matcap, metallic, emission, and particle materials may need visual adjustment after conversion.
- PhysBone, collider, contact, particle, and build-system limits are platform-specific. Stage 7 reports them but cannot decide the creator's intended compromise.
- Avatar-specific VRCFury, Modular Avatar, NDMF, GoGoLoco, and other build-system behavior depends on the exact installed package versions.
- Very aggressive mesh reduction can damage silhouettes, hair strands, clothing edges, blendshapes, or deformation. Keep Original when necessary.
- A successful prefab build does not replace the official SDK report or in-world Build & Test.

## Unsupported or not guaranteed

- The Studio does not bundle AutoLOD, Mesh Baker, VRChat SDK binaries, paid shaders, paid avatar assets, or third-party build systems.
- It does not promise identical behavior for every custom shader, animation-driven material effect, particle setup, or package version.
- It does not upload avatars automatically.

## Workarounds

- Use the original mesh candidate for damaged geometry.
- Keep special materials separate and complete the Stage 6 manual-polish checkpoint.
- Use Stage 7's exact component paths to remove only unwanted mobile components; restore them from the cache if needed.
- Install the avatar's required build systems before Stage 7 validation.
- If a dependency signature changes, rebuild the affected stage in order rather than reusing an old checkpoint.

## Reporting a new issue

Check the issue templates first. Include Unity/SDK versions, target platform, installed build systems, failing stage, exact error text, generated report path, and reproduction steps. Remove private avatar assets and credentials before attaching files.
