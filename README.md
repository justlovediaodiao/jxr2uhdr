# JXR to Ultra HDR

A small desktop app for converting JXR images to Ultra HDR JPEG with [`libjxr2uhdr`](https://github.com/justlovediaodiao/libjxr2uhdr).

## Features

- Select Windows HDR screenshots in `.jxr` format
- Convert to Ultra HDR JPEG images with good compatibility
- Automatically use a same-name `.png`, `.jpg`, or `.jpeg` SDR image next to the JXR file when present
- Process multiple images in parallel
- Save output next to the source file as `*_utralhdr.jpg`

## Build

- .NET 10 SDK
- Windows x64 runtime requires `native/win-x64/jxr2uhdr.dll` from the latest [`libjxr2uhdr` release](https://github.com/justlovediaodiao/libjxr2uhdr/releases)
- Run:

```bash
dotnet publish -r win-x64
```

## Release

Push a tag matching `v*` to run GitHub Actions. The release workflow downloads the latest `libjxr2uhdr-windows-x64.zip`, publishes the Windows x64 app, and uploads a zipped artifact to the GitHub Release.
