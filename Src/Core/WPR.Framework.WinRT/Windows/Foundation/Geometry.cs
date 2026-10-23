using System;
using System.Globalization;

namespace Windows.Foundation
{
    // The value types .NET's projection relocated into System.Runtime.WindowsRuntime. WP8 user IL
    // calls their properties (Size::get_Width, Rect::get_Bottom ...), so they are real structs with
    // properties; WindowsTypeSynthesizer's per-game versions had fields only. Field order is the
    // WinRT layout.

    /// <summary>Shim for <c>Windows.Foundation.Point</c>.</summary>
    public struct Point : IEquatable<Point>
    {
        private double _x;
        private double _y;

        public Point(double x, double y)
        {
            _x = x;
            _y = y;
        }

        public double X { get => _x; set => _x = value; }

        public double Y { get => _y; set => _y = value; }

        public static bool operator ==(Point point1, Point point2) => point1._x == point2._x && point1._y == point2._y;

        public static bool operator !=(Point point1, Point point2) => !(point1 == point2);

        public bool Equals(Point value) => this == value;

        public override bool Equals(object? o) => o is Point p && this == p;

        public override int GetHashCode() => HashCode.Combine(_x, _y);

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{_x},{_y}");
    }

    /// <summary>Shim for <c>Windows.Foundation.Size</c>.</summary>
    public struct Size : IEquatable<Size>
    {
        private double _width;
        private double _height;

        public Size(double width, double height)
        {
            if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
            _width = width;
            _height = height;
        }

        public static Size Empty => new() { _width = double.NegativeInfinity, _height = double.NegativeInfinity };

        public double Width
        {
            get => _width;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(Width));
                _width = value;
            }
        }

        public double Height
        {
            get => _height;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(Height));
                _height = value;
            }
        }

        public bool IsEmpty => _width < 0;

        public static bool operator ==(Size size1, Size size2) => size1._width == size2._width && size1._height == size2._height;

        public static bool operator !=(Size size1, Size size2) => !(size1 == size2);

        public bool Equals(Size value) => this == value;

        public override bool Equals(object? o) => o is Size s && this == s;

        public override int GetHashCode() => HashCode.Combine(_width, _height);

        public override string ToString() => IsEmpty ? "Empty" : string.Create(CultureInfo.InvariantCulture, $"{_width},{_height}");
    }

    /// <summary>Shim for <c>Windows.Foundation.Rect</c>.</summary>
    public struct Rect : IEquatable<Rect>
    {
        private double _x;
        private double _y;
        private double _width;
        private double _height;

        public Rect(double x, double y, double width, double height)
        {
            if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
            _x = x;
            _y = y;
            _width = width;
            _height = height;
        }

        public Rect(Point point1, Point point2)
        {
            _x = Math.Min(point1.X, point2.X);
            _y = Math.Min(point1.Y, point2.Y);
            _width = Math.Abs(point2.X - point1.X);
            _height = Math.Abs(point2.Y - point1.Y);
        }

        public Rect(Point location, Size size)
        {
            if (size.IsEmpty)
            {
                this = Empty;
                return;
            }
            _x = location.X;
            _y = location.Y;
            _width = size.Width;
            _height = size.Height;
        }

        public static Rect Empty => new()
        {
            _x = double.PositiveInfinity,
            _y = double.PositiveInfinity,
            _width = double.NegativeInfinity,
            _height = double.NegativeInfinity,
        };

        public double X { get => _x; set => _x = value; }

        public double Y { get => _y; set => _y = value; }

        public double Width
        {
            get => _width;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(Width));
                _width = value;
            }
        }

        public double Height
        {
            get => _height;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(Height));
                _height = value;
            }
        }

        public double Left => _x;

        public double Top => _y;

        public double Right => IsEmpty ? double.NegativeInfinity : _x + _width;

        public double Bottom => IsEmpty ? double.NegativeInfinity : _y + _height;

        public bool IsEmpty => _width < 0;

        public bool Contains(Point point) =>
            !IsEmpty && point.X >= _x && point.X - _width <= _x && point.Y >= _y && point.Y - _height <= _y;

        public void Intersect(Rect rect)
        {
            if (IsEmpty || rect.IsEmpty || rect.Left > Right || rect.Right < Left || rect.Top > Bottom || rect.Bottom < Top)
            {
                this = Empty;
                return;
            }
            double left = Math.Max(Left, rect.Left);
            double top = Math.Max(Top, rect.Top);
            _width = Math.Max(Math.Min(Right, rect.Right) - left, 0);
            _height = Math.Max(Math.Min(Bottom, rect.Bottom) - top, 0);
            _x = left;
            _y = top;
        }

        public void Union(Rect rect)
        {
            if (IsEmpty)
            {
                this = rect;
                return;
            }
            if (rect.IsEmpty) return;
            double left = Math.Min(Left, rect.Left);
            double top = Math.Min(Top, rect.Top);
            _width = Math.Max(Right, rect.Right) - left;
            _height = Math.Max(Bottom, rect.Bottom) - top;
            _x = left;
            _y = top;
        }

        public void Union(Point point) => Union(new Rect(point, point));

        public static bool operator ==(Rect rect1, Rect rect2) =>
            rect1._x == rect2._x && rect1._y == rect2._y && rect1._width == rect2._width && rect1._height == rect2._height;

        public static bool operator !=(Rect rect1, Rect rect2) => !(rect1 == rect2);

        public bool Equals(Rect value) => this == value;

        public override bool Equals(object? o) => o is Rect r && this == r;

        public override int GetHashCode() => HashCode.Combine(_x, _y, _width, _height);

        public override string ToString() =>
            IsEmpty ? "Empty" : string.Create(CultureInfo.InvariantCulture, $"{_x},{_y},{_width},{_height}");
    }
}

namespace Windows.UI
{
    /// <summary>Shim for <c>Windows.UI.Color</c> (relocated into System.Runtime.WindowsRuntime).</summary>
    public struct Color : IEquatable<Color>
    {
        private byte _A;
        private byte _R;
        private byte _G;
        private byte _B;

        public byte A { get => _A; set => _A = value; }

        public byte R { get => _R; set => _R = value; }

        public byte G { get => _G; set => _G = value; }

        public byte B { get => _B; set => _B = value; }

        public static Color FromArgb(byte a, byte r, byte g, byte b) => new() { _A = a, _R = r, _G = g, _B = b };

        public static bool operator ==(Color color1, Color color2) =>
            color1._A == color2._A && color1._R == color2._R && color1._G == color2._G && color1._B == color2._B;

        public static bool operator !=(Color color1, Color color2) => !(color1 == color2);

        public bool Equals(Color color) => this == color;

        public override bool Equals(object? o) => o is Color c && this == c;

        public override int GetHashCode() => HashCode.Combine(_A, _R, _G, _B);

        public override string ToString() => $"#{_A:X2}{_R:X2}{_G:X2}{_B:X2}";
    }
}
