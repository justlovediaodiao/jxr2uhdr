#define WIN32_LEAN_AND_MEAN
#ifndef COBJMACROS
#define COBJMACROS
#endif

#include "jxr2uhdr.h"

#include <windows.h>
#include <wincodec.h>

#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "ultrahdr_api.h"

typedef struct decoded_hdr_image {
  uint8_t* pixels;
  UINT width;
  UINT height;
} decoded_hdr_image_t;

typedef struct decoded_sdr_image {
  uint8_t* data;
  UINT width;
  UINT height;
} decoded_sdr_image_t;

static uint16_t float_to_half(float value) {
  uint32_t bits;
  uint32_t sign;
  int32_t exponent;
  uint32_t mantissa;

  memcpy(&bits, &value, sizeof(bits));
  sign = (bits >> 16) & 0x8000u;
  exponent = (int32_t)((bits >> 23) & 0xffu) - 127 + 15;
  mantissa = bits & 0x7fffffu;

  if (exponent <= 0) {
    if (exponent < -10) {
      return (uint16_t)sign;
    }
    mantissa = (mantissa | 0x800000u) >> (uint32_t)(1 - exponent);
    if ((mantissa & 0x00001000u) != 0) {
      mantissa += 0x00002000u;
    }
    return (uint16_t)(sign | (mantissa >> 13));
  }

  if (exponent >= 31) {
    if (((bits >> 23) & 0xffu) == 0xffu && mantissa != 0) {
      return (uint16_t)(sign | 0x7c00u | (mantissa >> 13) | 1u);
    }
    return (uint16_t)(sign | 0x7c00u);
  }

  if ((mantissa & 0x00001000u) != 0) {
    mantissa += 0x00002000u;
    if ((mantissa & 0x00800000u) != 0) {
      mantissa = 0;
      exponent += 1;
      if (exponent >= 31) {
        return (uint16_t)(sign | 0x7c00u);
      }
    }
  }

  return (uint16_t)(sign | ((uint32_t)exponent << 10) | (mantissa >> 13));
}

static int convert_f32_rgba_to_f16_rgba(const uint8_t* input, size_t pixel_count,
                                        uint8_t** output) {
  size_t i;
  uint16_t* half_pixels;
  const float* float_pixels;

  if (pixel_count > SIZE_MAX / 8) {
    return JXR2UHDR_OUT_OF_MEMORY;
  }

  half_pixels = (uint16_t*)malloc(pixel_count * 8);
  if (half_pixels == NULL) {
    return JXR2UHDR_OUT_OF_MEMORY;
  }

  float_pixels = (const float*)input;
  for (i = 0; i < pixel_count * 4; ++i) {
    half_pixels[i] = float_to_half(float_pixels[i]);
  }

  *output = (uint8_t*)half_pixels;
  return JXR2UHDR_OK;
}

static int copy_source_pixels(IWICBitmapSource* source, UINT width, UINT height,
                              UINT bytes_per_pixel, uint8_t** pixels) {
  HRESULT hr;
  uint8_t* buffer;
  size_t stride;
  size_t size;

  stride = (size_t)width * bytes_per_pixel;
  if (height != 0 && stride > SIZE_MAX / height) {
    return JXR2UHDR_OUT_OF_MEMORY;
  }
  size = stride * height;
  if (stride > UINT_MAX || size > UINT_MAX) {
    return JXR2UHDR_OUT_OF_MEMORY;
  }

  buffer = (uint8_t*)malloc(size);
  if (buffer == NULL) {
    return JXR2UHDR_OUT_OF_MEMORY;
  }

  hr = IWICBitmapSource_CopyPixels(source, NULL, (UINT)stride, (UINT)size, buffer);

  if (FAILED(hr)) {
    free(buffer);
    return JXR2UHDR_WIC_ERROR;
  }

  *pixels = buffer;
  return JXR2UHDR_OK;
}

