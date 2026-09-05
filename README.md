# JXR to Ultra HDR

A Windows desktop app for converting JPEG XR images to Ultra HDR JPEGs. The repository includes the native [`libjxr2uhdr`](libjxr2uhdr) library used by the app.

## Features

- Select Windows HDR screenshots in `.jxr` format
- Convert to Ultra HDR JPEG images with good compatibility
- Automatically use a same-name `.png`, `.jpg`, or `.jpeg` SDR image next to the JXR file when present
- Process multiple images in parallel
- Save output next to the source file as `*_utralhdr.jpg`, or choose one output directory for the batch

## Repository layout

- `libjxr2uhdr/` — native Windows x64 DLL and its public C API
- `jxr2uhdr.csproj` — Avalonia desktop app

## Build

Requirements:

- Windows x64 and Visual Studio with the MSVC C++ toolchain
- CMake
- vcpkg, available at `C:/vcpkg`
- .NET 10 SDK

From the repository root, build the native library first:

```powershell
cmake -S libjxr2uhdr -B build/libjxr2uhdr -A x64 `
  -DCMAKE_TOOLCHAIN_FILE=C:/vcpkg/scripts/buildsystems/vcpkg.cmake `
  -DVCPKG_TARGET_TRIPLET=x64-windows-static-md
cmake --build build/libjxr2uhdr --config Release --target jxr2uhdr

New-Item -ItemType Directory -Force native/win-x64 | Out-Null
Copy-Item build/libjxr2uhdr/Release/jxr2uhdr.dll native/win-x64/
```

Then publish the desktop app:

```powershell
dotnet publish -r win-x64 -o publish/win-x64
```

For details about the native API and its dependencies, see [`libjxr2uhdr/README.md`](libjxr2uhdr/README.md).

## CI and releases

The `Build and release` workflow can be run manually to build both Windows x64 artifacts. Pushing a tag matching `v*` additionally creates one GitHub Release containing:

- `libjxr2uhdr-windows-x64.zip` — the DLL, public header, and native-library license
- `jxr2uhdr-<tag>-win-x64.zip` — the published desktop app, including the DLL
