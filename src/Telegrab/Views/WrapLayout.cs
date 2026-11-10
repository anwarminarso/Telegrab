using Microsoft.Maui.Layouts;

namespace Telegrab.Views;

/// <summary>
/// Layout bungkus (wrap) sederhana untuk tile media di dalam item <c>CollectionView</c>.
///
/// Pengganti <c>FlexLayout Wrap="Wrap"</c> + <c>BindableLayout</c>: di Windows kombinasi itu di
/// dalam item virtualized dapat memicu "Layout cycle detected" (dotnet/maui#10546, #18515) saat
/// container terus didaur ulang ketika scroll panjang, berujung crash native di
/// <c>Microsoft.UI.Xaml.dll</c>. Di sini ukuran hanya diturunkan dari constraint + ukuran anak,
/// tanpa status dari arrange sebelumnya, sehingga hasil measure stabil (tidak berosilasi).
/// </summary>
public class WrapLayout : Layout
{
    protected override ILayoutManager CreateLayoutManager() => new WrapLayoutManager(this);

    private sealed class WrapLayoutManager : ILayoutManager
    {
        private readonly WrapLayout _layout;

        public WrapLayoutManager(WrapLayout layout) => _layout = layout;

        public Size Measure(double widthConstraint, double heightConstraint)
        {
            var padding = _layout.Padding;
            double available = widthConstraint - padding.HorizontalThickness;

            double x = 0, y = 0, rowHeight = 0, maxWidth = 0;
            foreach (var child in _layout)
            {
                if (child.Visibility == Visibility.Collapsed) continue;

                // DesiredSize sudah termasuk margin anak.
                var size = child.Measure(double.PositiveInfinity, double.PositiveInfinity);
                if (x > 0 && x + size.Width > available)
                {
                    y += rowHeight;
                    x = 0;
                    rowHeight = 0;
                }
                x += size.Width;
                rowHeight = Math.Max(rowHeight, size.Height);
                maxWidth = Math.Max(maxWidth, x);
            }
            y += rowHeight;

            return new Size(maxWidth + padding.HorizontalThickness, y + padding.VerticalThickness);
        }

        public Size ArrangeChildren(Rect bounds)
        {
            var padding = _layout.Padding;
            double left = bounds.Left + padding.Left;
            double top = bounds.Top + padding.Top;
            double available = bounds.Width - padding.HorizontalThickness;

            double x = 0, y = 0, rowHeight = 0;
            foreach (var child in _layout)
            {
                if (child.Visibility == Visibility.Collapsed) continue;

                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > available)
                {
                    y += rowHeight;
                    x = 0;
                    rowHeight = 0;
                }
                child.Arrange(new Rect(left + x, top + y, size.Width, size.Height));
                x += size.Width;
                rowHeight = Math.Max(rowHeight, size.Height);
            }

            return new Size(bounds.Width, y + rowHeight + padding.VerticalThickness);
        }
    }
}