static int copy_frame_as_format(IWICBitmapSource* source, const WICPixelFormatGUID* format,
                                UINT width, UINT height, UINT bytes_per_pixel, uint8_t** pixels) {
  HRESULT hr;
  IWICImagingFactory* factory = NULL;
  IWICFormatConverter* converter = NULL;
  int result;

  hr = CoCreateInstance(&CLSID_WICImagingFactory, NULL, CLSCTX_INPROC_SERVER,
                        &IID_IWICImagingFactory, (void**)&factory);
  if (FAILED(hr)) {
    return JXR2UHDR_WIC_ERROR;
  }

  hr = IWICImagingFactory_CreateFormatConverter(factory, &converter);
  if (FAILED(hr)) {
    IWICImagingFactory_Release(factory);
    return JXR2UHDR_WIC_ERROR;
  }

  hr = IWICFormatConverter_Initialize(converter, source, format, WICBitmapDitherTypeNone, NULL,
                                      0.0, WICBitmapPaletteTypeCustom);
  if (FAILED(hr)) {
    IWICFormatConverter_Release(converter);
    IWICImagingFactory_Release(factory);
    return JXR2UHDR_UNSUPPORTED_PIXEL_FORMAT;
  }

  result = copy_source_pixels((IWICBitmapSource*)converter, width, height, bytes_per_pixel, pixels);

  IWICFormatConverter_Release(converter);
  IWICImagingFactory_Release(factory);
  return result;
}

static wchar_t* utf8_to_windows_path(const char* value) {
  int required;
  wchar_t* windows_path;

  if (value == NULL) {
    return NULL;
  }

  required = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value, -1, NULL, 0);
  if (required <= 0) {
    return NULL;
  }

  windows_path = (wchar_t*)malloc((size_t)required * sizeof(wchar_t));
  if (windows_path == NULL) {
    return NULL;
  }

  if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value, -1, windows_path, required) <= 0) {
    free(windows_path);
    return NULL;
  }

  return windows_path;
}

static HRESULT create_decoder_from_path(IWICImagingFactory* factory, const char* path,
                                             IWICBitmapDecoder** decoder) {
  HRESULT hr;
  wchar_t* windows_path = utf8_to_windows_path(path);
  if (windows_path == NULL) {
    return E_INVALIDARG;
  }

  hr = IWICImagingFactory_CreateDecoderFromFilename(factory, windows_path, NULL, GENERIC_READ,
                                                    WICDecodeMetadataCacheOnDemand, decoder);
  free(windows_path);
  return hr;
}

static int decode_jxr_with_wic(const char* path, decoded_hdr_image_t* image) {
  HRESULT hr;
  IWICImagingFactory* factory = NULL;
  IWICBitmapDecoder* decoder = NULL;
  IWICBitmapFrameDecode* frame = NULL;
  IWICBitmapSource* source = NULL;
  WICPixelFormatGUID source_format;
  UINT width = 0;
  UINT height = 0;
  uint8_t* pixels = NULL;
  int result = JXR2UHDR_OK;

  memset(image, 0, sizeof(*image));

  hr = CoCreateInstance(&CLSID_WICImagingFactory, NULL, CLSCTX_INPROC_SERVER,
                        &IID_IWICImagingFactory, (void**)&factory);
  if (FAILED(hr)) {
    return JXR2UHDR_WIC_ERROR;
  }

  hr = create_decoder_from_path(factory, path, &decoder);
  if (FAILED(hr)) {
    result = JXR2UHDR_WIC_ERROR;
    goto cleanup;
  }

  hr = IWICBitmapDecoder_GetFrame(decoder, 0, &frame);
  if (FAILED(hr)) {
    result = JXR2UHDR_WIC_ERROR;
    goto cleanup;
  }

  hr = IWICBitmapFrameDecode_GetSize(frame, &width, &height);
  if (FAILED(hr) || width == 0 || height == 0) {
    result = JXR2UHDR_WIC_ERROR;
    goto cleanup;
  }

  hr = IWICBitmapFrameDecode_GetPixelFormat(frame, &source_format);
  if (FAILED(hr)) {
    result = JXR2UHDR_WIC_ERROR;
    goto cleanup;
  }

  source = (IWICBitmapSource*)frame;

  if (IsEqualGUID(&source_format, &GUID_WICPixelFormat64bppRGBAHalf)) {
    result = copy_source_pixels(source, width, height, 8, &pixels);
  } else if (IsEqualGUID(&source_format, &GUID_WICPixelFormat128bppRGBAFloat)) {
    uint8_t* f32_pixels = NULL;
    size_t pixel_count = (size_t)width * height;
    result = copy_source_pixels(source, width, height, 16, &f32_pixels);
    if (result == JXR2UHDR_OK) {
      result = convert_f32_rgba_to_f16_rgba(f32_pixels, pixel_count, &pixels);
    }
    free(f32_pixels);
  } else {
    result = copy_frame_as_format(source, &GUID_WICPixelFormat64bppRGBAHalf, width, height, 8,
                                  &pixels);
    if (result != JXR2UHDR_OK) {
      result = JXR2UHDR_UNSUPPORTED_PIXEL_FORMAT;
    }
  }

  if (result == JXR2UHDR_OK) {
    image->pixels = pixels;
    image->width = width;
    image->height = height;
    pixels = NULL;
  }

cleanup:
  free(pixels);
  if (frame != NULL) IWICBitmapFrameDecode_Release(frame);
  if (decoder != NULL) IWICBitmapDecoder_Release(decoder);
  if (factory != NULL) IWICImagingFactory_Release(factory);
  return result;
}

