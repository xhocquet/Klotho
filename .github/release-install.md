## Install

Deterministic Unity & Godot framework for rollback, lockstep & server-authoritative multiplayer. Engine-agnostic pure C# core, FP64 fixed-point math, ECS, physics, navigation, and replays — frame-perfect, cross-platform reproducible.

### Godot (.NET)

1. Download the zip below and extract it
2. Copy the `klotho/` folder into your project's `res://addons/`
3. Add one line to your game `.csproj`:
   ```xml
   <Import Project="addons/klotho/Klotho.props" />
   ```
   > No `.csproj` yet? Generate one via *Project* ▸ *Tools* ▸ *C#* ▸ **Create C# solution**, then add the line above.
4. Build (`dotnet build` or the Godot editor's **Build** button)

See [Installation.Godot.md](https://github.com/xpTURN/Klotho/blob/__TAG__/Docs/Installation.Godot.md) for the dedicated-server setup and full details.

### Unity

*Window > Package Manager > + > Add package from git URL...* (in order):

```
https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask
https://github.com/xpTURN/Polyfill.git?path=src/Polyfill/Assets/Polyfill
https://github.com/xpTURN/Klotho.git?path=com.xpturn.klotho#__TAG__
```

See [Installation.Unity.md](https://github.com/xpTURN/Klotho/blob/__TAG__/Docs/Installation.Unity.md) for Polyfill activation, the dedicated-server setup and full details.

[README](https://github.com/xpTURN/Klotho/blob/__TAG__/README.md) · [CHANGELOG](https://github.com/xpTURN/Klotho/blob/__TAG__/CHANGELOG.md)
