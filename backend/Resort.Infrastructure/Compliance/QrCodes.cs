using QRCoder;
using Resort.Application.Compliance;

namespace Resort.Infrastructure.Compliance;

/// <summary>QR codes as PNG bytes with QRCoder (MIT licence). Error correction M: a UPI link stays readable off a thermal
/// print or a phone screen. No System.Drawing, so it works on Linux and in containers.</summary>
public sealed class QrCodes : IQrCodes
{
    public byte[] Png(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(8);
    }
}
