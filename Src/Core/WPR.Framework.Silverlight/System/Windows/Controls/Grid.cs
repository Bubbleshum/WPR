using System;
using System.Linq;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Grid panel with column/row definitions supporting Auto, Pixel, and Star sizing.
    /// Children placed via attached <see cref="ColumnProperty"/> / <see cref="RowProperty"/>
    /// (and Span variants). Layout supports the common cases; the multi-pass fairness
    /// algorithm WPF/Silverlight uses for spanning children with Auto columns is
    /// approximated rather than reproduced exactly.
    /// </summary>
    public class Grid : Panel
    {
        public static readonly DependencyProperty ColumnProperty = DependencyProperty.RegisterAttached(
            "Column", typeof(int), typeof(Grid), new PropertyMetadata(0));

        public static readonly DependencyProperty RowProperty = DependencyProperty.RegisterAttached(
            "Row", typeof(int), typeof(Grid), new PropertyMetadata(0));

        public static readonly DependencyProperty ColumnSpanProperty = DependencyProperty.RegisterAttached(
            "ColumnSpan", typeof(int), typeof(Grid), new PropertyMetadata(1));

        public static readonly DependencyProperty RowSpanProperty = DependencyProperty.RegisterAttached(
            "RowSpan", typeof(int), typeof(Grid), new PropertyMetadata(1));

        // SL's actual Grid.attached-property accessors take FrameworkElement (not
        // DependencyObject) — patched user IL emits `call Grid::SetRow(FrameworkElement, Int32)`
        // and the CLR matches signatures exactly. We keep the DependencyObject
        // overloads alongside for code-behind that needs the broader argument type.
        public static int GetColumn(FrameworkElement element) =>
            (int)element.GetValue(ColumnProperty)!;
        public static void SetColumn(FrameworkElement element, int value) =>
            element.SetValue(ColumnProperty, value);
        public static int GetColumn(DependencyObject element) =>
            (int)element.GetValue(ColumnProperty)!;
        public static void SetColumn(DependencyObject element, int value) =>
            element.SetValue(ColumnProperty, value);

        public static int GetRow(FrameworkElement element) =>
            (int)element.GetValue(RowProperty)!;
        public static void SetRow(FrameworkElement element, int value) =>
            element.SetValue(RowProperty, value);
        public static int GetRow(DependencyObject element) =>
            (int)element.GetValue(RowProperty)!;
        public static void SetRow(DependencyObject element, int value) =>
            element.SetValue(RowProperty, value);

        public static int GetColumnSpan(FrameworkElement element) =>
            (int)element.GetValue(ColumnSpanProperty)!;
        public static void SetColumnSpan(FrameworkElement element, int value) =>
            element.SetValue(ColumnSpanProperty, value);
        public static int GetColumnSpan(DependencyObject element) =>
            (int)element.GetValue(ColumnSpanProperty)!;
        public static void SetColumnSpan(DependencyObject element, int value) =>
            element.SetValue(ColumnSpanProperty, value);

        public static int GetRowSpan(FrameworkElement element) =>
            (int)element.GetValue(RowSpanProperty)!;
        public static void SetRowSpan(FrameworkElement element, int value) =>
            element.SetValue(RowSpanProperty, value);
        public static int GetRowSpan(DependencyObject element) =>
            (int)element.GetValue(RowSpanProperty)!;
        public static void SetRowSpan(DependencyObject element, int value) =>
            element.SetValue(RowSpanProperty, value);

        public ColumnDefinitionCollection ColumnDefinitions { get; }
        public RowDefinitionCollection RowDefinitions { get; }

        private double[] _columnWidths = Array.Empty<double>();
        private double[] _rowHeights = Array.Empty<double>();

        public Grid()
        {
            ColumnDefinitions = new ColumnDefinitionCollection(InvalidateMeasure);
            RowDefinitions = new RowDefinitionCollection(InvalidateMeasure);
        }

        internal override Size WprMeasure(Size availableSize)
        {
            ResolveColumns(availableSize.Width);
            ResolveRows(availableSize.Height);

            foreach (UIElement child in Children)
            {
                (int col, int row, int colSpan, int rowSpan) = GetCell(child);
                double w = SumExtents(_columnWidths, col, colSpan);
                double h = SumExtents(_rowHeights, row, rowSpan);
                child.Measure(new Size(w, h));
            }

            // A star row or column WANTS only what its content needs, as in Silverlight; the
            // remainder is shared out at arrange. Reporting the share instead made every star Grid
            // ask for all the space it was offered, so one aligned to Top or Center filled its
            // parent anyway: Microsoft Mahjong's tutorial banner (Background #CC000000,
            // VerticalAlignment Top, one implicit star row) darkened the whole board and took
            // every tap meant for the tiles.
            double[] rowDesired = DesiredExtents(_rowHeights, IsStarRow, c => (GetRow(c), GetRowSpan(c)), c => c.DesiredSize.Height);
            double[] colDesired = DesiredExtents(_columnWidths, IsStarColumn, c => (GetColumn(c), GetColumnSpan(c)), c => c.DesiredSize.Width);
            return new Size(colDesired.Sum(), rowDesired.Sum());
        }

        private bool IsStarRow(int i) => RowDefinitions.Count == 0 || RowDefinitions[i].Height.IsStar;

        private bool IsStarColumn(int i) => ColumnDefinitions.Count == 0 || ColumnDefinitions[i].Width.IsStar;

        /// <summary>
        /// The resolved extents with each star track replaced by what the children in it need
        /// (a spanning child's need beyond its fixed tracks is shared among its star tracks),
        /// never more than the track was resolved to.
        /// </summary>
        private double[] DesiredExtents(double[] resolved, Func<int, bool> isStar,
            Func<UIElement, (int Start, int Span)> cell, Func<UIElement, double> desired)
        {
            var result = (double[])resolved.Clone();
            for (int i = 0; i < result.Length; i++)
            {
                if (isStar(i)) result[i] = 0;
            }

            foreach (UIElement child in Children)
            {
                (int start, int span) = cell(child);
                start = Clamp(start, 0, Math.Max(0, result.Length - 1));
                int end = Math.Min(result.Length, start + Math.Max(1, span));
                double fixedSum = 0;
                int stars = 0;
                for (int i = start; i < end; i++)
                {
                    if (isStar(i)) stars++;
                    else fixedSum += resolved[i];
                }

                if (stars == 0) continue;
                double each = (desired(child) - fixedSum) / stars;
                if (each <= 0) continue;
                for (int i = start; i < end; i++)
                {
                    if (isStar(i) && each > result[i]) result[i] = Math.Min(each, resolved[i]);
                }
            }

            return result;
        }

        internal override Size WprArrange(Size finalSize)
        {
            ResolveColumns(finalSize.Width);
            ResolveRows(finalSize.Height);

            double[] colOffsets = ComputePrefixSums(_columnWidths);
            double[] rowOffsets = ComputePrefixSums(_rowHeights);

            foreach (UIElement child in Children)
            {
                (int col, int row, int colSpan, int rowSpan) = GetCell(child);
                double x = colOffsets[Math.Min(col, colOffsets.Length - 1)];
                double y = rowOffsets[Math.Min(row, rowOffsets.Length - 1)];
                double w = SumExtents(_columnWidths, col, colSpan);
                double h = SumExtents(_rowHeights, row, rowSpan);
                child.Arrange(new Rect(x, y, w, h));
            }

            return finalSize;
        }

        private (int col, int row, int colSpan, int rowSpan) GetCell(UIElement child)
        {
            int col = Clamp(GetColumn(child), 0, Math.Max(0, _columnWidths.Length - 1));
            int row = Clamp(GetRow(child), 0, Math.Max(0, _rowHeights.Length - 1));
            int colSpan = Math.Max(1, GetColumnSpan(child));
            int rowSpan = Math.Max(1, GetRowSpan(child));
            return (col, row, colSpan, rowSpan);
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private void ResolveColumns(double available)
        {
            int cols = Math.Max(1, ColumnDefinitions.Count);
            _columnWidths = new double[cols];

            // Pass 1: Pixel (absolute)
            for (int i = 0; i < ColumnDefinitions.Count; i++)
            {
                var def = ColumnDefinitions[i];
                if (def.Width.IsAbsolute)
                    _columnWidths[i] = ApplyMinMax(def.Width.Value, def.MinWidth, def.MaxWidth);
            }

            // Pass 2: Auto — measure non-spanning children in each Auto column. With unbounded
            // width a star column sizes to its content too (see ResolveRows).
            bool infinite = double.IsInfinity(available);
            for (int i = 0; i < ColumnDefinitions.Count; i++)
            {
                var def = ColumnDefinitions[i];
                if (def.Width.IsAuto || (infinite && def.Width.IsStar))
                {
                    double max = 0;
                    foreach (UIElement child in Children)
                    {
                        if (GetColumn(child) == i && Math.Max(1, GetColumnSpan(child)) == 1)
                        {
                            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            if (child.DesiredSize.Width > max) max = child.DesiredSize.Width;
                        }
                    }
                    _columnWidths[i] = ApplyMinMax(max, def.MinWidth, def.MaxWidth);
                }
            }

            // Pass 3: Star — distribute remaining space proportionally
            double consumed = _columnWidths.Sum();
            double remaining = double.IsInfinity(available) ? 0 : Math.Max(0, available - consumed);

            double starTotal = 0;
            for (int i = 0; i < ColumnDefinitions.Count; i++)
            {
                if (ColumnDefinitions[i].Width.IsStar)
                    starTotal += ColumnDefinitions[i].Width.Value;
            }

            if (starTotal > 0 && remaining > 0)
            {
                for (int i = 0; i < ColumnDefinitions.Count; i++)
                {
                    var def = ColumnDefinitions[i];
                    if (def.Width.IsStar)
                    {
                        double share = def.Width.Value / starTotal * remaining;
                        _columnWidths[i] = ApplyMinMax(share, def.MinWidth, def.MaxWidth);
                    }
                }
            }

            // Implicit single column when no ColumnDefinitions are set: fit the
            // available width when finite; when infinite (Grid nested inside an
            // unconstrained-width host like a horizontal StackPanel), use the max
            // child desired width so the Grid doesn't collapse to 0.
            if (ColumnDefinitions.Count == 0)
            {
                if (double.IsInfinity(available))
                {
                    double maxW = 0;
                    foreach (UIElement child in Children)
                    {
                        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        if (child.DesiredSize.Width > maxW) maxW = child.DesiredSize.Width;
                    }
                    _columnWidths[0] = maxW;
                }
                else
                {
                    _columnWidths[0] = Math.Max(0, available);
                }
            }

            for (int i = 0; i < ColumnDefinitions.Count; i++)
                ColumnDefinitions[i].ActualWidth = _columnWidths[i];
        }

        private void ResolveRows(double available)
        {
            int rows = Math.Max(1, RowDefinitions.Count);
            _rowHeights = new double[rows];

            for (int i = 0; i < RowDefinitions.Count; i++)
            {
                var def = RowDefinitions[i];
                if (def.Height.IsAbsolute)
                    _rowHeights[i] = ApplyMinMax(def.Height.Value, def.MinHeight, def.MaxHeight);
            }

            // Measured with unbounded height (inside a stack, a scroll viewer, an item template),
            // a star row has no remainder to share and sizes to its content as Auto does, as in
            // Silverlight. Minesweeper's mode tiles (rows "*" and "30") were 30px tall without it.
            bool infinite = double.IsInfinity(available);
            for (int i = 0; i < RowDefinitions.Count; i++)
            {
                var def = RowDefinitions[i];
                if (def.Height.IsAuto || (infinite && def.Height.IsStar))
                {
                    double max = 0;
                    foreach (UIElement child in Children)
                    {
                        if (GetRow(child) == i && Math.Max(1, GetRowSpan(child)) == 1)
                        {
                            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            if (child.DesiredSize.Height > max) max = child.DesiredSize.Height;
                        }
                    }
                    _rowHeights[i] = ApplyMinMax(max, def.MinHeight, def.MaxHeight);
                }
            }

            double consumed = _rowHeights.Sum();
            double remaining = double.IsInfinity(available) ? 0 : Math.Max(0, available - consumed);

            double starTotal = 0;
            for (int i = 0; i < RowDefinitions.Count; i++)
            {
                if (RowDefinitions[i].Height.IsStar)
                    starTotal += RowDefinitions[i].Height.Value;
            }

            if (starTotal > 0 && remaining > 0)
            {
                for (int i = 0; i < RowDefinitions.Count; i++)
                {
                    var def = RowDefinitions[i];
                    if (def.Height.IsStar)
                    {
                        double share = def.Height.Value / starTotal * remaining;
                        _rowHeights[i] = ApplyMinMax(share, def.MinHeight, def.MaxHeight);
                    }
                }
            }

            // Same fix as the columns case — if the parent gave us infinite
            // height (StackPanel-in-ScrollViewer), measure children and take the
            // max so the Grid doesn't collapse to height=0 and squash everything
            // into a (W, 0) cell at the origin.
            if (RowDefinitions.Count == 0)
            {
                if (double.IsInfinity(available))
                {
                    double maxH = 0;
                    foreach (UIElement child in Children)
                    {
                        // EACH CHILD GETS ITS OWN COLUMN'S WIDTH. This used to hand every child
                        // _columnWidths[0], which is only right for a single-column grid. In the
                        // common "icon | text" layout the first column is Auto-sized to the icon,
                        // so the text was measured against the ICON's width — perhaps 64px — wrapped
                        // to five or six lines, and reported a height several times its real one.
                        //
                        // That height then propagated up as the row height and back down as a
                        // finite available height, so the whole control measured to the wrong size:
                        // Carcassonne's menu buttons came out 186px tall instead of ~88, and two of
                        // them no longer fitted their slot, which is why the third overlapped the
                        // toolbar beneath it.
                        int col = Clamp(GetColumn(child), 0, Math.Max(0, _columnWidths.Length - 1));
                        int colSpan = Math.Max(1, GetColumnSpan(child));
                        double childWidth = SumExtents(_columnWidths, col, colSpan);

                        child.Measure(new Size(childWidth, double.PositiveInfinity));
                        if (child.DesiredSize.Height > maxH) maxH = child.DesiredSize.Height;
                    }
                    _rowHeights[0] = maxH;
                }
                else
                {
                    _rowHeights[0] = Math.Max(0, available);
                }
            }
            for (int i = 0; i < RowDefinitions.Count; i++)
                RowDefinitions[i].ActualHeight = _rowHeights[i];
        }

        private static double ApplyMinMax(double v, double min, double max)
        {
            if (v < min) v = min;
            if (v > max) v = max;
            return v;
        }

        private static double SumExtents(double[] arr, int start, int count)
        {
            double sum = 0;
            int end = Math.Min(arr.Length, start + count);
            for (int i = start; i < end; i++) sum += arr[i];
            return sum;
        }

        private static double[] ComputePrefixSums(double[] arr)
        {
            var result = new double[arr.Length];
            double s = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                result[i] = s;
                s += arr[i];
            }
            return result;
        }
    }
}