static int write_file(const char* path, const void* data, size_t size) {
  FILE* file;
  size_t written;
  wchar_t* windows_path;

  windows_path = utf8_to_windows_path(path);
  if (windows_path == NULL) {
    return JXR2UHDR_INVALID_ARGUMENT;
  }

  file = _wfopen(windows_path, L"wb");
  free(windows_path);
  if (file == NULL) {
    return JXR2UHDR_IO_ERROR;
  }

  written = fwrite(data, 1, size, file);
  fclose(file);

  if (written != size) {
    return JXR2UHDR_IO_ERROR;
  }

  return JXR2UHDR_OK;
}

static int decode_sdr_with_wic(const char* path, decoded_sdr_image_t* image) {
  HRESULT hr;
  IWICImagingFactory* factory = NULL;
  IWICBitmapDecoder* decoder = NULL;
  IWICBitmapFrameDecode* frame = NULL;
  IWICBitmapSource* source = NULL;
  UINT width = 0;
  UINT height = 0;
  uint8_t* pixels = NULL;
  int result = JXR2UHDR_OK;

  memset(image, 0, sizeof(*image));

  hr = CoCreateInstance(&CLSID_WICImagingFactory, NULL, CLSCTX_INPROC_SERVER,
                        &IID_IWICImagingFactory, (void**)&factory);
  if (FAILED(hr)) {
    return JXR2UHDR_WIC_ERROR;
  }

  hr = create_decoder_from_path(factory, path, &decoder);
  if (FAILED(hr)) {
    result = JXR2UHDR_WIC_ERROR;
    goto cleanup;
  }

  hr = IWICBitmapDecoder_GetFrame(decoder, 0, &frame);
  if (FAILED(hr)) {
    result = JXR2UHDR_WIC_ERROR;
    goto cleanup;
  }

  hr = IWICBitmapFrameDecode_GetSize(frame, &width, &height);
  if (FAILED(hr) || width == 0 || height == 0) {
    result = JXR2UHDR_WIC_ERROR;
    goto cleanup;
  }

  source = (IWICBitmapSource*)frame;
  result = copy_frame_as_format(source, &GUID_WICPixelFormat32bppRGBA, width, height, 4, &pixels);
  if (result == JXR2UHDR_OK) {
    image->data = pixels;
    image->width = width;
    image->height = height;
    pixels = NULL;
  }

cleanup:
  free(pixels);
  if (frame != NULL) IWICBitmapFrameDecode_Release(frame);
  if (decoder != NULL) IWICBitmapDecoder_Release(decoder);
  if (factory != NULL) IWICImagingFactory_Release(factory);
  return result;
}

static int check_uhdr_status(uhdr_error_info_t status) {
  return status.error_code == UHDR_CODEC_OK ? JXR2UHDR_OK : JXR2UHDR_ULTRAHDR_ERROR;
}

