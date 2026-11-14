# U8Sharp

U8 archive reading and writing (*Skyward Sword HD* `.arc`, and most Wii titles).

```csharp
U8File arc = U8File.FromBinary(Lz.DecompressFile(@"…\romfs\Stage\F000\NX\F000_stg_l0.arc.LZ"));
byte[] stage = arc["dat/stage.bzs"]!.Data;

arc.Set("dat/stage.bzs", stage);
byte[] packed = arc.Write();
```

## Build

```bash
dotnet build U8Sharp.slnx -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
