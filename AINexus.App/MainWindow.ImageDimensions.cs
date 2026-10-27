using System.Globalization;
using System.Windows.Controls;

namespace AINexus.App;

public partial class MainWindow
{
    internal bool TryReadImageDimensions(out int width, out int height, out string error)
    {
        var widthValid = int.TryParse(ImageWidthText?.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out width);
        var heightValid = int.TryParse(ImageHeightText?.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out height);
        if (!widthValid || !heightValid || width is < 256 or > 1024 || height is < 256 or > 1024
            || width % 64 != 0 || height % 64 != 0)
        {
            error = "Enter width and height from 256–1024 pixels, in multiples of 64.";
            return false;
        }
        error = "256–1024 pixels, in multiples of 64. Actual checkpoint support and memory fit require a run.";
        return true;
    }

    private void ImageDimensionsChanged(object sender, TextChangedEventArgs e)
    {
        if (ImageGenerateButton is not null) RefreshImageControls();
    }
}
