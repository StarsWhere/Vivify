# Vivify

*Bring your map to life.*

Vivify allows mappers to compile an AssetBundle, then load, instantiate, and manipulate custom-made assets using in-game events. These assets include post-processing shaders, 3D models, audio files, textures, and pretty much anything else that can be compiled into an asset bundle.

Join the Discord here: [https://discord.gg/rrZf3kapeh](https://discord.gg/rrZf3kapeh)

## Documentation

For any mappers interested in using any of these mods, please visit the wiki at [https://heck.aeroluna.dev/](https://heck.aeroluna.dev/)

## Build for Beat Saber 1.45.2

Run `pwsh -File ./scripts/build.ps1 -GameRoot '<Beat Saber directory>'`. The build uses the installed game and mod assemblies and downloads dnlib 4.5.0 into the repository's temporary cache. The plugin is written to `releases/bs-1.45.2/Plugins/`.

For legacy shader conversion, run `pwsh -File ./scripts/build-shaders.ps1` with Unity 2022.3.22f1 installed, then `pwsh -File ./scripts/build-assets.ps1 -AuditPath '<SongCore audit JSON>'`. Shader outputs and the pinned UnityPy dependency are cached under `.tmp/`; converted bundles are written under `releases/bs-1.45.2/UserData/Vivify/Compatibility/`.
