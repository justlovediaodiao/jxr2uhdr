# libjxr2uhdr

`libjxr2uhdr` is the native Windows component of the parent `jxr2uhdr` project. It converts JPEG XR images to Ultra HDR JPEGs and is built as a Windows x64 DLL.

The public API is declared in `include/jxr2uhdr.h`:

```c
int jxr2uhdr_convert(const char* jxr_path,
                     const char* sdr_image_path,
                     int quality,
                     const char* out_jpg_path);
```

All paths are UTF-8. Pass `NULL` or an empty string for `sdr_image_path` to let libultrahdr create the SDR rendition from the HDR image. Negative `quality` defaults to 95; values above 100 are clamped to 100.

## Build

The library supports Windows x64 with MSVC and uses Windows Imaging Component to decode JPEG XR. From the parent repository root, run:

```powershell
cmake -S libjxr2uhdr -B build/libjxr2uhdr -A x64 `
  -DCMAKE_TOOLCHAIN_FILE=C:/vcpkg/scripts/buildsystems/vcpkg.cmake `
  -DVCPKG_TARGET_TRIPLET=x64-windows-static-md

cmake --build build/libjxr2uhdr --config Release --target jxr2uhdr
```

Dependencies:

- libultrahdr is fetched from `google/libultrahdr` tag `v1.4.0` at configure time.
- libjpeg-turbo is installed by vcpkg through `vcpkg.json`.

The parent repository's GitHub Actions workflow builds this library before the desktop app. Manual runs retain downloadable build artifacts; tags matching `v*` publish the library and app together in one GitHub Release.
