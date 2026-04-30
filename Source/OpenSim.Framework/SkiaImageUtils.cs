/*
 * Copyright (C) 2026 Iain McCracken
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 *
 */

using SkiaSharp;
using CoreJ2K.Skia;
using CoreJ2K.Configuration;

namespace OpenSim.Framework;

/// <summary>
/// This little utility class provides a couple of common utilities, to reduce verbosity elsewhere.
/// </summary>
public static class SkiaImageUtils
{
    /// <summary>
    /// A basic lossless JPEG2000 encoder configuration.
    /// </summary>
    /// <remarks>
    /// The WithFileFormat(true) parameter enforces that a proper JP2 header is included in the output.
    /// </remarks>
    private static readonly J2KEncoderConfiguration encoderConfiguration = new J2KEncoderConfiguration().WithLossless().WithFileFormat(true);

    /// <summary>
    /// Try to encode a bitmap to JPEG2000, lossless, with JP2 header.
    /// </summary>
    /// <param name="inputImage">a Skia bitmap</param>
    /// <param name="encoded">(output) JPEG2000 bytes</param>
    /// <returns>true if the encode succeeded</returns>
    public static bool TryEncodeToJ2KLossless(SKBitmap inputImage, out byte[] encoded)
    {
        encoded = null;

        if (inputImage is null) return false;

        bool needDispose = false;
        SKBitmap workingImage = inputImage;
        if ((inputImage.ColorType != SKColorType.Rgb888x) && (inputImage.ColorType != SKColorType.Bgra8888))
        {
            // We know that both Rbg888x and Bgra8888 color types will encode to JPEG2000 successfully. Be paranoid and convert
            // other color types to Bgra8888. This way, images with alpha will come through properly, and images without alpha
            // will have alpha 100% applied properly.
            needDispose = true;
            workingImage = inputImage.Copy(SKColorType.Bgra8888);
        }

        encoded = workingImage.EncodeToJ2K(encoderConfiguration);

        if (needDispose)
            workingImage.Dispose();

        return encoded is not null && encoded.Length != 0;
    }

    /// <summary>
    /// Try to encode a bitmap to PNG.
    /// </summary>
    /// <param name="inputImage">a Skia bitmap</param>
    /// <param name="encoded">(output) PNG bytes</param>
    /// <returns>true if the encoding succeeded.</returns>
    public static bool TryEncodeToPng(SKBitmap inputImage, out byte[] encoded)
    {
        encoded = null;

        // Bypass the exception throwing null check in SkiaSharp
        if (inputImage is null) return false;

        bool needDispose = false;
        SKBitmap workingImage = inputImage;
        if ((inputImage.ColorType != SKColorType.Rgb888x) && (inputImage.ColorType != SKColorType.Bgra8888))
        {
            // We know that both Rbg888x and Bgra8888 color types will encode to PNG successfully. Be paranoid and convert
            // other color types to Bgra8888. This way, images with alpha will come through properly, and images without alpha
            // will have alpha 100% applied properly.
            needDispose = true;
            workingImage = inputImage.Copy(SKColorType.Bgra8888);
        }

        using SKData data = workingImage.Encode(SKEncodedImageFormat.Png, 100);
        encoded = data?.ToArray();

        if (needDispose)
            workingImage.Dispose();

        return encoded is not null && encoded.Length != 0;
    }

    /// <summary>
    /// Try to encode a bitmap to JPEG
    /// </summary>
    /// <param name="inputImage">a Skia bitmap</param>
    /// <param name="quality">encoding quality</param>
    /// <param name="encoded">(output) JPEG bytes</param>
    /// <returns>true if the encoding succeeded</returns>
    public static bool TryEncodeToJpeg(SKBitmap inputImage, int quality, out byte[] encoded)
    {
        encoded = null;

        if (inputImage is null) return false;

        bool needDispose = false;
        SKBitmap workingImage = inputImage;
        if (inputImage.ColorType != SKColorType.Bgra8888)
        {
            // Encoding to JPEG will silently fail with Rgb888x, so convert to Bgra8888 which is known to encode successfully.
            workingImage = inputImage.Copy(SKColorType.Bgra8888);
        }
        using SKData data = workingImage.Encode(SKEncodedImageFormat.Jpeg, quality);
        encoded = data?.ToArray();

        if (needDispose)
            workingImage.Dispose();

        return encoded is not null && encoded.Length != 0;
    }

    /// <summary>
    /// Try to decode a JPEG2000. If successful, the result will likely be SKColorType.Rgb888x and SKAlphaType.Opaque.
    /// </summary>
    /// <param name="inData">bytes of a JPEG2000 image</param>
    /// <param name="decoded">(output) a Skia bitmap, which will likely have the Rgb888x color typw</param>
    /// <returns>true if the decode succeeded</returns>
    public static bool TryDecodeFromJ2K(byte[] inData, out SKBitmap decoded)
    {
        decoded = null;

        if (inData is null || inData.Length == 0) return false;

        try
        {
            decoded = SKBitmapJ2kExtensions.FromJ2KBytes(inData);
        }
        catch (InvalidOperationException e)
        {
            // The given array of bytes is not a valid JPEG2000 image. Report failure.
            return false;
        }

        return decoded is not null;
    }

    /// <summary>
    /// Try to decode an image (other than a JPEG2000)
    /// </summary>
    /// <param name="inData">bytes of an image</param>
    /// <param name="decoded">(output) a Skia bitmap</param>
    /// <returns>true if the decode succeeded</returns>
    public static bool TryDecodeFromBytes(byte[] inData, out SKBitmap decoded)
    {
        decoded = null;

        if (inData is null || inData.Length == 0) return false;

        decoded = SKBitmap.Decode(inData);

        return decoded is not null;
    }

    /// <summary>
    /// Do a simple opaque resize.
    /// </summary>
    /// <remarks>
    /// The output color type is Bgra8888 and the alpha type is Opaque. The sampling is bilinear.
    /// </remarks>
    /// <param name="input"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <returns>The new bitmap with the new size.</returns>
    public static SKBitmap OpaqueResize(SKBitmap input, int x, int y)
    {
        if (input is null) return null;
        return input.Resize(new SKImageInfo(x, y, SKColorType.Bgra8888, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
    }

    /// <summary>
    /// Check if a file is *not* a JPEG
    /// </summary>
    /// <param name="checkMe">At least the first 3 bytes of the file</param>
    /// <returns>true if the file is definitely not a JPEG</returns>
    public static bool IsNotJpeg(byte[] checkMe)
    {
        if (checkMe is null || checkMe.Length < 3) return true;

        return checkMe[0] != 0xFF || checkMe[1] != 0xD8 || checkMe[2] != 0xFF;
    }

    // ************************************************************

    // Old method to be replaced.

    public static SKBitmap ResizeImageSolid(SKBitmap image, int width, int height)
    {
        SKBitmap result = new(width, height, SKColorType.Rgb888x, SKAlphaType.Opaque);

        using (SKCanvas canvas = new(result))
        using (SKPaint paint = new())
        {
            paint.IsAntialias = true;
            paint.FilterQuality = SKFilterQuality.High;

            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(image, new SKRect(0, 0, width, height), paint);
        }

        return result;
    }
}
