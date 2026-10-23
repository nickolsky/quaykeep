using Avalonia.Media.Imaging;
using QRCoder;

namespace Quaykeep.Services;

/// <summary>QR code as a bitmap: black modules on white with the standard quiet zone, sharp when scaled up (nearest neighbour).</summary>
public static class QrImage
{
    private const int PixelsPerModule = 8;

    public static Bitmap Render(string text)
    {
        using var generator = new QRCodeGenerator();
        QRCodeData data;
        try
        {
            data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        }
        catch (QRCoder.Exceptions.DataTooLongException)
        {
            data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L); // long configs: less error correction
        }
        using (data)
        {
            var png = new PngByteQRCode(data).GetGraphic(PixelsPerModule, drawQuietZones: true);
            using var ms = new MemoryStream(png);
            return new Bitmap(ms);
        }
    }
}