static int encode_ultrahdr(const decoded_hdr_image_t* hdr_image, const char* sdr_image_path,
                           int quality, const char* out_jpg_path) {
  uhdr_codec_private_t* encoder = NULL;
  uhdr_raw_image_t hdr_raw;
  uhdr_raw_image_t sdr_raw;
  uhdr_compressed_image_t* output;
  decoded_sdr_image_t sdr_image;
  int result;

  memset(&hdr_raw, 0, sizeof(hdr_raw));
  memset(&sdr_raw, 0, sizeof(sdr_raw));
  memset(&sdr_image, 0, sizeof(sdr_image));

  if (quality < 0) {
    quality = 95;
  }
  if (quality > 100) {
    quality = 100;
  }

  encoder = uhdr_create_encoder();
  if (encoder == NULL) {
    return JXR2UHDR_ULTRAHDR_ERROR;
  }

  hdr_raw.fmt = UHDR_IMG_FMT_64bppRGBAHalfFloat;
  hdr_raw.cg = UHDR_CG_BT_709;
  hdr_raw.ct = UHDR_CT_LINEAR;
  hdr_raw.range = UHDR_CR_FULL_RANGE;
  hdr_raw.w = hdr_image->width;
  hdr_raw.h = hdr_image->height;
  hdr_raw.planes[UHDR_PLANE_PACKED] = hdr_image->pixels;
  hdr_raw.stride[UHDR_PLANE_PACKED] = hdr_image->width;

  result = check_uhdr_status(uhdr_enc_set_raw_image(encoder, &hdr_raw, UHDR_HDR_IMG));
  if (result != JXR2UHDR_OK) goto cleanup;

  if (sdr_image_path != NULL && sdr_image_path[0] != '\0') {
    result = decode_sdr_with_wic(sdr_image_path, &sdr_image);
    if (result != JXR2UHDR_OK) goto cleanup;

    if (sdr_image.width != hdr_image->width || sdr_image.height != hdr_image->height) {
      result = JXR2UHDR_INVALID_ARGUMENT;
      goto cleanup;
    }

    sdr_raw.fmt = UHDR_IMG_FMT_32bppRGBA8888;
    sdr_raw.cg = UHDR_CG_BT_709;
    sdr_raw.ct = UHDR_CT_SRGB;
    sdr_raw.range = UHDR_CR_FULL_RANGE;
    sdr_raw.w = sdr_image.width;
    sdr_raw.h = sdr_image.height;
    sdr_raw.planes[UHDR_PLANE_PACKED] = sdr_image.data;
    sdr_raw.stride[UHDR_PLANE_PACKED] = sdr_image.width;

    result = check_uhdr_status(uhdr_enc_set_raw_image(encoder, &sdr_raw, UHDR_SDR_IMG));
    if (result != JXR2UHDR_OK) goto cleanup;
  }

  result = check_uhdr_status(uhdr_enc_set_quality(encoder, quality, UHDR_BASE_IMG));
  if (result != JXR2UHDR_OK) goto cleanup;

  result = check_uhdr_status(uhdr_enc_set_quality(encoder, quality, UHDR_GAIN_MAP_IMG));
  if (result != JXR2UHDR_OK) goto cleanup;

  result = check_uhdr_status(uhdr_encode(encoder));
  if (result != JXR2UHDR_OK) goto cleanup;

  output = uhdr_get_encoded_stream(encoder);
  if (output == NULL || output->data == NULL || output->data_sz == 0) {
    result = JXR2UHDR_ULTRAHDR_ERROR;
    goto cleanup;
  }

  result = write_file(out_jpg_path, output->data, output->data_sz);

cleanup:
  free(sdr_image.data);
  if (encoder != NULL) {
    uhdr_release_encoder(encoder);
  }
  return result;
}

JXR2UHDR_API int jxr2uhdr_convert(const char* jxr_path, const char* sdr_image_path, int quality,
                                  const char* out_jpg_path) {
  HRESULT hr;
  int com_initialized = 0;
  decoded_hdr_image_t hdr_image;
  int result;

  memset(&hdr_image, 0, sizeof(hdr_image));

  if (jxr_path == NULL || jxr_path[0] == '\0' || out_jpg_path == NULL ||
      out_jpg_path[0] == '\0') {
    return JXR2UHDR_INVALID_ARGUMENT;
  }

  hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
  if (SUCCEEDED(hr)) {
    com_initialized = 1;
  } else if (hr != RPC_E_CHANGED_MODE) {
    return JXR2UHDR_COM_INIT_FAILED;
  }

  result = decode_jxr_with_wic(jxr_path, &hdr_image);
  if (result == JXR2UHDR_OK) {
    result = encode_ultrahdr(&hdr_image, sdr_image_path, quality, out_jpg_path);
  }

  free(hdr_image.pixels);

  if (com_initialized) {
    CoUninitialize();
  }

  return result;
}
