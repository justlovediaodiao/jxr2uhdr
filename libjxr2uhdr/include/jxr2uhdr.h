#pragma once

#define JXR2UHDR_API __declspec(dllexport)

#ifdef __cplusplus
extern "C" {
#endif

enum jxr2uhdr_result {
  JXR2UHDR_OK = 0,
  JXR2UHDR_INVALID_ARGUMENT = 1,
  JXR2UHDR_COM_INIT_FAILED = 2,
  JXR2UHDR_WIC_ERROR = 3,
  JXR2UHDR_IO_ERROR = 4,
  JXR2UHDR_OUT_OF_MEMORY = 5,
  JXR2UHDR_ULTRAHDR_ERROR = 6,
  JXR2UHDR_UNSUPPORTED_PIXEL_FORMAT = 7
};

/*
 * Convert a JPEG XR file to an Ultra HDR JPEG.
 *
 * All paths are UTF-8.
 *
 * jxr_path: input JPEG XR path. Required.
 * sdr_image_path: optional SDR image path. Pass NULL or an empty string to let libultrahdr
 *                 tone-map the SDR rendition from the HDR image.
 * quality: Ultra HDR JPEG quality. Negative defaults to 95; values above 100 use 100.
 * out_jpg_path: output Ultra HDR JPEG path. Required.
 *
 * Returns JXR2UHDR_OK on success, or another jxr2uhdr_result value on failure.
 */
JXR2UHDR_API int jxr2uhdr_convert(const char* jxr_path, const char* sdr_image_path, int quality,
                                  const char* out_jpg_path);

#ifdef __cplusplus
}
#endif
