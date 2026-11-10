# QuickLook.Plugin.ThreeDViewer

<a href="https://github.com/QL-Win/QuickLook"><img src="https://img.shields.io/badge/QuickLook-4.x-blue" alt="QuickLook 4.x"></a>
<a href="https://github.com/Sisyphe42/QuickLook.Plugin.ThreeDViewer/releases/latest"><img src="https://img.shields.io/github/v/release/Sisyphe42/QuickLook.Plugin.ThreeDViewer" alt="Release"></a>
<img src="https://img.shields.io/badge/.NET%20Framework-4.6.2-purple" alt=".NET 4.6.2">

A [QuickLook](https://github.com/QL-Win/QuickLook) plugin that previews 3D models
with spacebar — mouse rotate, glTF PBR materials, animation playback, and a
background that follows your dark/light theme.

**Formats:** `.glb` `.gltf` `.fbx` `.obj` `.stl` `.ply` `.3ds` `.dae` `.off` `.x3d` `.3mf`

## Install

1. Grab `QuickLook.Plugin.ThreeDViewer.qlplugin` from the
   [latest release](https://github.com/Sisyphe42/QuickLook.Plugin.ThreeDViewer/releases/latest).
2. Open it — QuickLook installs it and asks for a restart.
3. Select any 3D file and press <kbd>Space</kbd>.

> Note: another 3D plugin (e.g. the legacy
> [HelixViewer](https://github.com/jeremyhart/QuickLook.Plugin.HelixViewer))
> may conflict with this one; if `.obj` / `.stl` / `.ply` / `.3ds` previews
> don't open here, move that plugin's folder out of
> `UserData\QuickLook.Plugin\`.

## Features

- **Mouse control** — left-drag rotate, right-drag pan, middle-drag pan, wheel
  zoom, with inertia.
- **glTF PBR** — base color / metallic / roughness / emissive factors and
  embedded or external textures are read from the file and mapped onto WPF's
  material pipeline.
- **Animation** — node and bone animation channels play automatically
  (translation / rotation / scale keyframes with slerp).
- **Theme aware** — background, status text and ViewCube follow the system
  dark/light theme; the host title bar is synced via `ContextObject.Theme`.
- **Large models** — above 500k faces the mesh is uniformly downsampled to
  ≤120k triangles with a notice banner; the UI never blocks.
- **Fast first frame** — one-time warmup at QuickLook startup moves all JIT /
  template initialization off the preview path; `View()` returns in ~1 ms and
  the model streams in on a background thread.

## Preview quality note

WPF's Viewport3D is a fixed-function pipeline, so PBR rendering is an
approximation (factors drive diffuse/specular/emissive), not physically based
IBL. Normal/occlusion maps, morph targets and animation blending are not
rendered.

## Build

Requires .NET SDK (any recent version) — the project targets .NET Framework
4.6.2 via reference assemblies.

```powershell
dotnet build src\QuickLook.Plugin.ThreeDViewer -c Release
powershell -File scripts\build.ps1            # build + create .qlplugin
```

`scripts\build.ps1` also supports `-Deploy` (copy to QuickLook's UserData),
`-DisableHelixViewer` / `-RollbackHelixViewer` (take over the legacy plugin's
extensions). Nothing is applied without those flags.

## Test

`test\models\` ships 23 samples covering every format, PBR factors, bone
skinning, transparency, multi-level node transforms and multi-material meshes.
Three >500k-face samples for degradation testing are regenerable — see
`test\models\README.md`.

## License

[MIT](LICENSE)
