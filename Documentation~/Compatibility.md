# Compatibility matrix

This is a starting record of the development environment and test categories. It is evidence for the listed setup, not a guarantee for every avatar or package version.

## Development environment

| System | Version | Status | Notes |
| --- | --- | --- | --- |
| Unity | 2022.3.22f1 | Pass | Main development editor |
| VRChat Avatars SDK | 3.10.4 | Pass | Android/iOS workflow exercised |
| VRCFury | 1.1404.0 | Partial | Disposable validation clone; avatar-specific components still need review |
| Modular Avatar | Not installed in the reference project | Not tested | Install it in the test project before evaluating |
| NDMF | Not installed in the reference project | Not tested | Install it in the test project before evaluating |
| GoGoLoco | Avatar-dependent | Partial | Missing package files block validation until installed |
| Poiyomi | Installed avatar shader versions vary | Partial | Explicit mobile mapping and manual polish required |
| lilToon | Not part of the reference test | Not tested | Requires a separate avatar test |

## Avatar categories

| Category | Status | Notes |
| --- | --- | --- |
| Multiple outfits and toggles | Tested | Mobile exclusions and fallbacks must be reviewed |
| UV-tiled avatar | Tested | Supported 4x4 split path; each piece still needs visual approval |
| Poiyomi-heavy avatar | Partial | Material mappings and emission/matcap settings need manual review |
| Particle-heavy avatar | Partial | Stage 7 reports particle limits for creator review |
| PhysBone-heavy avatar | Partial | Component and affected-transform limits require creator choices |
| Animation-driven material swaps | Partial | Unsupported properties require explicit static fallback decisions |
| Multiple hair systems | Tested | Exclude only intended mobile hair and verify fallback toggles |

When reporting a result, describe the category and package versions without uploading private avatar content.
