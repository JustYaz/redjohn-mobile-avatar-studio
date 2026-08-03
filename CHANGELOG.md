# Changelog

## 0.1.4 - Material-only default selection

- A newly analyzed recipe now retains every original source mesh automatically when no candidates have been generated, regardless of default generation flags.
- Stage 5 can proceed after material and texture work without manually changing every mesh row.

## 0.1.3 - Original-mesh conversion path

- Material-only conversions no longer require mesh candidate generation. When reduction is not requested, the analyzed original source mesh is retained automatically.
- Stage 5 still blocks missing source meshes and invalid generated candidates.

## 0.1.2 - Public documentation polish

- Standardized the public product name as RedJohn Mobile Avatar Studio.
- Added direct documentation links, corrected VCC wording, and re-enabled the release-safety workflow.
- Removed the optional release-safety workflow after GitHub Actions became unavailable on the publisher account.

## 0.1.1 - Maintenance release

- Promoted the previously unreleased component-restore, material sanitization, shader classification, behavior fallback, and Stage 8 reporting work into a versioned release.
- Added the VCC/VPM listing, compatibility matrix, known-issues guide, package documentation URLs, and release-safety workflow.
- Stage 7 component repair has separate Available and Removed/Restore tabs with a deterministic restore cache.
- Generated materials are reset to a clean target-shader state while visible manual settings are preserved.
- Stage 8 reports exact VRChat SDK validation messages instead of only the generic wrapper.
- Emission maps remain available for manual polish while generated lit materials start with emission disabled.
- Material names, shader classification, whitelist recommendations, capability-aware property transfer, and behavior fallback decisions are now explicit and reviewable.

## 0.1.0 - First public release

- First usable open-source release of the eight-stage Android/iOS mobile-avatar workflow.
- Includes non-destructive mesh candidates, UV-tile splitting, reviewed mobile material and texture conversion, behavior remapping, manual-polish checkpoints, component repair, and official SDK size validation.
- Includes the stable UV texture-sharing pass and behavior handling for excluded UV-split material slots.
- This is a first public release, not a claim that every shader, avatar, or third-party build-system version is universally compatible.

## 0.1.0-preview.17

- Stage 6 success no longer opens a modal dialog from the long-running editor callback, avoiding Unity's false "operation completed successfully" Console error.

## 0.1.0-preview.16

- Material-swap curves for fully excluded UV-split renderers or slots are now removed from the mobile copy instead of blocking behavior conversion.

## 0.1.0-preview.15

- Behavior material remapping now resolves reloaded material references by stable asset identity and retained UV-piece mapping.

## 0.1.0-preview.14

- Duplicate UV texture sharing remains available after Android/iOS texture overrides have been applied; the operation safely re-scans the texture choices afterward.

## 0.1.0-preview.13

- Stage 4 can optionally share duplicate UV-split texture copies while keeping materials, shader settings, animations, and toggles separate.

## 0.1.0-preview.12

- Stage 4 texture analysis now excludes textures used only by mobile-excluded renderers or removed UV pieces.

## 0.1.0-preview.11

- UV-split piece rows now have a direct Preview button and isolate the selected piece in the preview pane.

## 0.1.0-preview.10

- Generated Toon Standard materials retain matcap textures but start with matcap activation, strength, and keywords disabled for manual polish.
- UV-split pieces now receive per-piece isolated texture copies instead of sharing one generated texture asset.

## 0.1.0-preview.9

- Exposes retained UV-split pieces in the Stage 2 mesh list with individual keep/exclude toggles and animation/menu labels.

## 0.1.0-preview.8

- Added a Stage 2 button to approve all retained UV-split mesh candidates in one click.

## 0.1.0-preview.7

- Rewrites animated material-object swaps on UV-split renderers onto retained generated pieces.
- Keeps alternate materials referenced only by those swaps in the mobile material conversion set.
- Converts the single-slot generated piece bindings deterministically instead of stopping Stage 6.

## 0.1.0-preview.6

- Added draggable, persistent Studio pane splitters for the Stage 2 controls/preview, UV tile list, renderer list, Overview contract list, Stage 7 validation issues, and component repair lists.
- Added an outer Stage 2 controls scrollbar so resizing a section cannot make lower controls unreachable.
- Added double-click reset for every Studio splitter.

## 0.1.0-preview.5

- Fixed UV Tile Split scanning so particle, trail, line, and other renderer types without a MeshFilter are skipped safely instead of aborting the scan.

## 0.1.0-preview.4

- Added a non-destructive Stage 2 UV Tile Split/Bake workflow for supported Poiyomi-style 4x4 UV Tile Dissolve meshes.
- Generates isolated skinned/static geometry pieces, per-piece source material domains, and isolated texture assets before mobile shader conversion.
- Preserves skinning, blendshapes, UV channels, normals, tangents, colors, and source bounds while refusing unsafe cross-tile topology.
- Rewrites binary UV-tile animation curves to generated child activation curves and duplicates compatible material/blendshape/renderer-enable bindings.
- Integrates retained tile pieces with adaptive mesh candidate generation, visual approvals, preview, material conversion, final assembly, and contract validation.

## 0.1.0-preview.3

- Stage 5 no longer requires or applies mobile material mappings on renderers excluded in Stage 2, including renderers beneath an excluded hierarchy root.
- Exclusion choices remain intact and their payload is still cleared by the existing non-destructive mobile-content pass.

## 0.1.0-preview.2

- Stage 6 now sanitizes generated materials referenced only by animation-driven material swaps, not only materials assigned in the prefab's default renderer state.
- The final Android/iOS texture scan now follows the complete generated prefab dependency graph so textures used by animated material swaps receive the same reviewed mobile overrides.
- Active Toon Standard properties, including intentional matcaps and retained emission maps, remain untouched while stale properties from the original PC shader are removed.

## 0.1.0-preview.1

- Added the resumable eight-stage mobile avatar workflow.
- Added per-renderer mesh candidate selection and approval.
- Added isolated mobile material conversion and Android/iOS texture profiles.
- Added behavior isolation, mobile content exclusions, and fallback validation.
- Added the Stage 6 manual-material checkpoint and final dependency rescan.
- Added disposable Stage 7 resolved audits and exact component repair controls.
- Added official Stage 8 Android/iOS bundle measurement without automatic upload.
- Verified the first complete Android and iOS conversion workflow on development test data.
